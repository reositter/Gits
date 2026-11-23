import { $, api, esc, fmt, fmtTime, showMsg, ensureLoggedIn, logout, icon } from './common.js';

const main = $('#main');
const dlg = $('#dlg');
const STATUS = { Open: 'Öppen', Locked: 'Låst', Sent: 'Skickad', Cancelled: 'Avbruten' };
const pill = (s) => `<span class="pill ${s}">${STATUS[s] ?? s}</span>`;
const signed = (n) => n > 0 ? `<span class="pos">+${fmt(n)}</span>` : n < 0 ? `<span class="neg">${fmt(n)}</span>` : '0';
const SIGNED_WITH = { password: 'Lösenord', 'excel-import': 'Excel-import', 'admin-correction': 'Admin-korrigering' };
let me;

async function start() {
  me = await ensureLoggedIn(main, { requireAdmin: true });
  $('#logoutBtn').classList.remove('hidden');
  $('#logoutBtn').onclick = logout;
  $('#subnav').classList.remove('hidden');
  $('#tabs').querySelectorAll('button').forEach((b) => b.onclick = () => go(b.dataset.tab));
  window.addEventListener('hashchange', route);
  route();
}

function go(tab) { location.hash = tab; }
function route() {
  let [tab, id] = (location.hash.slice(1) || 'sessions').split('/');
  // Utan valt bolag finns bara de bolagsoberoende flikarna.
  if (!me.company && !['companies', 'users', 'audit'].includes(tab)) tab = 'companies';
  $('#tabs').querySelectorAll('button').forEach((b) => b.classList.toggle('active', b.dataset.tab === tab));
  const r = { sessions: () => id ? sessionView(Number(id)) : sessionsView(), barcodes: barcodesView, companies: companiesView, users: usersView, audit: auditView }[tab] ?? sessionsView;
  r().catch((e) => showMsg(main, e.message));
}

const pageHead = (title, sub = '', actions = '') => `
  <div class="page-head"><div><h1>${title}</h1>${sub ? `<div class="sub">${sub}</div>` : ''}</div>${actions ? `<div class="toolbar">${actions}</div>` : ''}</div>`;

// ---------- Inventeringar ----------
async function sessionsView() {
  const [sessions, status] = await Promise.all([api('GET', '/api/admin/sessions'), api('GET', '/api/admin/status')]);
  const active = sessions.some((s) => s.status === 'Open' || s.status === 'Locked');
  main.innerHTML = `
    ${pageHead('Inventeringar', esc(status.company.name))}
    <div class="grid-2">
      <div class="card">
        <h2>Ny inventering</h2>
        ${active ? `<p class="muted" style="margin:0">Det finns redan en pågående inventering. Skicka eller avbryt den innan du startar en ny.</p>` : `
        <form id="newForm" class="row">
          <input id="newName" placeholder="t.ex. Årsinventering ${new Date().getFullYear()}" required>
          <button class="primary shrink">${icon('plus', 16)}Starta</button>
        </form>
        <p class="hint">Lagersaldot i Visma sparas när inventeringen startas och används som jämförelse.</p>`}
        <div id="nmsg"></div>
      </div>
      <div class="card">
        <div class="card-head" style="margin-bottom:10px"><h2>Visma-koppling</h2>
          <div class="toolbar"><button id="checkVisma" class="small">Kontrollera</button><button id="refresh" class="small">${icon('refresh', 14)}Uppdatera artiklar</button></div></div>
        <div class="muted"><code>${esc(status.company.companyPath)}</code></div>
        <div class="muted" style="margin-top:4px">${status.articles} artiklar i cache · uppdaterad ${fmtTime(status.lastRefresh) || 'aldrig'} <span id="rmsg"></span></div>
        ${status.lastError ? `<div class="msg bad">${icon('alert', 16)}<div class="grow">${esc(status.lastError)}</div></div>` : ''}
        <div id="vstatus"></div>
      </div>
    </div>
    <div class="card flush">
      <div class="card-head"><h2>Alla inventeringar</h2></div>
      <div class="table-wrap"><table>
        <thead><tr><th>Nr</th><th>Namn</th><th>Status</th><th>Startad</th><th class="n">Artiklar</th><th class="n">Rader</th><th>Skickad</th><th></th></tr></thead>
        <tbody>${sessions.map((s) => `<tr class="clickable" data-id="${s.id}"><td class="secondary">#${s.id}</td><td><b>${esc(s.name)}</b></td><td>${pill(s.status)}</td>
          <td>${fmtTime(s.createdAt)}</td><td class="n">${s.articles}</td><td class="n">${s.lines}</td><td>${fmtTime(s.sentAt) || '<span class="secondary">–</span>'}</td>
          <td class="actions secondary">${icon('chevron', 16)}</td></tr>`).join('')
          || '<tr><td colspan="8" class="empty">Inga inventeringar ännu.</td></tr>'}</tbody>
      </table></div>
    </div>`;
  main.querySelectorAll('tr[data-id]').forEach((tr) => tr.onclick = () => go(`sessions/${tr.dataset.id}`));
  $('#refresh').onclick = async () => {
    $('#rmsg').textContent = '· hämtar…';
    try { const r = await api('POST', '/api/admin/articles/refresh'); $('#rmsg').textContent = `· ${r.count} artiklar hämtade`; }
    catch (e) { $('#rmsg').textContent = '· ' + e.message; }
  };
  $('#checkVisma').onclick = async () => {
    showMsg($('#vstatus'), 'Kontrollerar…', 'warn');
    try {
      const v = await api('GET', '/api/admin/visma-status');
      showMsg($('#vstatus'), `${v.details ?? v.name}` +
        (v.writesToInventoryList ? ` · Inventeringsunderlag: ${v.inventoryListRows ? v.inventoryListRows + ' artiklar' : 'saknas – skapa det i Visma innan du skickar'}` : ''),
        v.writesToInventoryList && !v.inventoryListRows ? 'warn' : 'ok');
    } catch (e) { showMsg($('#vstatus'), e.message); }
  };
  $('#newForm')?.addEventListener('submit', async (e) => {
    e.preventDefault();
    try { const r = await api('POST', '/api/admin/sessions', { name: $('#newName').value }); go(`sessions/${r.id}`); }
    catch (err) { showMsg($('#nmsg'), err.message); }
  });
}

