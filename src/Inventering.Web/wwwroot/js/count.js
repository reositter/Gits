import { $, api, esc, fmt, fmtTime, showMsg, ensureLoggedIn, logout } from './common.js';

const main = $('#main');
const QUEUE_KEY = 'inv.queue';
const PREFS_KEY = 'inv.prefs';
const store = {
  get(k, d) { try { return JSON.parse(localStorage.getItem(k)) ?? d; } catch { return d; } },
  set(k, v) { try { localStorage.setItem(k, JSON.stringify(v)); } catch { /* privat läge */ } },
};
const deviceId = (() => {
  let id = store.get('inv.device'); if (!id) { id = crypto.randomUUID(); store.set('inv.device', id); } return id;
})();

let me, session, current = null, scanner = null, scanning = false, lastScan = { code: '', at: 0 };
const prefs = Object.assign({ location: '', quickMode: false, sessionId: null }, store.get(PREFS_KEY, {}));
const savePrefs = () => store.set(PREFS_KEY, prefs);

// ---------- Offline-kö ----------
// Varje rad får ett id i mobilen och läggs i kön innan den skickas. Servern ignorerar
// dubbletter, så det är ofarligt att skicka om efter tappad wifi.
const queue = {
  all: () => store.get(QUEUE_KEY, []),
  add(line) { const q = this.all(); q.push(line); store.set(QUEUE_KEY, q); renderQueue(); },
  remove(ids) { store.set(QUEUE_KEY, this.all().filter((l) => !ids.includes(l.id))); renderQueue(); },
};
let flushing = false;
async function flush() {
  if (flushing) return;
  const pending = queue.all();
  if (!pending.length) return;
  flushing = true;
  try {
    const bySession = Object.groupBy ? Object.groupBy(pending, (l) => l.sessionId) : pending.reduce((a, l) => ((a[l.sessionId] ??= []).push(l), a), {});
    for (const [sid, lines] of Object.entries(bySession)) {
      try {
        const r = await api('POST', `/api/sessions/${sid}/lines`, lines.map(({ sessionId, name, ...l }) => l));
        queue.remove(r.accepted);
        if (r.rejected.length) {
          queue.remove(r.rejected.map((x) => x.id));
          alert('Några rader avvisades av servern:\n' + r.rejected.map((x) => x.error).join('\n'));
        }
      } catch (e) {
        if (e.status === 409 || e.status === 404) {
          // Inventeringen är stängd – raderna kan aldrig sparas.
          queue.remove(lines.map((l) => l.id));
          alert(`${lines.length} rad(er) kunde inte sparas: ${e.message}`);
        } else if (e.status === 401) { location.reload(); return; }
        // Nätverksfel: försök igen senare.
      }
    }
  } finally {
    flushing = false;
    renderQueue();
    if (session) loadMyLines();
  }
}
function renderQueue() {
  const n = queue.all().length, b = $('#queueBadge');
  b.textContent = `${n} ej skickade`;
  b.classList.toggle('hidden', n === 0);
}
setInterval(flush, 10000);
window.addEventListener('online', flush);

// ---------- Start ----------
async function start() {
  me = await ensureLoggedIn(main);
  $('#logoutBtn').classList.remove('hidden');
  $('#logoutBtn').onclick = logout;
  renderQueue();
  flush();
  await chooseSession();
}

async function chooseSession() {
  const sessions = await api('GET', '/api/sessions/open');
  if (sessions.length === 1 || sessions.some((s) => s.id === prefs.sessionId)) {
    session = sessions.find((s) => s.id === prefs.sessionId) ?? sessions[0];
    return renderCount();
  }
  main.innerHTML = `
    <div class="card">
      <h2>Välj inventering</h2>
      ${sessions.length ? `<ul class="list">${sessions.map((s) => `
        <li><div class="grow"><b>${esc(s.name)}</b><div class="muted">Startad ${fmtTime(s.createdAt)}</div></div>
        <button class="primary shrink" data-id="${s.id}">Välj</button></li>`).join('')}</ul>`
        : `<p>Det finns ingen öppen inventering just nu.</p>`}
      <div class="row" style="margin-top:12px">
        <button id="reload">Uppdatera</button>
        ${me.role === 'Admin' ? `<button onclick="location.href='/admin.html'">Till admin</button>` : ''}
      </div>
    </div>`;
  main.querySelectorAll('button[data-id]').forEach((b) => b.onclick = () => {
    session = sessions.find((s) => s.id === Number(b.dataset.id));
    prefs.sessionId = session.id; savePrefs(); renderCount();
  });
  $('#reload').onclick = chooseSession;
}

