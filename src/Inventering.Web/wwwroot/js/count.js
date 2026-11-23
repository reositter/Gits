import { $, api, esc, fmt, fmtTime, showMsg, ensureLoggedIn, logout, icon } from './common.js';

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
let view = { drafts: [], pending: [], subs: [], lines: [] };
const prefs = Object.assign({ location: '', quickMode: false, sessionId: null }, store.get(PREFS_KEY, {}));
const savePrefs = () => store.set(PREFS_KEY, prefs);
const time = (t) => t ? new Date(t).toLocaleTimeString('sv-SE', { hour: '2-digit', minute: '2-digit' }) : '';
const plural = (n, one, many) => `${fmt(n)} ${n === 1 ? one : many}`;

// ---------- Offline-kö ----------
// Varje rad får ett id i mobilen och läggs i kön innan den skickas. Servern ignorerar
// dubbletter, så det är ofarligt att skicka om efter tappad wifi.
const queue = {
  all: () => store.get(QUEUE_KEY, []),
  add(line) { const q = this.all(); q.push(line); store.set(QUEUE_KEY, q); renderQueue(); },
  update(id, fields) {
    const q = this.all(); const l = q.find((x) => x.id === id);
    if (!l) return false;
    Object.assign(l, fields); store.set(QUEUE_KEY, q); return true;
  },
  remove(ids) { store.set(QUEUE_KEY, this.all().filter((l) => !ids.includes(l.id))); renderQueue(); },
};
let flushing = false;
async function flush() {
  if (flushing) return;
  const pending = queue.all();
  if (!pending.length) return;
  flushing = true;
  try {
    const bySession = pending.reduce((a, l) => ((a[l.sessionId] ??= []).push(l), a), {});
    for (const [sid, lines] of Object.entries(bySession)) {
      try {
        const r = await api('POST', `/api/sessions/${sid}/lines`, lines.map(({ sessionId, name, unit, ...l }) => l));
        queue.remove(r.accepted);
        if (r.rejected.length) {
          queue.remove(r.rejected.map((x) => x.id));
          toast('Rader avvisades: ' + r.rejected.map((x) => x.error).join(', '));
        }
      } catch (e) {
        if (e.status === 409 || e.status === 404) {
          // Inventeringen är stängd – raderna kan aldrig sparas.
          queue.remove(lines.map((l) => l.id));
          toast(`${plural(lines.length, 'rad', 'rader')} kunde inte sparas: ${e.message}`);
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
  b.innerHTML = `${icon('wifiOff', 15)}${n}`;
  b.title = `${n} rader väntar på uppkoppling`;
  b.classList.toggle('hidden', n === 0);
}
setInterval(flush, 10000);
window.addEventListener('online', flush);

// Byt inte bolag medan rader väntar på att skickas – de hör till inventeringen i nuvarande bolag.
window.beforeCompanySwitch = () => {
  if (queue.all().length) { toast('Rader väntar på uppkoppling. Byt bolag när de har skickats.'); return false; }
  stopScanner();
  prefs.sessionId = null; savePrefs();
  return true;
};

// ---------- Ark (bottom sheet) och kvittens ----------
const sheet = document.createElement('dialog');
sheet.className = 'm-sheet';
document.body.append(sheet);
sheet.addEventListener('click', (e) => { if (e.target === sheet) closeSheet(); }); // klick på bakgrunden
sheet.addEventListener('cancel', (e) => { e.preventDefault(); closeSheet(); });
let onSheetClose = null;
function openSheet(html, onClose) {
  sheet.innerHTML = `<div class="m-grab"></div>${html}`;
  onSheetClose = onClose ?? null;
  if (!sheet.open) sheet.showModal();
}
function closeSheet() {
  if (sheet.open) sheet.close();
  const f = onSheetClose; onSheetClose = null; f?.();
}

const toastEl = document.createElement('div');
toastEl.className = 'm-toast'; toastEl.hidden = true; toastEl.setAttribute('role', 'status');
document.body.append(toastEl);
function toast(text, action) {
  toastEl.innerHTML = `<span>${esc(text)}</span>${action ? `<button type="button">${esc(action.label)}</button>` : ''}`;
  if (action) toastEl.querySelector('button').onclick = () => { toastEl.hidden = true; action.run(); };
  toastEl.hidden = false;
  clearTimeout(toast.t);
  toast.t = setTimeout(() => { toastEl.hidden = true; }, action ? 5000 : 2800);
}

// ---------- Start ----------
async function start() {
  me = await ensureLoggedIn(main);
  $('#logoutBtn').onclick = logout;
  const menu = $('#menuBtn');
  menu.innerHTML = icon('more', 20);
  menu.classList.remove('hidden');
  menu.onclick = openMenu;
  renderQueue();
  flush();
  await chooseSession();
}

function openMenu() {
  const canSwitch = me.companies.length > 1;
  openSheet(`
    <h2>${esc(me.displayName || me.userName)}</h2>
    <div class="m-sub">${esc(me.company?.name ?? '')}</div>
    <ul class="m-menu" style="margin-top:12px">
      ${session ? `<li><button data-a="session">${icon('file', 20)}Byt inventering</button></li>` : ''}
      ${canSwitch ? `<li><button data-a="company">${icon('building', 20)}Byt bolag</button></li>` : ''}
      ${me.role === 'Admin' ? `<li><button data-a="admin">${icon('sheet', 20)}Administration</button></li>` : ''}
      <li><button data-a="logout" class="danger">${icon('logout', 20)}Logga ut</button></li>
    </ul>`);
  sheet.querySelectorAll('button[data-a]').forEach((b) => b.onclick = () => {
    const a = b.dataset.a;
    closeSheet();
    if (a === 'session') { stopScanner(); prefs.sessionId = null; savePrefs(); session = null; chooseSession(true); }
    if (a === 'company') $('#companyBtn').click();
    if (a === 'admin') location.href = '/admin.html';
    if (a === 'logout') logout();
  });
}

async function chooseSession(force = false) {
  const sessions = await api('GET', '/api/sessions/open');
  if (!force && (sessions.length === 1 || sessions.some((s) => s.id === prefs.sessionId))) {
    session = sessions.find((s) => s.id === prefs.sessionId) ?? sessions[0];
    return renderCount();
  }
  main.innerHTML = `
    <div class="m-hero"><h1>Inventeringar</h1><p class="m-meta">${esc(me.company?.name ?? '')}</p></div>
    ${sessions.length ? `<ul class="m-list">${sessions.map((s) => `
      <li class="m-row" data-id="${s.id}"><div class="m-row-main"><div class="m-row-t">${esc(s.name)}</div>
      <div class="m-row-s">Startad ${fmtTime(s.createdAt)}</div></div><span class="chev">${icon('chevron', 18)}</span></li>`).join('')}</ul>`
      : `<div class="m-empty">Det finns ingen pågående inventering i ${esc(me.company?.name ?? 'bolaget')} just nu.</div>`}
    <div class="m-dock"><button class="m-btn m-btn-secondary" id="reload" style="flex:1">${icon('refresh', 18)}Uppdatera</button></div>`;
  main.querySelectorAll('[data-id]').forEach((li) => li.onclick = () => {
    session = sessions.find((s) => s.id === Number(li.dataset.id));
    prefs.sessionId = session.id; savePrefs(); renderCount();
  });
  $('#reload').onclick = () => chooseSession(force);
}

// ---------- Räkning ----------
function renderCount() {
  prefs.sessionId = session.id; savePrefs();
  main.innerHTML = `
    <div class="m-hero"><h1>${esc(session.name)}</h1><p class="m-meta" id="meta">&nbsp;</p></div>
    <form id="manual" class="m-search" autocomplete="off" role="search">
      ${icon('search', 18)}
      <input id="code" placeholder="Sök artikel eller skriv EAN" enterkeyhint="search" autocapitalize="none" autocorrect="off" spellcheck="false">
      <button type="submit" id="searchBtn" hidden>Sök</button>
    </form>
    <div class="m-chips">
      <button type="button" class="m-chip" id="locChip">${icon('pin', 15)}<span></span></button>
      <button type="button" class="m-chip" id="quickChip">${icon('bolt', 15)}Snabbläge</button>
    </div>
    <div id="reader" class="m-reader hidden"></div>
    <div id="results"></div>
    <div id="lists"><div class="m-empty">Laddar…</div></div>
    <div class="m-dock">
      <button type="button" class="m-btn m-btn-secondary hidden" id="submitBtn"></button>
      <button type="button" class="m-btn m-btn-primary" id="scanBtn">${icon('camera', 20)}Skanna</button>
    </div>`;

  const code = $('#code');
  code.addEventListener('input', () => { $('#searchBtn').hidden = !code.value; });
  $('#manual').onsubmit = (e) => { e.preventDefault(); lookup(code.value, 'manual'); code.blur(); };
  $('#scanBtn').onclick = toggleScanner;
  $('#submitBtn').onclick = submitSheet;
  $('#locChip').onclick = locationSheet;
  $('#quickChip').onclick = () => {
    prefs.quickMode = !prefs.quickMode; savePrefs(); renderChips();
    toast(prefs.quickMode ? 'Snabbläge: varje skanning sparas direkt som 1 st.' : 'Snabbläge av: du anger antal efter varje skanning.');
  };
  renderChips();
  loadMyLines();
}

function renderChips() {
  $('#locChip span').textContent = prefs.location ? `Plats ${prefs.location}` : 'Ange plats';
  $('#locChip').setAttribute('aria-pressed', String(!!prefs.location));
  $('#quickChip').setAttribute('aria-pressed', String(!!prefs.quickMode));
}

function locationSheet() {
  openSheet(`
    <form id="lf">
      <h2>Plats</h2>
      <div class="m-sub">Hylla eller lagerplats du räknar på. Sparas på varje rad och hjälper till att hitta dubbelräkning.</div>
      <label for="lv">Plats</label>
      <input id="lv" value="${esc(prefs.location)}" placeholder="t.ex. A3" autocapitalize="characters">
      <div class="m-actions">
        <button class="m-btn m-btn-primary">Använd</button>
        ${prefs.location ? `<button type="button" class="m-btn m-btn-plain" id="lclear">Ingen plats</button>` : ''}
      </div>
    </form>`);
  $('#lv', sheet).focus();
  $('#lf', sheet).onsubmit = (e) => { e.preventDefault(); prefs.location = $('#lv', sheet).value.trim(); savePrefs(); renderChips(); closeSheet(); };
  $('#lclear', sheet)?.addEventListener('click', () => { prefs.location = ''; savePrefs(); renderChips(); closeSheet(); });
}

async function lookup(code, source) {
  code = code.trim();
  if (!code) return;
  $('#results').innerHTML = '';
  try {
    const a = await api('GET', `/api/articles/lookup?code=${encodeURIComponent(code)}`);
    found(a, code, source);
  } catch (e) {
    if (e.status !== 404) return note(e.message, true);
    // Ingen exakt träff: sök på namn/artikelnr (artiklar utan streckkod).
    const hits = await api('GET', `/api/articles/search?q=${encodeURIComponent(code)}`).catch(() => []);
    if (!hits.length) { buzz([200, 100, 200]); return note(`Ingen artikel matchar "${code}".`, true); }
    $('#results').innerHTML = `
      <div class="m-section"><div class="m-section-h"><b>Sökresultat</b><span>${hits.length}</span></div>
      <ul class="m-list">${hits.map((a, i) => `
        <li class="m-row m-hit" data-i="${i}"><div class="m-row-main"><div class="m-row-t">${esc(a.name)}</div>
        <div class="m-row-s">${esc(a.articleNo)}${a.ean ? ' · ' + esc(a.ean) : ''}</div></div><span class="chev">${icon('chevron', 18)}</span></li>`).join('')}</ul></div>`;
    $('#results').querySelectorAll('[data-i]').forEach((li) => li.onclick = () => found({ ...hits[li.dataset.i], via: 'Sökning' }, code, 'search'));
  }
}

function note(text, bad) { $('#results').innerHTML = `<div class="m-note ${bad ? 'bad' : ''}">${esc(text)}</div>`; }

function found(a, code, source) {
  $('#results').innerHTML = '';
  $('#code').value = ''; $('#searchBtn').hidden = true;
  buzz(40);
  if (!a.isStockItem) return note(`${a.articleNo} ${a.name} är inte en lagerartikel.`, true);
  if (prefs.quickMode && source === 'scan') {
    const q = a.quantity ?? 1;
    const line = addLine(a, code, q, '');
    toast(`+${fmt(q)}  ${a.name}`, { label: 'Ändra', run: () => openEntry({ article: a, code, edit: line }) });
    return;
  }
  openEntry({ article: a, code });
}

/** Ark för antal – ny rad, eller ändra en befintlig (c.edit). */
function openEntry(c) {
  current = c;
  pauseScanner();
  const a = c.article;
  const qty = c.edit ? c.edit.quantity : (a.quantity ?? 1);
  const via = a.via && !['EAN', 'Artikelnr', 'Sökning'].includes(a.via) ? a.via : null;
  openSheet(`
    <form id="entry" autocomplete="off">
      <h2>${esc(a.name ?? '')}</h2>
      <div class="m-sub">${[esc(a.articleNo), a.ean ? 'EAN ' + esc(a.ean) : null, prefs.location && !c.edit ? 'Plats ' + esc(prefs.location) : null, via ? esc(via) : null].filter(Boolean).join(' · ')}</div>
      <div class="m-stepper">
        <button type="button" id="minus" aria-label="Minska">${icon('minus', 22)}</button>
        <input id="qty" type="number" inputmode="decimal" step="any" min="0" value="${qty}" required aria-label="Antal">
        <button type="button" id="plus" aria-label="Öka">${icon('plus', 22)}</button>
      </div>
      <div class="m-unit">${esc(a.unit ?? 'st')}</div>
      <div id="commentWrap" ${c.edit?.comment ? '' : 'hidden'}><label for="comment">Kommentar</label><input id="comment" maxlength="500" value="${esc(c.edit?.comment ?? '')}"></div>
      ${c.edit?.comment ? '' : `<button type="button" class="m-textlink" id="addComment">Lägg till kommentar</button>`}
      <div class="m-actions">
        <button class="m-btn m-btn-primary" id="saveEntry">${c.edit ? 'Spara ändring' : 'Spara'}</button>
        ${c.edit && !c.edit.pending ? `<button type="button" class="m-btn m-btn-danger" id="delEntry">Ta bort raden</button>` : ''}
      </div>
    </form>`, () => { current = null; if (scanning) resumeScanner(); });
  const q = $('#qty', sheet);
  q.focus(); q.select();
  $('#minus', sheet).onclick = () => step(-1);
  $('#plus', sheet).onclick = () => step(1);
  $('#addComment', sheet)?.addEventListener('click', (e) => { e.target.remove(); $('#commentWrap', sheet).hidden = false; $('#comment', sheet).focus(); });
  $('#entry', sheet).onsubmit = (e) => { e.preventDefault(); saveEntry(); };
  $('#delEntry', sheet)?.addEventListener('click', () => deleteLine(c.edit.id));
}

function step(d) {
  const el = $('#qty', sheet);
  el.value = Math.max(0, (parseFloat(String(el.value).replace(',', '.')) || 0) + d);
}

async function saveEntry() {
  const qty = parseFloat(String($('#qty', sheet).value).replace(',', '.'));
  if (!Number.isFinite(qty) || qty < 0) return $('#qty', sheet).focus();
  const comment = $('#comment', sheet)?.value.trim() || null;
  const { article, code, edit } = current;
  if (edit) {
    // Ej skickad rad ändras i kön; skickad rad ändras på servern.
    if (!queue.update(edit.id, { quantity: qty, comment })) {
      try { await api('PATCH', `/api/sessions/${session.id}/lines/${edit.id}`, { quantity: qty, comment }); }
      catch (e) { closeSheet(); return toast(e.message); }
    }
    toast(`Ändrat till ${fmt(qty)}  ${article.name ?? ''}`);
  } else {
    addLine(article, code, qty, comment);
    toast(`Sparat ${fmt(qty)}  ${article.name}`);
  }
  closeSheet();
  loadMyLines();
}

async function deleteLine(id) {
  try { await api('DELETE', `/api/sessions/${session.id}/lines/${id}`); closeSheet(); toast('Raden togs bort'); loadMyLines(); }
  catch (e) { closeSheet(); toast(e.message); }
}

function addLine(a, code, qty, comment) {
  const line = {
    id: crypto.randomUUID(), sessionId: session.id, articleNo: a.articleNo, name: a.name, unit: a.unit, scannedCode: code, scanVia: a.via ?? null,
    quantity: qty, location: prefs.location || null, comment: comment || null,
    countedAt: new Date().toISOString(), deviceId,
  };
  queue.add(line);
  flush();
  return line;
}

async function loadMyLines() {
  const el = $('#lists');
  if (!el) return;
  const pending = queue.all().filter((l) => l.sessionId === session.id);
  let lines = [], subs = [];
  try {
    [lines, subs] = await Promise.all([
      api('GET', `/api/sessions/${session.id}/mylines`),
      api('GET', `/api/sessions/${session.id}/mysubmissions`),
    ]);
  } catch { /* offline – visa det vi har */ }
  const drafts = lines.filter((l) => !l.submissionId);
  view = { drafts, pending, subs, lines };
  const draftCount = drafts.length + pending.length;

  const row = (l, pendingRow) => `
    <li class="m-row ${pendingRow ? 'pending' : ''}" data-edit="${l.id}">
      <div class="m-row-main"><div class="m-row-t">${esc(l.name ?? l.articleNo)}</div>
      <div class="m-row-s">${[esc(l.articleNo), l.location ? esc(l.location) : null, pendingRow ? 'väntar på uppkoppling' : time(l.countedAt), l.comment ? esc(l.comment) : null].filter(Boolean).join(' · ')}</div></div>
      <div class="m-qty">${fmt(l.quantity)}</div>
    </li>`;

  el.innerHTML = `
    ${draftCount ? `
      <div class="m-section"><div class="m-section-h"><b>Att lämna in</b><span>tryck på en rad för att ändra</span></div>
      <ul class="m-list">${pending.map((l) => row(l, true)).join('')}${drafts.map((l) => row(l, false)).join('')}</ul></div>` : ''}
    ${subs.length ? `
      <div class="m-section"><div class="m-section-h"><b>Inlämnat</b><span>${plural(subs.length, 'inlämning', 'inlämningar')}</span></div>
      <ul class="m-list">${subs.map((s) => `
        <li class="m-row" data-sub="${s.id}"><div class="m-row-main"><div class="m-row-t">Inlämning ${s.id}</div>
        <div class="m-row-s">${fmtTime(s.submittedAt)} · ${plural(s.lineCount, 'rad', 'rader')}</div></div>
        <div class="m-qty">${fmt(s.totalQuantity)}</div><span class="chev">${icon('chevron', 18)}</span></li>`).join('')}</ul></div>` : ''}
    ${!draftCount && !subs.length ? `<div class="m-empty">Skanna en streckkod eller sök efter en artikel för att börja räkna.</div>` : ''}`;

  const all = [...pending, ...drafts];
  el.querySelectorAll('[data-edit]').forEach((li) => li.onclick = () => {
    const l = all.find((x) => x.id === li.dataset.edit);
    openEntry({ article: { articleNo: l.articleNo, name: l.name, unit: l.unit }, code: l.scannedCode,
      edit: { id: l.id, quantity: l.quantity, comment: l.comment, pending: pending.includes(l) } });
  });
  el.querySelectorAll('[data-sub]').forEach((li) => li.onclick = () => receiptSheet(subs.find((s) => s.id === Number(li.dataset.sub)), lines));

  const total = lines.length + pending.length;
  $('#meta').innerHTML = total ? `${plural(total, 'rad', 'rader')}${draftCount ? ` · <b>${fmt(draftCount)} att lämna in</b>` : ' · allt inlämnat'}` : 'Inga rader ännu';
  const sb = $('#submitBtn');
  sb.classList.toggle('hidden', draftCount === 0);
  sb.disabled = pending.length > 0;
  sb.innerHTML = pending.length ? `${icon('wifiOff', 18)}Väntar` : `Lämna in ${fmt(drafts.length)}`;
}

function receiptSheet(s, lines) {
  const mine = lines.filter((l) => l.submissionId === s.id);
  openSheet(`
    <h2>Inlämning ${s.id}</h2>
    <div class="m-sub">${fmtTime(s.submittedAt)} · ${plural(s.lineCount, 'rad', 'rader')} · totalt ${fmt(s.totalQuantity)} · låst</div>
    <ul class="m-table">${mine.map((l) => `<li><span>${esc(l.name ?? l.articleNo)}</span><span>${fmt(l.quantity)}</span></li>`).join('')}</ul>
    <p class="m-sub" style="margin-top:12px">Kvitto <code>${esc(s.hash.slice(0, 12))}</code>. Kontakta administratören om något behöver ändras.</p>
    <div class="m-actions"><button class="m-btn m-btn-secondary" id="ok">Stäng</button></div>`);
  $('#ok', sheet).onclick = closeSheet;
}

/** Signera inlämning: visar exakt vilka rader som lämnas in och kräver lösenord. */
function submitSheet() {
  const drafts = view.drafts;
  if (!drafts.length) return;
  stopScanner();
  const total = drafts.reduce((n, l) => n + Number(l.quantity), 0);
  openSheet(`
    <form id="subForm">
      <h2>Lämna in ${plural(drafts.length, 'rad', 'rader')}</h2>
      <div class="m-sub">Totalt ${fmt(total)} i ${esc(session.name)}. Efter inlämning är raderna låsta och kan bara ändras av administratören.</div>
      <ul class="m-table">${drafts.map((l) => `<li><span>${esc(l.name ?? l.articleNo)}${l.location ? ` <span class="m-sub">· ${esc(l.location)}</span>` : ''}</span><span>${fmt(l.quantity)}</span></li>`).join('')}</ul>
      <label for="subComment">Kommentar (valfritt)</label>
      <input id="subComment" maxlength="500" placeholder="t.ex. Gång A klar">
      <label for="subPw">Ditt lösenord</label>
      <input id="subPw" type="password" autocomplete="current-password" required>
      <div id="subMsg"></div>
      <div class="m-actions"><button class="m-btn m-btn-primary" id="subOk">Signera och lämna in</button></div>
    </form>`);
  $('#subPw', sheet).focus();
  $('#subForm', sheet).onsubmit = async (e) => {
    e.preventDefault();
    $('#subOk', sheet).disabled = true;
    try {
      const r = await api('POST', `/api/sessions/${session.id}/submit`, {
        password: $('#subPw', sheet).value, lineIds: drafts.map((l) => l.id), deviceId, comment: $('#subComment', sheet).value || null,
      });
      buzz([40, 60, 40]);
      openSheet(`
        <div class="m-receipt">
          <div class="ok">${icon('check', 28)}</div>
          <h2>Inlämnat</h2>
          <div class="m-sub">Inlämning ${r.id} · ${plural(r.lineCount, 'rad', 'rader')} · totalt ${fmt(r.totalQuantity)}</div>
          <p><code>Kvitto ${esc(r.hash.slice(0, 12))}</code></p>
        </div>
        <div class="m-actions"><button class="m-btn m-btn-primary" id="subDone">Klar</button></div>`);
      $('#subDone', sheet).onclick = closeSheet;
      loadMyLines();
    } catch (err) {
      if (err.status === 409) { closeSheet(); loadMyLines(); toast(err.message); return; }
      showMsg($('#subMsg', sheet), err.message);
      $('#subOk', sheet).disabled = false;
    }
  };
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
  if (!window.isSecureContext) return note('Kameran kräver en säker anslutning (https). Använd sökfältet eller en handskanner så länge.', true);
  $('#reader').classList.remove('hidden');
  scanner ??= new Html5Qrcode('reader', { formatsToSupport: FORMATS(), experimentalFeatures: { useBarCodeDetectorIfSupported: true }, verbose: false });
  try {
    await scanner.start({ facingMode: 'environment' },
      { fps: 10, qrbox: (w, h) => ({ width: Math.floor(w * 0.85), height: Math.floor(Math.min(h, w) * 0.45) }) },
      onScan, () => {});
    scanning = true;
    $('#scanBtn').innerHTML = `${icon('stop', 20)}Stoppa`;
    window.scrollTo({ top: 0, behavior: 'smooth' });
  } catch (e) {
    $('#reader').classList.add('hidden');
    note('Kunde inte starta kameran: ' + (e?.message ?? e), true);
  }
}

async function stopScanner() {
  if (scanner && scanning) { try { await scanner.stop(); } catch { } }
  scanning = false;
  $('#reader')?.classList.add('hidden');
  const b = $('#scanBtn'); if (b) b.innerHTML = `${icon('camera', 20)}Skanna`;
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

// Handskannrar i tangentbordsläge "skriver" koden snabbt och avslutar med Enter. Fånga det även
// när inget fält har fokus, så att mobilens tangentbord inte behöver vara uppe.
let wedge = { buf: '', at: 0 };
document.addEventListener('keydown', (e) => {
  if (!session || sheet.open || e.target.closest?.('input, textarea, select')) return;
  const now = Date.now();
  if (now - wedge.at > 80) wedge.buf = '';
  wedge.at = now;
  if (e.key === 'Enter') {
    if (wedge.buf.length >= 3) { e.preventDefault(); lookup(wedge.buf, 'scan'); }
    wedge.buf = '';
  } else if (e.key.length === 1) wedge.buf += e.key;
});

function buzz(p) { try { navigator.vibrate?.(p); } catch { } }

document.addEventListener('visibilitychange', () => { if (document.hidden) stopScanner(); else flush(); });

start().catch((e) => { console.error(e); showMsg(main, 'Något gick fel: ' + e.message); });