let filter = 'all';
async function sessionView(id) {
  const [d, subs] = await Promise.all([api('GET', `/api/admin/sessions/${id}/summary`), api('GET', `/api/admin/sessions/${id}/submissions`)]);
  const draftTotal = d.drafts.reduce((n, x) => n + x.lines, 0);
  const s = d.session;
  const arts = d.articles;
  const warnCount = arts.filter((a) => a.warnings.length).length;
  const diffCount = arts.filter((a) => a.difference !== 0).length;
  const shown = filter === 'warn' ? arts.filter((a) => a.warnings.length) : filter === 'diff' ? arts.filter((a) => a.difference !== 0) : arts;
  const net = arts.reduce((n, a) => n + a.difference, 0);
  const editable = s.status === 'Open' || s.status === 'Locked';

  main.innerHTML = `
    <a class="back" href="#sessions">${icon('back', 16)}Inventeringar</a>
    ${pageHead(`${esc(s.name)} ${pill(s.status)}`,
      `#${s.id} · startad ${fmtTime(s.createdAt)}${s.lockedAt ? ' · låst ' + fmtTime(s.lockedAt) : ''}${s.sentAt ? ' · skickad ' + fmtTime(s.sentAt) : ''}`,
      `<a class="btn" href="/api/admin/sessions/${s.id}/report.html" target="_blank" rel="noopener">${icon('file', 16)}Rapport</a>
       <a class="btn" href="/api/admin/sessions/${s.id}/report.xlsx">${icon('download', 16)}Rapport (Excel)</a>
       <span class="sep"></span>
       ${s.status === 'Open' ? `<button data-act="lock" class="primary">${icon('lock', 16)}Lås räkningen</button>` : ''}
       ${s.status === 'Locked' ? `<button data-act="reopen">${icon('unlock', 16)}Öppna igen</button><button id="sendBtn" class="primary">${icon('send', 16)}Godkänn och skicka</button>` : ''}
       ${editable ? `<button data-act="cancel" class="danger">Avbryt</button>` : ''}`)}
    <div id="smsg"></div>
    ${s.status === 'Open' ? `<div class="msg warn">${icon('alert', 16)}<div class="grow">Räkning pågår. Lås inventeringen när alla har lämnat in, för att granska och skicka.</div></div>` : ''}
    ${draftTotal ? `<div class="msg bad">${icon('alert', 16)}<div class="grow">Ej inlämnade rader räknas inte: ${d.drafts.map((x) => `${esc(x.user)} (${x.lines})`).join(', ')}</div></div>` : ''}

    <div class="stats">
      <div class="stat"><div class="v">${arts.length}</div><div class="l">Räknade artiklar</div></div>
      <div class="stat"><div class="v">${diffCount}</div><div class="l">Med differens</div></div>
      <div class="stat"><div class="v">${signed(net)}</div><div class="l">Nettodifferens (antal)</div></div>
      <div class="stat"><div class="v">${subs.length}</div><div class="l">Inlämningar</div></div>
      <div class="stat"><div class="v">${d.uncounted.length}</div><div class="l">Ej räknade med saldo</div></div>
    </div>

    ${s.status === 'Sent' ? `<div class="card" id="results"><h2>Resultat från Visma</h2><p class="muted">Laddar…</p></div>` : ''}

    ${editable ? `
    <div class="card">
      <div class="card-head" style="margin-bottom:12px">
        <div><h2>Inventera via Excel</h2><div class="hint" style="margin-top:2px">Skapa en lista att skicka ut eller skriva ut. När den kommer tillbaka ifylld läses den in som en inlämning – precis som från mobilen.</div></div>
        <button id="xlCreate">${icon('sheet', 16)}Skapa Excel-fil</button>
      </div>
      <label class="dropzone" id="drop">
        ${icon('upload', 26)}
        <div><b>Dra hit en ifylld Excel-fil</b> eller klicka för att välja</div>
        <div class="hint">.xlsx – vår inventeringslista eller en egen fil med kolumnerna Artikelnr och Räknat antal</div>
        <input type="file" id="xlFile" accept=".xlsx,application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" hidden>
      </label>
    </div>` : ''}

    <div class="card flush">
      <div class="card-head"><h2>Artiklar</h2>
        <div class="segmented">
          <button data-f="all" class="${filter === 'all' ? 'active' : ''}">Alla ${arts.length}</button>
          <button data-f="diff" class="${filter === 'diff' ? 'active' : ''}">Differens ${diffCount}</button>
          <button data-f="warn" class="${filter === 'warn' ? 'active' : ''}">Varningar ${warnCount}</button>
        </div></div>
      <div class="table-wrap"><table>
        <thead><tr><th>Artikelnr</th><th>Benämning</th><th class="n">Saldo vid start</th><th class="n">Räknat</th><th class="n">Differens</th><th class="n">Rader</th><th>Räknat av</th></tr></thead>
        <tbody>${shown.map((a) => `
          <tr class="clickable" data-art="${esc(a.articleNo)}"><td><b>${esc(a.articleNo)}</b></td>
          <td>${esc(a.name)}${a.warnings.map((w) => `<div class="warn-text">${icon('alert', 13)}${esc(w)}</div>`).join('')}</td>
          <td class="n">${fmt(a.snapshotQuantity)}</td><td class="n"><b>${fmt(a.countedQuantity)}</b></td><td class="n">${signed(a.difference)}</td>
          <td class="n">${a.lineCount}</td><td class="secondary">${esc(a.countedBy.join(', '))}</td></tr>`).join('') || '<tr><td colspan="7" class="empty">Inget att visa.</td></tr>'}</tbody>
      </table></div>
    </div>

    <div class="card flush">
      <div class="card-head"><h2>Inlämningar</h2><a class="btn small" href="/api/admin/sessions/${s.id}/lines.csv">${icon('download', 14)}Alla rader (CSV)</a></div>
      <div class="table-wrap"><table>
        <thead><tr><th>Nr</th><th>Räknare</th><th>Inlämnad</th><th class="n">Rader</th><th class="n">Totalt</th><th>Källa</th><th>Kontrollsumma</th><th>Kommentar</th></tr></thead>
        <tbody>${subs.map((x) => `<tr class="clickable" data-sub="${x.id}"><td class="secondary">#${x.id}</td><td><b>${esc(x.countedByName ?? x.user)}</b>${x.countedByName ? `<div class="secondary">inläst av ${esc(x.user)}</div>` : ''}</td><td>${fmtTime(x.submittedAt)}</td>
          <td class="n">${x.lineCount}${x.voidedLines ? ` <span class="secondary">(${x.voidedLines} strukna)</span>` : ''}</td><td class="n">${fmt(x.totalQuantity)}</td>
          <td>${SIGNED_WITH[x.signedWith] ?? esc(x.signedWith)}</td>
          <td>${x.intact ? `<span class="pos">${icon('check', 14)} Intakt</span>` : `<span class="neg">${icon('alert', 14)} Ändrad efter signering</span>`} <code class="secondary">${esc(x.hash.slice(0, 10))}</code></td>
          <td class="secondary">${esc(x.comment)}</td></tr>`).join('') || '<tr><td colspan="8" class="empty">Inga inlämningar ännu.</td></tr>'}</tbody>
      </table></div>
    </div>

    ${d.uncounted.length ? `<div class="card flush"><div class="card-head"><div><h2>Ej räknade lagerartiklar med saldo</h2>
      <div class="hint" style="margin-top:2px">Ändras inte i Visma om du inte väljer att sätta dem till 0 när du skickar.</div></div><span class="pill plain">${d.uncounted.length}</span></div>
      <div class="table-wrap"><table><thead><tr><th>Artikelnr</th><th>Benämning</th><th class="n">Saldo</th></tr></thead>
      <tbody>${d.uncounted.map((a) => `<tr><td>${esc(a.articleNo)}</td><td>${esc(a.name)}</td><td class="n">${fmt(a.stockQuantity)}</td></tr>`).join('')}</tbody></table></div></div>` : ''}`;

  main.querySelectorAll('button[data-f]').forEach((b) => b.onclick = () => { filter = b.dataset.f; sessionView(id); });
  main.querySelectorAll('tr[data-art]').forEach((tr) => tr.onclick = () => linesDialog(s, { articleNo: tr.dataset.art }));
  main.querySelectorAll('tr[data-sub]').forEach((tr) => tr.onclick = () => linesDialog(s, { submissionId: tr.dataset.sub }));
  main.querySelectorAll('button[data-act]').forEach((b) => b.onclick = async () => {
    const act = b.dataset.act;
    if (act === 'cancel' && !confirm('Avbryta inventeringen? Inget skickas till Visma.')) return;
    if (act === 'lock' && draftTotal && !confirm(`${draftTotal} rader är inte inlämnade och kommer inte att räknas. Låsa ändå?`)) return;
    try { await api('POST', `/api/admin/sessions/${id}/${act}`); sessionView(id); } catch (e) { showMsg($('#smsg'), e.message); }
  });
  $('#sendBtn')?.addEventListener('click', () => sendDialog(s, arts, d.uncounted));
  if (s.status === 'Sent') loadResults(id);
  if (editable) wireExcel(s);
}

// ---------- Excel: skapa lista och läs in ----------
function wireExcel(s) {
  $('#xlCreate').onclick = () => {
    dlg.innerHTML = `
      <form id="xf">
        <h2>Skapa Excel-fil</h2>
        <p class="muted" style="margin-top:0">En inventeringslista med bolagets lagerartiklar. Räknaren fyller i kolumnen <b>Räknat antal</b> och sitt namn.</p>
        <label class="check"><input type="checkbox" id="xStock"> Visa saldo i Visma i filen</label>
        <p class="hint" style="margin:-4px 0 8px 26px">Av: räkna utan att se förväntat saldo (rekommenderas).</p>
        <label class="check"><input type="checkbox" id="xUncounted"> Bara artiklar som inte räknats än</label>
        <div class="row">
          <div><label>Från artikelnr</label><input id="xFrom" placeholder="valfritt"></div>
          <div><label>Till artikelnr</label><input id="xTo" placeholder="valfritt"></div>
        </div>
        <div class="dialog-actions"><button type="button" id="xCancel">Avbryt</button><button class="primary">${icon('download', 16)}Ladda ner</button></div>
      </form>`;
    dlg.showModal();
    $('#xCancel', dlg).onclick = () => dlg.close();
    $('#xf', dlg).onsubmit = (e) => {
      e.preventDefault();
      const q = new URLSearchParams({ includeStock: $('#xStock', dlg).checked, onlyUncounted: $('#xUncounted', dlg).checked });
      if ($('#xFrom', dlg).value) q.set('from', $('#xFrom', dlg).value);
      if ($('#xTo', dlg).value) q.set('to', $('#xTo', dlg).value);
      location.href = `/api/admin/sessions/${s.id}/countsheet.xlsx?${q}`;
      dlg.close();
    };
  };
  const drop = $('#drop'), input = $('#xlFile');
  input.onchange = () => input.files[0] && importPreview(s, input.files[0]);
  ['dragenter', 'dragover'].forEach((ev) => drop.addEventListener(ev, (e) => { e.preventDefault(); drop.classList.add('over'); }));
  ['dragleave', 'drop'].forEach((ev) => drop.addEventListener(ev, (e) => { e.preventDefault(); drop.classList.remove('over'); }));
  drop.addEventListener('drop', (e) => { const f = e.dataTransfer.files[0]; if (f) importPreview(s, f); });
}

async function uploadExcel(s, file, commit, extra = {}) {
  const form = new FormData();
  form.append('file', file);
  Object.entries(extra).forEach(([k, v]) => form.append(k, v));
  const q = new URLSearchParams({ commit, allowDuplicate: !!extra.allowDuplicate });
  const res = await fetch(`/api/admin/sessions/${s.id}/import?${q}`, { method: 'POST', body: form, headers: { 'X-Requested-With': 'inventering' }, credentials: 'same-origin' });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error ?? `Fel ${res.status}`);
  return data;
}

async function importPreview(s, file) {
  $('#xlFile').value = '';
  let p;
  try { p = await uploadExcel(s, file, false); }
  catch (e) { showMsg($('#smsg'), e.message); return; }
  const bad = p.rows.filter((r) => r.error);
  dlg.className = 'wide';
  dlg.innerHTML = `
    <form id="ipf">
      <h2>Läs in ${esc(p.fileName)}</h2>
      <div class="stats" style="margin:8px 0 12px">
        <div class="stat"><div class="v">${p.valid}</div><div class="l">Rader att läsa in</div></div>
        <div class="stat"><div class="v">${p.articles}</div><div class="l">Artiklar</div></div>
        <div class="stat"><div class="v">${fmt(p.total)}</div><div class="l">Totalt antal</div></div>
        <div class="stat"><div class="v ${p.invalid ? 'neg' : ''}">${p.invalid}</div><div class="l">Rader med fel (hoppas över)</div></div>
      </div>
      ${p.warnings.map((w) => `<div class="msg warn">${icon('alert', 16)}<div class="grow">${esc(w)}</div></div>`).join('')}
      <label>Räknat av</label>
      <input id="ipBy" value="${esc(p.countedBy ?? '')}" placeholder="Namn på den som räknat">
      <div class="table-wrap" style="max-height:42vh;overflow:auto;margin-top:14px;border:1px solid var(--border);border-radius:6px"><table>
        <thead><tr><th class="n">Rad</th><th>Artikelnr</th><th>Benämning</th><th class="n">Antal</th><th>Plats</th><th>Kommentar / fel</th></tr></thead>
        <tbody>${[...bad, ...p.rows.filter((r) => !r.error)].map((r) => `<tr><td class="n secondary">${r.row}</td><td><b>${esc(r.articleNo)}</b></td><td>${esc(r.name)}</td>
          <td class="n">${r.error ? '' : fmt(r.quantity)}</td><td>${esc(r.location)}</td>
          <td>${r.error ? `<span class="neg">${icon('alert', 13)} ${esc(r.error)}</span>` : `<span class="secondary">${esc(r.comment)}</span>`}</td></tr>`).join('')
          || '<tr><td colspan="6" class="empty">Inga ifyllda rader.</td></tr>'}</tbody>
      </table></div>
      ${p.duplicate ? `<label class="check"><input type="checkbox" id="ipDup"> Läs in ändå (filen är redan inläst en gång)</label>` : ''}
      <div id="ipmsg"></div>
      <div class="dialog-actions"><button type="button" id="ipCancel">Avbryt</button>
        <button class="primary" id="ipOk" ${p.valid ? '' : 'disabled'}>${icon('upload', 16)}Läs in ${p.valid} rader</button></div>
    </form>`;
  dlg.showModal();
  const close = () => { dlg.close(); dlg.className = ''; };
  $('#ipCancel', dlg).onclick = close;
  $('#ipf', dlg).onsubmit = async (e) => {
    e.preventDefault();
    $('#ipOk', dlg).disabled = true;
    try {
      const r = await uploadExcel(s, file, true, { countedBy: $('#ipBy', dlg).value, allowDuplicate: $('#ipDup', dlg)?.checked ?? false });
      close();
      await sessionView(s.id);
      showMsg($('#smsg'), `Inläst som inlämning #${r.submissionId}: ${r.imported} rader${r.skipped ? `, ${r.skipped} med fel hoppades över` : ''}.`, 'ok');
    } catch (err) { showMsg($('#ipmsg', dlg), err.message); $('#ipOk', dlg).disabled = false; }
  };
}