// ---------- Räkning ----------
function renderCount() {
  prefs.sessionId = session.id; savePrefs();
  $('#title').textContent = session.name;
  main.innerHTML = `
    <div class="card">
      <div id="reader" class="hidden"></div>
      <div class="row" style="margin-top:8px">
        <button id="scanBtn" class="primary big">📷 Skanna streckkod</button>
      </div>
      <form id="manual" class="row" style="margin-top:12px" autocomplete="off">
        <input id="code" placeholder="Artikelnr, EAN eller sök på namn" enterkeyhint="search" autocapitalize="none">
        <button class="shrink">Sök</button>
      </form>
      <div id="results"></div>
      <div class="row" style="margin-top:8px">
        <label class="row shrink" style="margin:0;color:var(--text)"><input type="checkbox" id="quick" style="width:auto"> +1 per skanning</label>
        <input id="loc" placeholder="Plats/hylla (valfritt)" value="${esc(prefs.location)}">
      </div>
    </div>
    <form id="entry" class="card hidden" autocomplete="off">
      <div class="article" id="artName"></div>
      <div class="muted" id="artMeta"></div>
      <label for="qty">Antal</label>
      <div class="qty">
        <button type="button" id="minus">−</button>
        <input id="qty" type="number" inputmode="decimal" step="any" min="0" required>
        <button type="button" id="plus">+</button>
      </div>
      <label for="comment">Kommentar (valfritt)</label>
      <input id="comment" maxlength="500">
      <div class="row" style="margin-top:12px">
        <button type="button" id="cancelEntry">Avbryt</button>
        <button class="primary">Spara</button>
      </div>
    </form>
    <div id="flash"></div>
    <div class="card">
      <div class="row"><h2 style="margin:0">Mina rader</h2><button class="small shrink" id="switch">Byt inventering</button></div>
      <ul class="list" id="mylines"><li class="muted">Laddar…</li></ul>
    </div>`;

  $('#quick').checked = prefs.quickMode;
  $('#quick').onchange = (e) => { prefs.quickMode = e.target.checked; savePrefs(); };
  $('#loc').onchange = (e) => { prefs.location = e.target.value.trim(); savePrefs(); };
  $('#scanBtn').onclick = toggleScanner;
  $('#manual').onsubmit = (e) => { e.preventDefault(); lookup($('#code').value, 'manual'); };
  $('#minus').onclick = () => stepQty(-1);
  $('#plus').onclick = () => stepQty(1);
  $('#cancelEntry').onclick = closeEntry;
  $('#entry').onsubmit = (e) => { e.preventDefault(); saveEntry(); };
  $('#switch').onclick = () => { stopScanner(); prefs.sessionId = null; savePrefs(); session = null; $('#title').textContent = 'Inventering'; chooseSession(); };
  $('#code').focus(); // Handskannrar i tangentbordsläge skriver hit och skickar Enter.
  loadMyLines();
}

function stepQty(d) {
  const el = $('#qty');
  el.value = Math.max(0, (parseFloat(el.value.replace(',', '.')) || 0) + d);
}

async function lookup(code, source) {
  code = code.trim();
  if (!code) return;
  showMsg($('#results'), '');
  try {
    const a = await api('GET', `/api/articles/lookup?code=${encodeURIComponent(code)}`);
    found(a, code, source);
  } catch (e) {
    if (e.status !== 404) return showMsg($('#results'), e.message);
    // Ingen exakt träff → sök på namn/artikelnr (artiklar utan EAN).
    const hits = await api('GET', `/api/articles/search?q=${encodeURIComponent(code)}`).catch(() => []);
    if (!hits.length) {
      buzz([200, 100, 200]);
      return showMsg($('#results'), `Hittar ingen artikel för "${code}".`);
    }
    $('#results').innerHTML = `<ul class="list">${hits.map((a, i) => `
      <li><div class="grow"><b>${esc(a.articleNo)}</b> ${esc(a.name)}<div class="muted">${esc(a.ean ?? 'Ingen EAN')}</div></div>
      <button class="shrink" data-i="${i}">Välj</button></li>`).join('')}</ul>`;
    $('#results').querySelectorAll('button').forEach((b) => b.onclick = () => found(hits[b.dataset.i], code, 'search'));
  }
}

function found(a, code, source) {
  $('#results').innerHTML = '';
  $('#code').value = '';
  buzz(60);
  if (!a.isStockItem) return showMsg($('#results'), `${a.articleNo} ${a.name} är inte en lagerartikel.`, 'warn');
  if (prefs.quickMode && source === 'scan') {
    addLine(a, code, 1, '');
    flash(`+1 ${a.articleNo} ${a.name}`);
    return;
  }
  current = { article: a, code };
  pauseScanner();
  $('#artName').textContent = `${a.articleNo} – ${a.name}`;
  $('#artMeta').textContent = [a.ean ? `EAN ${a.ean}` : 'Ingen EAN', a.unit].filter(Boolean).join(' · ');
  $('#qty').value = '';
  $('#comment').value = '';
  $('#entry').classList.remove('hidden');
  $('#qty').focus();
}

