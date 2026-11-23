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
        <label class="row shrink" style="margin:0;color:var(--text)" title="Varje skanning sparas direkt som 1 st – skanna en gång per förpackning. Antalet kan ändras i efterhand."><input type="checkbox" id="quick" style="width:auto"> Snabbläge: 1 st per skanning</label>
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
        <button class="primary" id="saveEntry">Spara</button>
      </div>
    </form>
    <div id="flash"></div>
    <dialog id="submitDlg"></dialog>
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
  $('#qty').addEventListener('focus', (e) => e.target.select());
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
    $('#results').querySelectorAll('button').forEach((b) => b.onclick = () => found({ ...hits[b.dataset.i], via: 'Sökning' }, code, 'search'));
  }
}

function found(a, code, source) {
  $('#results').innerHTML = '';
  $('#code').value = '';
  buzz(60);
  if (!a.isStockItem) return showMsg($('#results'), `${a.articleNo} ${a.name} är inte en lagerartikel.`, 'warn');
  if (prefs.quickMode && source === 'scan') {
    const q = a.quantity ?? 1;
    const line = addLine(a, code, q, '');
    flash(`+${fmt(q)} ${a.articleNo} ${a.name}`, () => openEntry({ article: a, code, edit: line }));
    return;
  }
  openEntry({ article: a, code });
}

/** Öppnar antalsformuläret – för ny rad, eller för att ändra en befintlig (current.edit). */
function openEntry(c) {
  current = c;
  pauseScanner();
  const a = c.article;
  $('#artName').textContent = `${a.articleNo} – ${a.name ?? ''}`;
  $('#artMeta').textContent = c.edit ? 'Ändra antal'
    : [a.ean ? `EAN ${a.ean}` : 'Ingen EAN', a.unit, a.via && !['EAN', 'Artikelnr'].includes(a.via) ? a.via : null].filter(Boolean).join(' · ');
  $('#qty').value = c.edit ? c.edit.quantity : (a.quantity ?? 1);
  $('#comment').value = c.edit?.comment ?? '';
  $('#saveEntry').textContent = c.edit ? 'Spara ändring' : 'Spara';
  $('#entry').classList.remove('hidden');
  $('#entry').scrollIntoView({ block: 'nearest' });
  $('#qty').focus();
  $('#qty').select();
}

async function saveEntry() {
  const qty = parseFloat(String($('#qty').value).replace(',', '.'));
  if (!Number.isFinite(qty) || qty < 0) return $('#qty').focus();
  const comment = $('#comment').value.trim() || null;
  const { article, code, edit } = current;
  if (edit) {
    // Ej skickad rad ändras i kön; skickad rad ändras på servern.
    if (!queue.update(edit.id, { quantity: qty, comment })) {
      try { await api('PATCH', `/api/sessions/${session.id}/lines/${edit.id}`, { quantity: qty, comment }); }
      catch (e) { return showMsg($('#flash'), e.message); }
    }
    flash(`Ändrat: ${fmt(qty)} × ${article.articleNo} ${article.name ?? ''}`);
  } else {
    addLine(article, code, qty, comment);
    flash(`Sparat: ${fmt(qty)} × ${article.articleNo} ${article.name}`);
  }
  closeEntry();
  loadMyLines();
}

function closeEntry() {
  current = null;
  $('#entry').classList.add('hidden');
  if (scanning) resumeScanner(); else $('#code').focus();
}

function addLine(a, code, qty, comment) {
  const line = {
    id: crypto.randomUUID(), sessionId: session.id, articleNo: a.articleNo, name: a.name, scannedCode: code, scanVia: a.via ?? null,
    quantity: qty, location: prefs.location || null, comment: comment || null,
    countedAt: new Date().toISOString(), deviceId,
  };
  queue.add(line);
  flush();
  return line;
}

function flash(text, onEdit) {
  const el = $('#flash');
  el.innerHTML = `<div class="msg ok row"><span>${esc(text)}</span>${onEdit ? '<button class="small shrink" id="flashEdit">Ändra antal</button>' : ''}</div>`;
  if (onEdit) $('#flashEdit').onclick = () => { el.innerHTML = ''; onEdit(); };
  clearTimeout(flash.t);
  flash.t = setTimeout(() => { el.innerHTML = ''; }, onEdit ? 6000 : 3000);
}