async function loadResults(id) {
  const rows = await api('GET', `/api/admin/sessions/${id}/results`);
  const failed = rows.filter((r) => !r.success);
  $('#results').innerHTML = `<h2>Resultat från Visma</h2>
    <p style="margin:0">${rows.length - failed.length} artiklar skrivna till inventeringsunderlaget${failed.length ? `, <b class="neg">${failed.length} misslyckades</b>` : ''}.</p>
    ${failed.length ? `<div class="table-wrap" style="margin-top:10px"><table><thead><tr><th>Artikelnr</th><th class="n">Räknat</th><th>Fel</th></tr></thead>
    <tbody>${failed.map((r) => `<tr><td>${esc(r.articleNo)}</td><td class="n">${fmt(r.countedQuantity)}</td><td>${esc(r.message)}</td></tr>`).join('')}</tbody></table></div>` : ''}`;
}

function sendDialog(s, arts, uncounted) {
  const changed = arts.filter((a) => a.currentStock !== a.snapshotQuantity).length;
  dlg.innerHTML = `
    <form method="dialog" id="sendForm">
      <h2>Godkänn och skicka till Visma</h2>
      <p class="muted" style="margin-top:0">${arts.length} artiklar skickas med räknat antal – summan av alla inlämnade räkningar per artikel.</p>
      <div id="vinfo" class="muted">Kontrollerar Visma…</div>
      ${changed ? `<div class="msg warn">${icon('alert', 16)}<div class="grow">${changed} artiklar har fått ändrat saldo i Visma sedan inventeringen startade. Kontrollera dem innan du skickar.</div></div>` : ''}
      ${uncounted.length ? `<label class="check"><input type="checkbox" id="zero"> Sätt ${uncounted.length} ej räknade lagerartiklar till 0</label>` : ''}
      <label class="check"><input type="checkbox" id="confirm" required> Jag har granskat inventeringen och vill uppdatera Visma</label>
      <div id="dmsg"></div>
      <div class="dialog-actions"><button value="cancel" formnovalidate>Avbryt</button><button class="primary" id="doSend" value="send">${icon('send', 16)}Skicka</button></div>
    </form>`;
  dlg.showModal();
  let toInventoryList = false;
  api('GET', '/api/admin/visma-status').then((v) => {
    toInventoryList = v.writesToInventoryList;
    $('#vinfo', dlg).innerHTML = !v.writesToInventoryList
      ? `<div class="msg warn">${icon('alert', 16)}<div class="grow">${esc(v.name)}: ${esc(v.details ?? '')}</div></div>`
      : v.inventoryListRows
        ? `<div class="msg ok">${icon('check', 16)}<div class="grow">Antalen skrivs till Vismas <b>inventeringsunderlag</b> (${v.inventoryListRows} artiklar). Lagret ändras först när du slutför inventeringen i Visma.</div></div>`
        : `<div class="msg bad">${icon('alert', 16)}<div class="grow">Det finns inget inventeringsunderlag i Visma. Skapa underlaget i Visma först.</div></div>`;
  }).catch((e) => { $('#vinfo', dlg).innerHTML = `<div class="msg bad">${icon('alert', 16)}<div class="grow">Kunde inte nå Visma: ${esc(e.message)}</div></div>`; });
  $('#doSend', dlg).onclick = async (e) => {
    e.preventDefault();
    if (!$('#confirm', dlg).checked) return showMsg($('#dmsg', dlg), 'Bekräfta först.', 'warn');
    e.currentTarget.disabled = true;
    showMsg($('#dmsg', dlg), 'Skickar till Visma…', 'warn');
    try {
      const r = await api('POST', `/api/admin/sessions/${s.id}/send`, { confirm: true, zeroUncounted: $('#zero', dlg)?.checked ?? false });
      dlg.close();
      await sessionView(s.id);
      showMsg($('#smsg'), `Klart: ${r.ok} artiklar skrivna${r.failed ? `, ${r.failed} misslyckades` : ''}.` +
        (toInventoryList ? ' Slutför nu inventeringen i Visma Administration (uppdatera lagret från inventeringsunderlaget).' : ''), r.failed ? 'warn' : 'ok');
    } catch (err) { showMsg($('#dmsg', dlg), err.message); $('#doSend', dlg).disabled = false; }
  };
}