function saveEntry() {
  const qty = parseFloat($('#qty').value.replace(',', '.'));
  if (!Number.isFinite(qty) || qty < 0) return $('#qty').focus();
  addLine(current.article, current.code, qty, $('#comment').value);
  flash(`Sparat: ${fmt(qty)} × ${current.article.articleNo} ${current.article.name}`);
  closeEntry();
}

function closeEntry() {
  current = null;
  $('#entry').classList.add('hidden');
  if (scanning) resumeScanner(); else $('#code').focus();
}

function addLine(a, code, qty, comment) {
  queue.add({
    id: crypto.randomUUID(), sessionId: session.id, articleNo: a.articleNo, name: a.name, scannedCode: code,
    quantity: qty, location: prefs.location || null, comment: comment || null,
    countedAt: new Date().toISOString(), deviceId,
  });
  flush();
}

function flash(text) {
  showMsg($('#flash'), text, 'ok');
  clearTimeout(flash.t);
  flash.t = setTimeout(() => showMsg($('#flash'), ''), 3000);
}

async function loadMyLines() {
  const el = $('#mylines');
  if (!el) return;
  const pending = queue.all().filter((l) => l.sessionId === session.id);
  let lines = [];
  try { lines = await api('GET', `/api/sessions/${session.id}/mylines`); } catch { /* offline */ }
  const total = lines.length + pending.length;
  el.innerHTML = total === 0 ? '<li class="muted">Inga rader ännu.</li>' :
    pending.map((l) => `<li><div class="grow"><b>${esc(l.articleNo)}</b> ${esc(l.name)}<div class="muted">Väntar på att skickas${l.location ? ' · ' + esc(l.location) : ''}</div></div><span class="num">${fmt(l.quantity)}</span></li>`).join('') +
    lines.map((l) => `<li><div class="grow"><b>${esc(l.articleNo)}</b> ${esc(l.name)}<div class="muted">${fmtTime(l.countedAt)}${l.location ? ' · ' + esc(l.location) : ''}${l.comment ? ' · ' + esc(l.comment) : ''}</div></div>
      <span class="num">${fmt(l.quantity)}</span><button class="small danger shrink" data-id="${l.id}" aria-label="Ta bort">✕</button></li>`).join('');
  el.querySelectorAll('button[data-id]').forEach((b) => b.onclick = async () => {
    if (!confirm('Ta bort raden?')) return;
    try { await api('DELETE', `/api/sessions/${session.id}/lines/${b.dataset.id}`); loadMyLines(); }
    catch (e) { alert(e.message); }
  });
}

// ---------- Kamera ----------
const FORMATS = () => [
  Html5QrcodeSupportedFormats.EAN_13, Html5QrcodeSupportedFormats.EAN_8,
  Html5QrcodeSupportedFormats.UPC_A, Html5QrcodeSupportedFormats.UPC_E,
  Html5QrcodeSupportedFormats.CODE_128, Html5QrcodeSupportedFormats.CODE_39,
  Html5QrcodeSupportedFormats.QR_CODE,
];

async function toggleScanner() { scanning ? stopScanner() : startScanner(); }

async function startScanner() {
  if (!window.isSecureContext) {
    return showMsg($('#results'), 'Kameran kräver HTTPS. Använd sökfältet eller en handskanner, eller öppna sidan via https://.', 'warn');
  }
  $('#reader').classList.remove('hidden');
  scanner ??= new Html5Qrcode('reader', { formatsToSupport: FORMATS(), experimentalFeatures: { useBarCodeDetectorIfSupported: true }, verbose: false });
  try {
    await scanner.start({ facingMode: 'environment' },
      { fps: 10, qrbox: (w, h) => ({ width: Math.floor(w * 0.85), height: Math.floor(Math.min(h, w) * 0.45) }) },
      onScan, () => {});
    scanning = true;
    $('#scanBtn').textContent = '■ Stoppa kamera';
  } catch (e) {
    $('#reader').classList.add('hidden');
    showMsg($('#results'), 'Kunde inte starta kameran: ' + (e?.message ?? e));
  }
}

async function stopScanner() {
  if (scanner && scanning) { try { await scanner.stop(); } catch { } }
  scanning = false;
  $('#reader')?.classList.add('hidden');
  const b = $('#scanBtn'); if (b) b.textContent = '📷 Skanna streckkod';
}
function pauseScanner() { if (scanning) try { scanner.pause(true); } catch { } }
function resumeScanner() { if (scanning) try { scanner.resume(); } catch { } }

function onScan(text) {
  // Samma kod läses många gånger per sekund – ignorera upprepning inom 2 s.
  const now = Date.now();
  if (text === lastScan.code && now - lastScan.at < 2000) return;
  lastScan = { code: text, at: now };
  if (current) return;
  lookup(text, 'scan');
}

function buzz(p) { try { navigator.vibrate?.(p); } catch { } }

document.addEventListener('visibilitychange', () => { if (document.hidden) stopScanner(); else flush(); });

start().catch((e) => { console.error(e); showMsg(main, 'Något gick fel: ' + e.message); });