async function loadMyLines() {
  const el = $('#mylines');
  if (!el) return;
  const pending = queue.all().filter((l) => l.sessionId === session.id);
  let lines = [], subs = [];
  try {
    [lines, subs] = await Promise.all([
      api('GET', `/api/sessions/${session.id}/mylines`),
      api('GET', `/api/sessions/${session.id}/mysubmissions`),
    ]);
  } catch { /* offline */ }
  const drafts = lines.filter((l) => !l.submissionId);
  const draftCount = drafts.length + pending.length;
  const lineRow = (l, opts) => `<li ${opts.edit ? `data-edit="${l.id}"` : ''}><div class="grow"><b>${esc(l.articleNo)}</b> ${esc(l.name)}
      <div class="muted">${opts.pending ? 'Väntar på uppkoppling' : fmtTime(l.countedAt)}${l.location ? ' · ' + esc(l.location) : ''}${l.comment ? ' · ' + esc(l.comment) : ''}</div></div>
      ${opts.edit ? `<button class="small shrink qtybtn">${fmt(l.quantity)} ✎</button>` : `<span class="num">${fmt(l.quantity)}</span>`}
      ${opts.del ? `<button class="small danger shrink" data-id="${l.id}" aria-label="Ta bort">✕</button>` : ''}</li>`;

  el.innerHTML = `
    <li class="muted" style="display:block">Ej inlämnade rader (${draftCount}) – kan ändras tills du lämnar in.</li>
    ${pending.map((l) => lineRow(l, { pending: true, edit: true })).join('')}
    ${drafts.map((l) => lineRow(l, { edit: true, del: true })).join('')}
    ${draftCount ? `<li style="display:block"><button class="primary big" id="submitBtn" ${pending.length ? 'disabled' : ''}>
        ${pending.length ? 'Väntar på uppkoppling…' : `✍ Signera och lämna in ${drafts.length} ${drafts.length === 1 ? 'rad' : 'rader'}`}</button></li>` : ''}
    ${subs.map((sub) => `
      <li style="display:block">
        <details>
          <summary><b>Inlämning #${sub.id}</b> · ${fmtTime(sub.submittedAt)} · ${sub.lineCount} rader · totalt ${fmt(sub.totalQuantity)}
            <div class="muted">Kvitto ${esc(sub.hash.slice(0, 12))} · låst</div></summary>
          <ul class="list">${lines.filter((l) => l.submissionId === sub.id).map((l) => lineRow(l, {})).join('')}</ul>
        </details>
      </li>`).join('')}
    ${draftCount === 0 && subs.length === 0 ? '<li class="muted">Inga rader ännu.</li>' : ''}`;

  const all = [...pending, ...drafts];
  el.querySelectorAll('li[data-edit]').forEach((li) => li.onclick = (e) => {
    if (e.target.closest('button[data-id]')) return;
    const l = all.find((x) => x.id === li.dataset.edit);
    openEntry({ article: { articleNo: l.articleNo, name: l.name }, code: l.scannedCode, edit: { id: l.id, quantity: l.quantity, comment: l.comment } });
  });
  el.querySelectorAll('button[data-id]').forEach((b) => b.onclick = async () => {
    if (!confirm('Ta bort raden?')) return;
    try { await api('DELETE', `/api/sessions/${session.id}/lines/${b.dataset.id}`); loadMyLines(); }
    catch (e) { alert(e.message); }
  });
  $('#submitBtn')?.addEventListener('click', () => submitDialog(drafts));
}

/** Signera inlämning: visar exakt vilka rader som lämnas in och kräver lösenord. */
function submitDialog(drafts) {
  stopScanner();
  const total = drafts.reduce((n, l) => n + Number(l.quantity), 0);
  const dlg = $('#submitDlg');
  dlg.innerHTML = `
    <form id="subForm">
      <h2>Lämna in räkning</h2>
      <p>Du lämnar in <b>${drafts.length} rader</b> (totalt ${fmt(total)}) i <b>${esc(session.name)}</b>.
      Efter inlämning är raderna låsta och kan bara ändras av admin.</p>
      <div class="table-wrap" style="max-height:40vh;overflow:auto"><table>
        <thead><tr><th>Artikel</th><th>Plats</th><th class="n">Antal</th></tr></thead>
        <tbody>${drafts.map((l) => `<tr><td><b>${esc(l.articleNo)}</b> ${esc(l.name)}</td><td>${esc(l.location)}</td><td class="n">${fmt(l.quantity)}</td></tr>`).join('')}</tbody>
      </table></div>
      <label for="subComment">Kommentar (valfritt)</label>
      <input id="subComment" maxlength="500">
      <label for="subPw">Ditt lösenord – signerar att räkningen är korrekt</label>
      <input id="subPw" type="password" autocomplete="current-password" required>
      <div id="subMsg"></div>
      <div class="row" style="margin-top:12px">
        <button type="button" id="subCancel">Avbryt</button>
        <button class="primary" id="subOk">Signera och lämna in</button>
      </div>
    </form>`;
  dlg.showModal();
  $('#subPw', dlg).focus();
  $('#subCancel', dlg).onclick = () => dlg.close();
  $('#subForm', dlg).onsubmit = async (e) => {
    e.preventDefault();
    $('#subOk', dlg).disabled = true;
    try {
      const r = await api('POST', `/api/sessions/${session.id}/submit`, {
        password: $('#subPw', dlg).value, lineIds: drafts.map((l) => l.id), deviceId, comment: $('#subComment', dlg).value || null,
      });
      dlg.innerHTML = `<h2>Inlämnat ✔</h2>
        <p><b>Inlämning #${r.id}</b><br>${fmtTime(r.submittedAt)} · ${r.lineCount} rader · totalt ${fmt(r.totalQuantity)}</p>
        <p class="muted">Kvitto: <code>${esc(r.hash.slice(0, 12))}</code></p>
        <button class="primary big" id="subDone">OK</button>`;
      $('#subDone', dlg).onclick = () => dlg.close();
      buzz([60, 60, 60]);
      loadMyLines();
    } catch (err) {
      showMsg($('#subMsg', dlg), err.status === 409 ? err.message + ' ' : err.message);
      $('#subOk', dlg).disabled = false;
      if (err.status === 409) { dlg.close(); loadMyLines(); alert(err.message); }
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