async function linesDialog(s, filter) {
  const { articleNo, submissionId } = filter;
  const qs = articleNo ? `articleNo=${encodeURIComponent(articleNo)}` : `submissionId=${submissionId}`;
  const lines = await api('GET', `/api/admin/sessions/${s.id}/lines?${qs}`);
  const editable = s.status === 'Open' || s.status === 'Locked';
  dlg.className = 'wide';
  dlg.innerHTML = `
    <h2>${articleNo ? `${esc(articleNo)} · ${esc(lines[0]?.name ?? '')}` : `Inlämning #${esc(submissionId)}`}</h2>
    <div class="table-wrap" style="max-height:55vh;overflow:auto;border:1px solid var(--border);border-radius:6px"><table>
      <thead><tr><th>Tid</th><th>Vem</th>${articleNo ? '<th>Inlämning</th>' : '<th>Artikel</th>'}<th>Plats</th><th class="n">Antal</th><th></th></tr></thead>
      <tbody>${lines.map((l) => `<tr class="${l.isVoided ? 'voided' : ''}"><td>${fmtTime(l.countedAt)}</td><td>${esc(l.user)}</td>
        <td>${articleNo ? (l.submissionId ? '#' + l.submissionId : '<span class="neg">Utkast</span>') : `<b>${esc(l.articleNo)}</b> ${esc(l.name)}`}</td>
        <td>${esc(l.location)}${l.comment ? `<div class="secondary">${esc(l.comment)}</div>` : ''}${l.scanVia ? `<div class="secondary">${esc(l.scanVia)}</div>` : ''}</td><td class="n"><b>${fmt(l.quantity)}</b></td>
        <td class="actions">${editable ? `<button class="small ${l.isVoided ? '' : 'danger'}" data-id="${l.id}" data-a="${l.isVoided ? 'unvoid' : 'void'}">${l.isVoided ? 'Återställ' : 'Stryk'}</button>` : ''}</td></tr>`).join('')}</tbody>
    </table></div>
    ${s.status === 'Locked' && articleNo ? `<form id="corr" class="row" style="margin-top:14px">
      <input id="corrQty" type="number" step="any" placeholder="Korrigering (+ eller −)" required>
      <button class="shrink">${icon('plus', 16)}Lägg till korrigering</button></form>` : ''}
    <div id="lmsg"></div>
    <div class="dialog-actions"><button id="closeDlg">Stäng</button></div>`;
  dlg.showModal();
  $('#closeDlg', dlg).onclick = () => { dlg.close(); dlg.className = ''; sessionView(s.id); };
  dlg.querySelectorAll('button[data-id]').forEach((b) => b.onclick = async () => {
    try { await api('POST', `/api/admin/lines/${b.dataset.id}/${b.dataset.a}`); linesDialog(s, filter); }
    catch (e) { showMsg($('#lmsg', dlg), e.message); }
  });
  $('#corr', dlg)?.addEventListener('submit', async (e) => {
    e.preventDefault();
    try {
      await api('POST', `/api/sessions/${s.id}/lines`, [{ id: crypto.randomUUID(), articleNo, quantity: Number($('#corrQty', dlg).value), comment: 'Korrigering av admin', countedAt: new Date().toISOString() }]);
      linesDialog(s, filter);
    } catch (err) { showMsg($('#lmsg', dlg), err.message); }
  });
}

// ---------- Streckkoder ----------
const TEMPLATES = [
  { name: 'GS1-128 / DataMatrix', type: 'Gs1', pattern: '', quantityDecimals: 0, quantityMultiplier: 1,
    description: '(01) GTIN ger artikel via EAN, (30)/(37) antal, (310x) vikt i kg.' },
  { name: 'Artikelnr*antal', type: 'Regex', pattern: '^(?<art>[^*]+)\\*(?<qty>\\d+(?:[.,]\\d+)?)$', quantityDecimals: 0, quantityMultiplier: 1,
    description: 'Egen etikett, t.ex. 1001*12.' },
  { name: 'Viktvara EAN-13 (23 + 5 art + 5 vikt g)', type: 'Regex', pattern: '^23(?<art>\\d{5})(?<qty>\\d{5})\\d$', quantityDecimals: 3, quantityMultiplier: 1,
    description: 'Butiksintern EAN, t.ex. 2310030012508 = artikel 10030, 1,250 kg. Anpassa prefix och längder.' },
  { name: 'Prefix + artikelnr (t.ex. A1001)', type: 'Regex', pattern: '^A(?<art>.+)$', quantityDecimals: 0, quantityMultiplier: 1,
    description: 'Hyllkant/egen etikett med bokstavsprefix.' },
];


async function barcodesView() {
  const [rules, aliases] = await Promise.all([api('GET', '/api/admin/barcodes/rules'), api('GET', '/api/admin/barcodes/aliases')]);
  main.innerHTML = `
    ${pageHead('Streckkoder', `Hur skannade koder tolkas i ${esc(me.company?.name ?? '')}`)}
    <div class="card">
      <h2>Testa streckkod</h2>
      <form id="tf" class="row"><input id="tcode" placeholder="Skanna eller skriv en kod" autocomplete="off"><button class="shrink">Testa</button></form>
      <div id="tres"></div>
      <p class="hint">Ordning vid skanning: 1) extra streckkod nedan, 2) EAN eller artikelnr i Visma, 3) regler uppifrån och ned.
      GS1-koder kan skrivas med parenteser, t.ex. <code>(01)07310000000011(30)12</code>.</p>
    </div>
    <div class="card flush">
      <div class="card-head"><h2>Regler</h2>
        <div class="toolbar">
          <select id="tpl" style="width:auto;height:32px;padding:0 8px;font-size:13px"><option value="">Lägg till från mall…</option>${TEMPLATES.map((t, i) => `<option value="${i}">${esc(t.name)}</option>`).join('')}</select>
          <button id="newRule" class="primary small">${icon('plus', 14)}Ny regel</button>
        </div></div>
      <div class="table-wrap"><table>
        <thead><tr><th></th><th>Namn</th><th>Typ</th><th>Mönster</th><th class="n">Dec.</th><th class="n">× antal</th><th>Aktiv</th><th></th></tr></thead>
        <tbody>${rules.map((r, i) => `<tr><td class="row" style="gap:2px">
            <button class="icon-btn ghost" title="Flytta upp" data-mv="${r.id}" data-dir="up" ${i === 0 ? 'disabled' : ''}>${icon('up', 16)}</button>
            <button class="icon-btn ghost" title="Flytta ned" data-mv="${r.id}" data-dir="down" ${i === rules.length - 1 ? 'disabled' : ''}>${icon('down', 16)}</button></td>
          <td><b>${esc(r.name)}</b> ${r.companyId == null ? '<span class="pill plain">Alla bolag</span>' : ''}${r.description ? `<div class="muted">${esc(r.description)}</div>` : ''}</td>
          <td>${r.type === 1 || r.type === 'Gs1' ? 'GS1' : 'Regex'}</td><td><code>${esc(r.pattern)}</code></td>
          <td class="n">${r.quantityDecimals}</td><td class="n">${fmt(r.quantityMultiplier)}</td><td>${r.isActive ? 'Ja' : 'Nej'}</td>
          <td class="actions"><button class="small" data-edit="${r.id}">Ändra</button><button class="small danger" data-del="${r.id}">Ta bort</button></td></tr>`).join('')
          || '<tr><td colspan="8" class="empty">Inga regler – bara EAN och artikelnr från Visma används.</td></tr>'}</tbody>
      </table></div>
    </div>
    <div class="card">
      <h2>Extra streckkoder</h2>
      <p class="hint" style="margin-top:-6px;margin-bottom:12px">Visma har en streckkod per artikel. Här kan fler läggas till, t.ex. kartong-EAN som motsvarar 12 st.</p>
      <form id="af" class="row" style="flex-wrap:wrap" autocomplete="off">
        <input id="acode" placeholder="Streckkod" required>
        <input id="aart" placeholder="Artikelnr" required>
        <input id="aqty" type="number" step="any" min="0" value="1" placeholder="Antal per skanning" style="max-width:140px">
        <input id="anote" placeholder="Notering (t.ex. Kartong)">
        <button class="primary shrink">Spara</button>
      </form><div id="amsg"></div>
      <div class="table-wrap" style="margin-top:14px"><table>
        <thead><tr><th>Streckkod</th><th>Artikel</th><th class="n">Antal/skanning</th><th>Notering</th><th></th></tr></thead>
        <tbody>${aliases.map((a) => `<tr><td><code>${esc(a.code)}</code></td><td>${esc(a.articleNo)} ${esc(a.name)}</td><td class="n">${fmt(a.quantity)}</td>
          <td>${esc(a.note)}</td><td class="actions"><button class="small danger" data-adel="${esc(a.code)}">Ta bort</button></td></tr>`).join('')
          || '<tr><td colspan="5" class="empty">Inga extra streckkoder.</td></tr>'}</tbody>
      </table></div>
    </div>`;

  $('#tf').onsubmit = async (e) => {
    e.preventDefault();
    try { $('#tres').innerHTML = testResult(await api('POST', '/api/admin/barcodes/test', { code: $('#tcode').value })); }
    catch (err) { showMsg($('#tres'), err.message); }
  };
  $('#newRule').onclick = () => ruleDialog(null);
  $('#tpl').onchange = (e) => { if (e.target.value !== '') ruleDialog({ ...TEMPLATES[e.target.value], isActive: true }); e.target.value = ''; };
  main.querySelectorAll('[data-edit]').forEach((b) => b.onclick = () => ruleDialog(rules.find((r) => r.id === Number(b.dataset.edit))));
  main.querySelectorAll('[data-del]').forEach((b) => b.onclick = async () => {
    if (!confirm('Ta bort regeln?')) return;
    await api('DELETE', `/api/admin/barcodes/rules/${b.dataset.del}`); barcodesView();
  });
  main.querySelectorAll('[data-mv]').forEach((b) => b.onclick = async () => {
    await api('POST', `/api/admin/barcodes/rules/${b.dataset.mv}/move/${b.dataset.dir}`); barcodesView();
  });
  $('#af').onsubmit = async (e) => {
    e.preventDefault();
    try {
      await api('POST', '/api/admin/barcodes/aliases', { code: $('#acode').value, articleNo: $('#aart').value, quantity: Number($('#aqty').value || 1), note: $('#anote').value });
      barcodesView();
    } catch (err) { showMsg($('#amsg'), err.message); }
  };
  main.querySelectorAll('[data-adel]').forEach((b) => b.onclick = async () => {
    if (!confirm('Ta bort streckkoden?')) return;
    await api('DELETE', `/api/admin/barcodes/aliases/${encodeURIComponent(b.dataset.adel)}`); barcodesView();
  });
}

function testResult(r) {
  const gs1 = r.gs1 ? `<div class="hint">GS1-fält: ${Object.entries(r.gs1).map(([k, v]) => `(${esc(k)}) ${esc(v)}`).join(' · ')}</div>` : '';
  return r.found
    ? `<div class="msg ok">${icon('check', 16)}<div class="grow">Artikel <b>${esc(r.articleNo)}</b> ${esc(r.name)} · antal: ${r.quantity == null ? 'anges av användaren' : fmt(r.quantity)} · via ${esc(r.via)}</div></div>${gs1}`
    : `<div class="msg bad">${icon('alert', 16)}<div class="grow">Ingen artikel hittades.</div></div>${gs1}`;
}

function ruleDialog(rule) {
  const r = rule ?? { name: '', type: 'Regex', pattern: '', quantityDecimals: 0, quantityMultiplier: 1, isActive: true, description: '' };
  const isGs1 = r.type === 1 || r.type === 'Gs1';
  dlg.innerHTML = `
    <form id="rf">
      <h2>${rule?.id ? 'Ändra regel' : 'Ny regel'}</h2>
      <label>Namn</label><input id="rn" value="${esc(r.name)}" required>
      <label>Typ</label>
      <select id="rt"><option value="Regex" ${isGs1 ? '' : 'selected'}>Reguljärt uttryck</option><option value="Gs1" ${isGs1 ? 'selected' : ''}>GS1-128 / DataMatrix</option></select>
      <div id="patRow">
        <label>Mönster</label><input id="rp" value="${esc(r.pattern)}" style="font-family:ui-monospace,Consolas,monospace">
        <p class="hint">Grupper: <code>(?&lt;art&gt;…)</code> artikelnr eller <code>(?&lt;ean&gt;…)</code> EAN (en krävs), valfritt <code>(?&lt;qty&gt;…)</code> antal.</p>
      </div>
      <div class="row">
        <div><label>Decimaler i antal</label><input id="rd" type="number" min="0" max="6" value="${r.quantityDecimals}"></div>
        <div><label>Multiplikator</label><input id="rm" type="number" step="any" min="0" value="${r.quantityMultiplier}"></div>
      </div>
      <p class="hint">Exempel: vikt i gram med 3 decimaler ger kg. Multiplikator 12 = varje skanning är 12 st.</p>
      <label>Beskrivning</label><input id="rdesc" value="${esc(r.description)}">
      <label class="check"><input type="checkbox" id="ra" ${r.isActive ? 'checked' : ''}> Aktiv</label>
      <label class="check"><input type="checkbox" id="rall" ${rule?.id && rule.companyId == null ? 'checked' : ''}> Gäller alla bolag (annars bara ${esc(me.company?.name)})</label>
      <label>Testa regeln</label>
      <div class="row"><input id="rtest" placeholder="Exempelkod"><button type="button" class="shrink" id="rtestBtn">Testa</button></div>
      <div id="rmsg"></div>
      <div class="dialog-actions"><button type="button" id="rcancel">Avbryt</button><button class="primary">Spara</button></div>
    </form>`;
  dlg.showModal();
  const sync = () => $('#patRow', dlg).classList.toggle('hidden', $('#rt', dlg).value === 'Gs1');
  $('#rt', dlg).onchange = sync; sync();
  const body = () => ({
    name: $('#rn', dlg).value, type: $('#rt', dlg).value, pattern: $('#rp', dlg).value,
    quantityDecimals: Number($('#rd', dlg).value || 0), quantityMultiplier: Number($('#rm', dlg).value || 1),
    isActive: $('#ra', dlg).checked, description: $('#rdesc', dlg).value, allCompanies: $('#rall', dlg).checked,
  });
  $('#rcancel', dlg).onclick = () => dlg.close();
  $('#rtestBtn', dlg).onclick = async () => {
    try { $('#rmsg', dlg).innerHTML = testResult(await api('POST', '/api/admin/barcodes/test', { code: $('#rtest', dlg).value, rule: { ...body(), name: body().name || 'test' } })); }
    catch (e) { showMsg($('#rmsg', dlg), e.message); }
  };
  $('#rf', dlg).onsubmit = async (e) => {
    e.preventDefault();
    try {
      if (rule?.id) await api('PUT', `/api/admin/barcodes/rules/${rule.id}`, body());
      else await api('POST', '/api/admin/barcodes/rules', body());
      dlg.close(); barcodesView();
    } catch (err) { showMsg($('#rmsg', dlg), err.message); }
  };
}

// ---------- Bolag ----------
async function companiesView() {
  const companies = await api('GET', '/api/admin/companies');
  main.innerHTML = `
    ${pageHead('Bolag', 'Varje bolag är ett företag i Visma Administration. Ge det ett namn som användarna känner igen – mappen (FTG1, FTG2 …) syns bara här.',
      `<button id="addCompany">${icon('plus', 16)}Lägg till manuellt</button><button id="discover" class="primary">${icon('search', 16)}Sök bolag i Visma</button>`)}
    <div id="cmsg"></div>
    <div id="found"></div>
    <div class="card flush">
      <div class="table-wrap"><table>
        <thead><tr><th>Namn i appen</th><th>Namn i Visma</th><th>Mapp</th><th class="n">Artiklar</th><th>Uppdaterad</th><th>Aktiv</th><th></th></tr></thead>
        <tbody>${companies.map((c) => `<tr>
          <td><b>${esc(c.name)}</b>${me.company?.id === c.id ? ' <span class="pill Open">Valt</span>' : ''}</td>
          <td>${esc(c.vismaName)}</td><td><code>${esc(c.companyPath)}</code></td>
          <td class="n">${c.articles}</td><td>${fmtTime(c.lastRefresh)}${c.lastError ? `<div class="warn-text">${icon('alert', 13)}${esc(c.lastError)}</div>` : ''}</td>
          <td>${c.isActive ? 'Ja' : 'Nej'}</td>
          <td class="actions">
            <button class="small" data-test="${c.id}">Testa</button>
            <button class="small" data-edit="${c.id}">Ändra</button></td></tr>`).join('')
          || '<tr><td colspan="7" class="empty">Inga bolag ännu. Sök i Visma eller lägg till manuellt.</td></tr>'}</tbody>
      </table></div>
    </div>`;

  $('#addCompany').onclick = () => companyDialog(null);
  main.querySelectorAll('[data-edit]').forEach((b) => b.onclick = () => companyDialog(companies.find((c) => c.id === Number(b.dataset.edit))));
  main.querySelectorAll('[data-test]').forEach((b) => b.onclick = async () => {
    b.disabled = true; showMsg($('#cmsg'), 'Testar kopplingen och läser artiklar…', 'warn');
    try {
      const r = await api('POST', `/api/admin/companies/${b.dataset.test}/test`);
      showMsg($('#cmsg'), `OK: ${r.status.details ?? r.status.name} · ${r.articles} artiklar` +
        (r.status.writesToInventoryList ? ` · inventeringsunderlag: ${r.status.inventoryListRows || 'saknas'}` : ''), 'ok');
    } catch (e) { showMsg($('#cmsg'), e.message); }
    b.disabled = false;
  });
  $('#discover').onclick = async () => {
    showMsg($('#found'), 'Söker bolag i Visma…', 'warn');
    try {
      const found = await api('GET', '/api/admin/companies/discover');
      $('#found').innerHTML = `<div class="card flush"><div class="card-head"><h2>Hittade i Visma</h2></div><div class="table-wrap"><table>
        <thead><tr><th>Namn i Visma</th><th>Mapp</th><th>Databas</th><th></th></tr></thead>
        <tbody>${found.map((f, i) => `<tr><td>${esc(f.name)}</td><td><code>${esc(f.fullPath)}</code></td><td>${esc(f.databaseName)}</td>
          <td class="actions">${f.registered ? '<span class="secondary">Registrerat</span>' : `<button class="small primary" data-add="${i}">Lägg till</button>`}</td></tr>`).join('')
          || '<tr><td colspan="4" class="empty">Inga bolag hittades.</td></tr>'}</tbody></table></div></div>`;
      $('#found').querySelectorAll('[data-add]').forEach((b) => b.onclick = () => {
        const f = found[b.dataset.add];
        companyDialog({ name: f.name && f.name !== f.directory ? f.name : '', companyPath: f.fullPath, vismaName: f.name, isActive: true, sortOrder: companies.length + 1 }, f.directory);
      });
    } catch (e) { showMsg($('#found'), e.message); }
  };
}

function companyDialog(c, directory) {
  const isNew = !c?.id;
  c ??= { name: '', companyPath: '', vismaName: '', isActive: true, sortOrder: 0 };
  dlg.innerHTML = `
    <form id="cf" autocomplete="off">
      <h2>${isNew ? 'Lägg till bolag' : 'Ändra bolag'}</h2>
      <label>Namn i appen (visas för användarna)</label>
      <input id="cn" value="${esc(c.name)}" placeholder="t.ex. Bygg AB – Lager Göteborg" required>
      ${directory ? `<p class="hint">Mappen heter <b>${esc(directory)}</b> i Visma${c.vismaName && c.vismaName !== directory ? `, bolagsnamn: <b>${esc(c.vismaName)}</b>` : ''}.</p>` : ''}
      <label>Företagsmapp i Visma</label>
      <input id="cp" value="${esc(c.companyPath)}" placeholder="C:\\ProgramData\\SPCS\\SPCS Administration\\Företag\\FTG1" required style="font-family:ui-monospace,Consolas,monospace">
      <label>Visma-användare (om inloggning är aktiverad i Visma)</label>
      <input id="cu" value="${esc(c.vismaUser ?? '')}" autocomplete="off">
      <label>Visma-lösenord ${c.hasPassword ? '(sparat – lämna tomt för att behålla)' : ''}</label>
      <input id="cpw" type="password" autocomplete="new-password">
      ${c.hasPassword ? `<label class="check"><input type="checkbox" id="cclr"> Ta bort sparat lösenord</label>` : ''}
      <div class="row">
        <div><label>Sorteringsordning</label><input id="cs" type="number" value="${c.sortOrder ?? 0}"></div>
        <label class="check" style="margin-top:30px"><input type="checkbox" id="ca" ${c.isActive ? 'checked' : ''}> Aktivt</label>
      </div>
      <div id="cdmsg"></div>
      <div class="dialog-actions"><button type="button" id="ccancel">Avbryt</button><button class="primary">Spara</button></div>
    </form>`;
  dlg.showModal();
  $('#cn', dlg).focus();
  $('#ccancel', dlg).onclick = () => dlg.close();
  $('#cf', dlg).onsubmit = async (e) => {
    e.preventDefault();
    const body = {
      name: $('#cn', dlg).value, companyPath: $('#cp', dlg).value, vismaName: c.vismaName ?? null,
      vismaUser: $('#cu', dlg).value, vismaPassword: $('#cpw', dlg).value || null, clearPassword: $('#cclr', dlg)?.checked ?? false,
      isActive: $('#ca', dlg).checked, sortOrder: Number($('#cs', dlg).value || 0),
    };
    try {
      if (isNew) await api('POST', '/api/admin/companies', body);
      else await api('PUT', `/api/admin/companies/${c.id}`, body);
      dlg.close();
      // Första bolaget: välj det direkt.
      if (!me.company) { location.reload(); return; }
      companiesView();
    } catch (err) { showMsg($('#cdmsg', dlg), err.message); }
  };
}

// ---------- Användare ----------
async function usersView() {
  const [users, companies] = await Promise.all([api('GET', '/api/admin/users'), api('GET', '/api/admin/companies')]);
  const cname = (id) => companies.find((c) => c.id === id)?.name ?? `#${id}`;
  main.innerHTML = `
    ${pageHead('Användare', 'Användarna hanteras här och behöver inget konto i Visma.')}
    <div class="card">
      <h2>Ny användare</h2>
      <form id="uf" class="row" style="flex-wrap:wrap" autocomplete="off">
        <input id="un" placeholder="Användarnamn" required autocapitalize="none">
        <input id="dn" placeholder="Namn">
        <select id="ur" class="shrink" style="width:auto"><option value="Counter">Räknare</option><option value="Admin">Admin</option></select>
        <select id="uc" class="shrink" style="width:auto"><option value="">Alla bolag</option>${companies.map((c) => `<option value="${c.id}">Bara ${esc(c.name)}</option>`).join('')}</select>
        <button class="primary shrink">Skapa</button>
      </form>
      <p class="hint">Ett lösenord genereras och visas en gång.</p>
      <div id="umsg"></div>
    </div>
    <div class="card flush"><div class="table-wrap"><table>
      <thead><tr><th>Användarnamn</th><th>Namn</th><th>Roll</th><th>Bolag</th><th>Tvåfaktor</th><th>Status</th><th></th></tr></thead>
      <tbody>${users.map((u) => `<tr><td>${esc(u.userName)}</td><td>${esc(u.displayName)}</td><td>${u.role === 'Admin' ? 'Admin' : 'Räknare'}</td>
        <td>${u.role === 'Admin' || !u.companyIds.length ? '<span class="secondary">Alla</span>' : esc(u.companyIds.map(cname).join(', '))}
          ${u.role !== 'Admin' ? `<button class="icon-btn ghost" title="Ändra bolag" data-comp="${u.id}">${icon('edit', 14)}</button>` : ''}</td>
        <td>${u.totpEnabled ? `<span class="pos">${icon('shield', 14)} Ja</span>` : '<span class="secondary">Nej</span>'}</td><td>${u.isActive ? (u.locked ? '<span class="pill Cancelled">Spärrad</span>' : '<span class="pill Open">Aktiv</span>') : '<span class="pill">Inaktiv</span>'}</td>
        <td class="actions">
          <button class="small shrink" data-pw="${u.id}">Nytt lösenord</button>
          ${u.totpEnabled ? `<button class="small shrink" data-totp="${u.id}">Återställ 2FA</button>` : ''}
          ${u.id !== me.id ? `<button class="small shrink" data-role="${u.id}" data-to="${u.role === 'Admin' ? 'Counter' : 'Admin'}">${u.role === 'Admin' ? 'Gör till räknare' : 'Gör till admin'}</button>
          <button class="small shrink ${u.isActive ? 'danger' : ''}" data-act="${u.id}" data-to="${!u.isActive}">${u.isActive ? 'Inaktivera' : 'Aktivera'}</button>` : ''}
        </td></tr>`).join('')}</tbody></table></div></div>
    <div class="card"><h2>Mitt lösenord</h2>
      <form id="pf" class="row" style="flex-wrap:wrap">
        <input id="cur" type="password" placeholder="Nuvarande" autocomplete="current-password" required>
        <input id="nw" type="password" placeholder="Nytt (minst 10 tecken)" autocomplete="new-password" required>
        <button class="shrink">Byt</button>
      </form><div id="pmsg"></div></div>`;

  $('#uf').onsubmit = async (e) => {
    e.preventDefault();
    try {
      const r = await api('POST', '/api/admin/users', { userName: $('#un').value, displayName: $('#dn').value, role: $('#ur').value, companyIds: $('#uc').value ? [Number($('#uc').value)] : [] });
      await usersView();
      showMsg($('#umsg'), `Användaren skapad. Lösenord: ${r.password}  (visas bara nu)`, 'ok');
    } catch (err) { showMsg($('#umsg'), err.message); }
  };
  const put = async (id, body) => { try { await api('PUT', `/api/admin/users/${id}`, body); await usersView(); } catch (e) { alert(e.message); } };
  main.querySelectorAll('[data-pw]').forEach((b) => b.onclick = async () => {
    const pw = genPassword();
    if (!confirm('Sätta ett nytt slumpat lösenord?')) return;
    await put(b.dataset.pw, { userName: '', role: '', password: pw });
    showMsg($('#umsg'), `Nytt lösenord: ${pw}  (visas bara nu)`, 'ok');
  });
  main.querySelectorAll('[data-totp]').forEach((b) => b.onclick = async () => {
    if (!confirm('Återställ tvåfaktor? Användaren får aktivera den igen vid nästa inloggning.')) return;
    await api('POST', `/api/admin/users/${b.dataset.totp}/reset-totp`); usersView();
  });
  main.querySelectorAll('[data-role]').forEach((b) => b.onclick = () => put(b.dataset.role, { userName: '', role: b.dataset.to }));
  main.querySelectorAll('[data-comp]').forEach((b) => b.onclick = () => {
    const u = users.find((x) => x.id === Number(b.dataset.comp));
    dlg.innerHTML = `<form id="cf"><h2>Bolag för ${esc(u.userName)}</h2>
      <p class="hint">Inget markerat = alla bolag.</p>
      ${companies.map((c) => `<label class="check"><input type="checkbox" value="${c.id}" ${u.companyIds.includes(c.id) ? 'checked' : ''}> ${esc(c.name)}</label>`).join('')}
      <div class="dialog-actions"><button type="button" id="cc">Avbryt</button><button class="primary">Spara</button></div></form>`;
    dlg.showModal();
    $('#cc', dlg).onclick = () => dlg.close();
    $('#cf', dlg).onsubmit = async (e) => {
      e.preventDefault();
      const ids = [...dlg.querySelectorAll('input[type=checkbox]:checked')].map((x) => Number(x.value));
      dlg.close();
      await put(u.id, { userName: '', role: '', companyIds: ids });
    };
  });
  main.querySelectorAll('[data-act]').forEach((b) => b.onclick = () => put(b.dataset.act, { userName: '', role: '', isActive: b.dataset.to === 'true' }));
  $('#pf').onsubmit = async (e) => {
    e.preventDefault();
    try { await api('POST', '/api/auth/password', { currentPassword: $('#cur').value, newPassword: $('#nw').value }); location.reload(); }
    catch (err) { showMsg($('#pmsg'), err.message); }
  };
}

function genPassword() {
  const c = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789';
  return Array.from(crypto.getRandomValues(new Uint32Array(12)), (n) => c[n % c.length]).join('');
}

// ---------- Logg ----------
async function auditView() {
  const rows = await api('GET', '/api/admin/audit');
  main.innerHTML = `${pageHead('Händelselogg', 'De senaste 500 händelserna')}<div class="card flush"><div class="table-wrap"><table>
    <thead><tr><th>Tid</th><th>Användare</th><th>Bolag</th><th>Händelse</th><th>Detaljer</th><th>IP</th></tr></thead>
    <tbody>${rows.map((r) => `<tr><td>${fmtTime(r.at)}</td><td>${esc(r.user)}</td><td>${esc(r.company)}</td><td>${esc(r.action)}</td><td>${esc(r.details)}</td><td>${esc(r.remoteIp)}</td></tr>`).join('')}</tbody>
  </table></div></div>`;
}

start().catch((e) => { if (e.message !== 'not admin') showMsg(main, e.message); });
