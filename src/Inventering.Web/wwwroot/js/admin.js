import { $, api, esc, fmt, fmtTime, showMsg, ensureLoggedIn, logout } from './common.js';

const main = $('#main');
const dlg = $('#dlg');
const STATUS = { Open: 'Öppen', Locked: 'Låst', Sent: 'Skickad', Cancelled: 'Avbruten' };
const pill = (s) => `<span class="pill ${s}">${STATUS[s] ?? s}</span>`;
const signed = (n) => n > 0 ? `<span class="pos">+${fmt(n)}</span>` : n < 0 ? `<span class="neg">${fmt(n)}</span>` : '0';
let me;

async function start() {
  me = await ensureLoggedIn(main, { requireAdmin: true });
  $('#logoutBtn').classList.remove('hidden');
  $('#logoutBtn').onclick = logout;
  $('#tabs').classList.remove('hidden');
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

// ---------- Inventeringar ----------
async function sessionsView() {
  const [sessions, status] = await Promise.all([api('GET', '/api/admin/sessions'), api('GET', '/api/admin/status')]);
  const active = sessions.some((s) => s.status === 'Open' || s.status === 'Locked');
  main.innerHTML = `
    <div class="card">
      <h2>${esc(status.company.name)}</h2>
      <p class="muted">Visma-mapp: <code>${esc(status.company.companyPath)}</code> · ${status.articles} artiklar i cache · senast uppdaterad ${fmtTime(status.lastRefresh) || 'aldrig'}</p>
      ${status.lastError ? `<div class="msg bad">Senaste fel: ${esc(status.lastError)}</div>` : ''}
      <button id="refresh" class="small">Uppdatera artiklar från Visma</button>
      <button id="checkVisma" class="small">Kontrollera Visma</button> <span id="rmsg"></span>
      <div id="vstatus"></div>
    </div>
    <div class="card">
      <h2>Ny inventering</h2>
      ${active ? `<p class="muted">Det finns redan en pågående inventering. Skicka eller avbryt den innan du startar en ny.</p>` : `
      <form id="newForm" class="row">
        <input id="newName" placeholder="Namn, t.ex. Årsinventering ${new Date().getFullYear()}" required>
        <button class="primary shrink">Starta</button>
      </form>
      <p class="muted">Lagersaldot i Visma sparas när inventeringen startas och används som jämförelse.</p>`}
      <div id="nmsg"></div>
    </div>
    <div class="card">
      <h2>Inventeringar</h2>
      <div class="table-wrap"><table>
        <thead><tr><th>#</th><th>Namn</th><th>Status</th><th>Startad</th><th class="n">Artiklar</th><th class="n">Rader</th><th>Skickad</th></tr></thead>
        <tbody>${sessions.map((s) => `<tr class="clickable" data-id="${s.id}"><td>${s.id}</td><td>${esc(s.name)}</td><td>${pill(s.status)}</td>
          <td>${fmtTime(s.createdAt)}</td><td class="n">${s.articles}</td><td class="n">${s.lines}</td><td>${fmtTime(s.sentAt)}</td></tr>`).join('')
          || '<tr><td colspan="7" class="muted">Inga inventeringar ännu.</td></tr>'}</tbody>
      </table></div>
    </div>`;
  main.querySelectorAll('tr[data-id]').forEach((tr) => tr.onclick = () => go(`sessions/${tr.dataset.id}`));
  $('#refresh').onclick = async () => {
    $('#rmsg').textContent = 'Hämtar…';
    try { const r = await api('POST', '/api/admin/articles/refresh'); $('#rmsg').textContent = `${r.count} artiklar hämtade.`; }
    catch (e) { $('#rmsg').textContent = e.message; }
  };
  $('#checkVisma').onclick = async () => {
    showMsg($('#vstatus'), 'Kontrollerar…', 'warn');
    try {
      const v = await api('GET', '/api/admin/visma-status');
      showMsg($('#vstatus'), `${v.name}${v.details ? ' – ' + v.details : ''}` +
        (v.writesToInventoryList ? ` · Inventeringsunderlag: ${v.inventoryListRows ? v.inventoryListRows + ' artiklar' + (v.inventoryListDate ? ', ' + fmtTime(v.inventoryListDate) : '') : 'SAKNAS – skapa det i Visma innan du skickar'}` : ''),
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
  const totalDiff = arts.reduce((n, a) => n + Math.abs(a.difference), 0);

  main.innerHTML = `
    <p><a href="#sessions">← Alla inventeringar</a></p>
    <div class="card">
      <div class="row"><h2 style="margin:0">#${s.id} ${esc(s.name)} ${pill(s.status)}</h2></div>
      <p class="muted">Startad ${fmtTime(s.createdAt)}${s.lockedAt ? ' · låst ' + fmtTime(s.lockedAt) : ''}${s.sentAt ? ' · skickad ' + fmtTime(s.sentAt) : ''}</p>
      <p>${arts.length} räknade artiklar · ${diffCount} med differens (summa ${fmt(totalDiff)}) · ${warnCount} med varningar · ${d.uncounted.length} lagerartiklar med saldo som inte räknats</p>
      <div class="toolbar">
        ${s.status === 'Open' ? `<button data-act="lock" class="primary">Lås räkningen</button>` : ''}
        ${s.status === 'Locked' ? `<button data-act="reopen">Öppna för räkning igen</button><button id="sendBtn" class="primary">Godkänn och skicka till Visma…</button>` : ''}
        ${s.status === 'Open' || s.status === 'Locked' ? `<button data-act="cancel" class="danger">Avbryt inventering</button>` : ''}
        <button onclick="location.href='/api/admin/sessions/${s.id}/export.csv'">Exportera per artikel</button>
        <button onclick="location.href='/api/admin/sessions/${s.id}/lines.csv'">Exportera alla rader</button>
        <button id="reloadBtn">Uppdatera</button>
      </div>
      <div id="smsg"></div>
      ${s.status === 'Open' ? `<div class="msg warn">Räkning pågår. Lås inventeringen när alla har lämnat in för att granska och skicka.</div>` : ''}
      ${draftTotal ? `<div class="msg bad">Ej inlämnade rader räknas inte: ${d.drafts.map((x) => `${esc(x.user)} (${x.lines})`).join(', ')}</div>` : ''}
    </div>
    <div class="card">
      <h2>Inlämningar (${subs.length})</h2>
      <div class="table-wrap"><table>
        <thead><tr><th>#</th><th>Användare</th><th>Inlämnad</th><th class="n">Rader</th><th class="n">Totalt</th><th>Signerad</th><th>Kontrollsumma</th><th>Kommentar</th></tr></thead>
        <tbody>${subs.map((x) => `<tr class="clickable" data-sub="${x.id}"><td>${x.id}</td><td>${esc(x.user)}</td><td>${fmtTime(x.submittedAt)}</td>
          <td class="n">${x.lineCount}${x.voidedLines ? ` <span class="muted">(${x.voidedLines} strukna)</span>` : ''}</td><td class="n">${fmt(x.totalQuantity)}</td>
          <td>${x.signedWith === 'password' ? 'Lösenord' : 'Admin-korrigering'}</td>
          <td>${x.intact ? `<span class="pos">✔ Intakt</span>` : `<span class="neg">⚠ Ändrad efter signering</span>`} <span class="muted">${esc(x.hash.slice(0, 12))}</span></td>
          <td>${esc(x.comment)}</td></tr>`).join('') || '<tr><td colspan="8" class="muted">Inga inlämningar ännu.</td></tr>'}</tbody>
      </table></div>
    </div>
    ${s.status === 'Sent' ? `<div class="card" id="results"><h2>Resultat från Visma</h2><p class="muted">Laddar…</p></div>` : ''}
    <div class="card">
      <div class="toolbar">
        <button data-f="all" class="small ${filter === 'all' ? 'primary' : ''}">Alla (${arts.length})</button>
        <button data-f="diff" class="small ${filter === 'diff' ? 'primary' : ''}">Differens (${diffCount})</button>
        <button data-f="warn" class="small ${filter === 'warn' ? 'primary' : ''}">Varningar (${warnCount})</button>
      </div>
      <div class="table-wrap"><table>
        <thead><tr><th>Artikelnr</th><th>Benämning</th><th class="n">Saldo vid start</th><th class="n">Räknat</th><th class="n">Differens</th><th class="n">Rader</th><th>Räknat av</th></tr></thead>
        <tbody>${shown.map((a) => `
          <tr class="clickable" data-art="${esc(a.articleNo)}"><td>${esc(a.articleNo)}</td>
          <td>${esc(a.name)}${a.warnings.map((w) => `<div class="warn-text">⚠ ${esc(w)}</div>`).join('')}</td>
          <td class="n">${fmt(a.snapshotQuantity)}</td><td class="n"><b>${fmt(a.countedQuantity)}</b></td><td class="n">${signed(a.difference)}</td>
          <td class="n">${a.lineCount}</td><td>${esc(a.countedBy.join(', '))}</td></tr>`).join('') || '<tr><td colspan="7" class="muted">Inget att visa.</td></tr>'}</tbody>
      </table></div>
    </div>
    ${d.uncounted.length ? `<div class="card"><h2>Ej räknade lagerartiklar med saldo (${d.uncounted.length})</h2>
      <p class="muted">Dessa ändras inte i Visma om du inte väljer att sätta dem till 0 när du skickar.</p>
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
  $('#reloadBtn').onclick = () => sessionView(id);
  $('#sendBtn')?.addEventListener('click', () => sendDialog(s, arts, d.uncounted));
  if (s.status === 'Sent') loadResults(id);
}

async function loadResults(id) {
  const rows = await api('GET', `/api/admin/sessions/${id}/results`);
  const failed = rows.filter((r) => !r.success);
  $('#results').innerHTML = `<h2>Resultat från Visma</h2>
    <p>${rows.length - failed.length} artiklar uppdaterade${failed.length ? `, <b class="neg">${failed.length} misslyckades</b>` : ''}.</p>
    ${failed.length ? `<div class="table-wrap"><table><thead><tr><th>Artikelnr</th><th class="n">Räknat</th><th>Fel</th></tr></thead>
    <tbody>${failed.map((r) => `<tr><td>${esc(r.articleNo)}</td><td class="n">${fmt(r.countedQuantity)}</td><td>${esc(r.message)}</td></tr>`).join('')}</tbody></table></div>` : ''}`;
}

function sendDialog(s, arts, uncounted) {
  const changed = arts.filter((a) => a.currentStock !== a.snapshotQuantity).length;
  dlg.innerHTML = `
    <form method="dialog" id="sendForm">
      <h2>Skicka till Visma</h2>
      <p>${arts.length} artiklar skickas med räknat antal (summan av alla inlämnade räkningar per artikel).</p>
      <div id="vinfo" class="muted">Kontrollerar Visma…</div>
      ${changed ? `<div class="msg warn">${changed} artiklar har fått ändrat saldo i Visma sedan inventeringen startade (försäljning/inleverans under räkningen). Kontrollera dem innan du skickar.</div>` : ''}
      ${uncounted.length ? `<label class="row" style="color:var(--text)"><input type="checkbox" id="zero" style="width:auto;flex:0"> Sätt ${uncounted.length} ej räknade lagerartiklar till 0</label>` : ''}
      <label class="row" style="color:var(--text)"><input type="checkbox" id="confirm" style="width:auto;flex:0" required> Jag har granskat inventeringen och vill uppdatera Visma</label>
      <div id="dmsg"></div>
      <div class="row" style="margin-top:12px"><button value="cancel" formnovalidate>Avbryt</button><button class="primary" id="doSend" value="send">Skicka</button></div>
    </form>`;
  dlg.showModal();
  let toInventoryList = false;
  api('GET', '/api/admin/visma-status').then((v) => {
    toInventoryList = v.writesToInventoryList;
    $('#vinfo', dlg).innerHTML = !v.writesToInventoryList
      ? `<div class="msg warn">${esc(v.name)}: ${esc(v.details ?? '')}</div>`
      : v.inventoryListRows
        ? `<div class="msg ok">Antalen skrivs till Vismas <b>inventeringsunderlag</b> (${v.inventoryListRows} artiklar). Lagret ändras först när du slutför inventeringen i Visma.</div>`
        : `<div class="msg bad">Det finns inget inventeringsunderlag i Visma. Skapa underlaget i Visma först – annars kan inget skrivas.</div>`;
  }).catch((e) => { $('#vinfo', dlg).innerHTML = `<div class="msg bad">Kunde inte nå Visma: ${esc(e.message)}</div>`; });
  $('#doSend', dlg).onclick = async (e) => {
    e.preventDefault();
    if (!$('#confirm', dlg).checked) return showMsg($('#dmsg', dlg), 'Bekräfta först.', 'warn');
    e.target.disabled = true;
    showMsg($('#dmsg', dlg), 'Skickar till Visma…', 'warn');
    try {
      const r = await api('POST', `/api/admin/sessions/${s.id}/send`, { confirm: true, zeroUncounted: $('#zero', dlg)?.checked ?? false });
      dlg.close();
      alert(`Klart: ${r.ok} artiklar skrivna${r.failed ? `, ${r.failed} misslyckades` : ''}.` +
        (toInventoryList ? '\n\nGå nu in i Visma Administration och slutför inventeringen (uppdatera lagret från inventeringsunderlaget).' : ''));
      sessionView(s.id);
    } catch (err) { showMsg($('#dmsg', dlg), err.message); e.target.disabled = false; }
  };
}

async function linesDialog(s, filter) {
  const { articleNo, submissionId } = filter;
  const qs = articleNo ? `articleNo=${encodeURIComponent(articleNo)}` : `submissionId=${submissionId}`;
  const lines = await api('GET', `/api/admin/sessions/${s.id}/lines?${qs}`);
  const editable = s.status === 'Open' || s.status === 'Locked';
  dlg.innerHTML = `
    <h2>${articleNo ? `${esc(articleNo)} ${esc(lines[0]?.name ?? '')}` : `Inlämning #${esc(submissionId)}`}</h2>
    <div class="table-wrap"><table>
      <thead><tr><th>Tid</th><th>Vem</th>${articleNo ? '<th>Inl.</th>' : '<th>Artikel</th>'}<th>Plats</th><th class="n">Antal</th><th></th></tr></thead>
      <tbody>${lines.map((l) => `<tr class="${l.isVoided ? 'voided' : ''}"><td>${fmtTime(l.countedAt)}</td><td>${esc(l.user)}</td>
        <td>${articleNo ? (l.submissionId ? '#' + l.submissionId : '<span class="neg">Utkast</span>') : `${esc(l.articleNo)} ${esc(l.name)}`}</td>
        <td>${esc(l.location)}${l.comment ? `<div class="muted">${esc(l.comment)}</div>` : ''}</td><td class="n">${fmt(l.quantity)}</td>
        <td>${editable ? `<button class="small ${l.isVoided ? '' : 'danger'}" data-id="${l.id}" data-a="${l.isVoided ? 'unvoid' : 'void'}">${l.isVoided ? 'Återställ' : 'Stryk'}</button>` : ''}</td></tr>`).join('')}</tbody>
    </table></div>
    ${s.status === 'Locked' && articleNo ? `<form id="corr" class="row" style="margin-top:12px">
      <input id="corrQty" type="number" step="any" placeholder="Korrigering (+/−)" required>
      <button class="shrink">Lägg till</button></form>` : ''}
    <div id="lmsg"></div>
    <div class="row" style="margin-top:12px"><button id="closeDlg">Stäng</button></div>`;
  dlg.showModal();
  $('#closeDlg', dlg).onclick = () => { dlg.close(); sessionView(s.id); };
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
    description: '(01) GTIN → artikel via EAN, (30)/(37) antal, (310x) vikt i kg.' },
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
    <div class="card">
      <h2>Testa streckkod</h2>
      <form id="tf" class="row"><input id="tcode" placeholder="Skanna eller skriv en kod" autocomplete="off"><button class="shrink">Testa</button></form>
      <div id="tres"></div>
      <p class="muted">Ordning vid skanning: 1) extra streckkod nedan, 2) EAN eller artikelnr i Visma, 3) regler uppifrån och ned.
      Skriv GS1-koder med parenteser, t.ex. <code>(01)07310000000011(30)12</code>.</p>
    </div>
    <div class="card">
      <h2>Regler</h2>
      <div class="table-wrap"><table>
        <thead><tr><th></th><th>Namn</th><th>Typ</th><th>Mönster</th><th class="n">Dec.</th><th class="n">× antal</th><th>Aktiv</th><th></th></tr></thead>
        <tbody>${rules.map((r, i) => `<tr><td class="row" style="gap:2px">
            <button class="small shrink" data-mv="${r.id}" data-dir="up" ${i === 0 ? 'disabled' : ''}>↑</button>
            <button class="small shrink" data-mv="${r.id}" data-dir="down" ${i === rules.length - 1 ? 'disabled' : ''}>↓</button></td>
          <td><b>${esc(r.name)}</b> ${r.companyId == null ? '<span class="pill">Alla bolag</span>' : ''}${r.description ? `<div class="muted">${esc(r.description)}</div>` : ''}</td>
          <td>${r.type === 1 || r.type === 'Gs1' ? 'GS1' : 'Regex'}</td><td><code>${esc(r.pattern)}</code></td>
          <td class="n">${r.quantityDecimals}</td><td class="n">${fmt(r.quantityMultiplier)}</td><td>${r.isActive ? 'Ja' : 'Nej'}</td>
          <td class="row" style="justify-content:flex-end"><button class="small shrink" data-edit="${r.id}">Ändra</button><button class="small shrink danger" data-del="${r.id}">Ta bort</button></td></tr>`).join('')
          || '<tr><td colspan="8" class="muted">Inga regler – bara EAN och artikelnr från Visma används.</td></tr>'}</tbody>
      </table></div>
      <div class="toolbar" style="margin-top:12px">
        <button id="newRule" class="primary">Ny regel</button>
        <select id="tpl" style="width:auto"><option value="">Lägg till från mall…</option>${TEMPLATES.map((t, i) => `<option value="${i}">${esc(t.name)}</option>`).join('')}</select>
      </div>
    </div>
    <div class="card">
      <h2>Extra streckkoder – ${esc(me.company?.name)}</h2>
      <p class="muted">Visma har en streckkod per artikel. Här kan fler läggas till, t.ex. kartong-EAN som motsvarar 12 st.</p>
      <form id="af" class="row" style="flex-wrap:wrap" autocomplete="off">
        <input id="acode" placeholder="Streckkod" required>
        <input id="aart" placeholder="Artikelnr" required>
        <input id="aqty" type="number" step="any" min="0" value="1" placeholder="Antal per skanning" style="max-width:140px">
        <input id="anote" placeholder="Notering (t.ex. Kartong)">
        <button class="primary shrink">Spara</button>
      </form><div id="amsg"></div>
      <div class="table-wrap"><table>
        <thead><tr><th>Streckkod</th><th>Artikel</th><th class="n">Antal/skanning</th><th>Notering</th><th></th></tr></thead>
        <tbody>${aliases.map((a) => `<tr><td><code>${esc(a.code)}</code></td><td>${esc(a.articleNo)} ${esc(a.name)}</td><td class="n">${fmt(a.quantity)}</td>
          <td>${esc(a.note)}</td><td><button class="small danger" data-adel="${esc(a.code)}">Ta bort</button></td></tr>`).join('')
          || '<tr><td colspan="5" class="muted">Inga extra streckkoder.</td></tr>'}</tbody>
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
  const gs1 = r.gs1 ? `<div class="muted">GS1-fält: ${Object.entries(r.gs1).map(([k, v]) => `(${esc(k)}) ${esc(v)}`).join(' · ')}</div>` : '';
  return r.found
    ? `<div class="msg ok">Artikel <b>${esc(r.articleNo)}</b> ${esc(r.name)} · antal: ${r.quantity == null ? 'anges av användaren' : fmt(r.quantity)} · via ${esc(r.via)}</div>${gs1}`
    : `<div class="msg bad">Ingen artikel hittades.</div>${gs1}`;
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
        <p class="muted">Grupper: <code>(?&lt;art&gt;…)</code> artikelnr eller <code>(?&lt;ean&gt;…)</code> EAN (en krävs), valfritt <code>(?&lt;qty&gt;…)</code> antal.</p>
      </div>
      <div class="row">
        <div><label>Decimaler i antal</label><input id="rd" type="number" min="0" max="6" value="${r.quantityDecimals}"></div>
        <div><label>Multiplikator</label><input id="rm" type="number" step="any" min="0" value="${r.quantityMultiplier}"></div>
      </div>
      <p class="muted">Exempel: vikt i gram med 3 decimaler → kg. Multiplikator 12 = varje skanning är 12 st.</p>
      <label>Beskrivning</label><input id="rdesc" value="${esc(r.description)}">
      <label class="row" style="color:var(--text)"><input type="checkbox" id="ra" style="width:auto;flex:0" ${r.isActive ? 'checked' : ''}> Aktiv</label>
      <label class="row" style="color:var(--text)"><input type="checkbox" id="rall" style="width:auto;flex:0" ${rule?.id && rule.companyId == null ? 'checked' : ''}> Gäller alla bolag (annars bara ${esc(me.company?.name)})</label>
      <label>Testa regeln</label>
      <div class="row"><input id="rtest" placeholder="Exempelkod"><button type="button" class="shrink" id="rtestBtn">Testa</button></div>
      <div id="rmsg"></div>
      <div class="row" style="margin-top:12px"><button type="button" id="rcancel">Avbryt</button><button class="primary">Spara</button></div>
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
    <div class="card">
      <h2>Bolag</h2>
      <p class="muted">Varje bolag är ett företag i Visma Administration. Ge dem namn som användarna känner igen – mappen (FTG1, FTG2 …) syns bara här.</p>
      <div class="table-wrap"><table>
        <thead><tr><th>Namn i appen</th><th>Namn i Visma</th><th>Mapp</th><th class="n">Artiklar</th><th>Uppdaterad</th><th>Aktiv</th><th></th></tr></thead>
        <tbody>${companies.map((c) => `<tr>
          <td><b>${esc(c.name)}</b>${me.company?.id === c.id ? ' <span class="pill Open">Valt</span>' : ''}</td>
          <td>${esc(c.vismaName)}</td><td><code>${esc(c.companyPath)}</code></td>
          <td class="n">${c.articles}</td><td>${fmtTime(c.lastRefresh)}${c.lastError ? `<div class="warn-text">⚠ ${esc(c.lastError)}</div>` : ''}</td>
          <td>${c.isActive ? 'Ja' : 'Nej'}</td>
          <td class="row" style="justify-content:flex-end">
            <button class="small shrink" data-test="${c.id}">Testa</button>
            <button class="small shrink" data-edit="${c.id}">Ändra</button></td></tr>`).join('')
          || '<tr><td colspan="7" class="muted">Inga bolag ännu. Sök i Visma eller lägg till manuellt.</td></tr>'}</tbody>
      </table></div>
      <div id="cmsg"></div>
      <div class="toolbar" style="margin-top:12px">
        <button id="discover" class="primary">Sök bolag i Visma</button>
        <button id="addCompany">Lägg till manuellt</button>
      </div>
      <div id="found"></div>
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
    $('#found').innerHTML = '<p class="muted">Söker…</p>';
    try {
      const found = await api('GET', '/api/admin/companies/discover');
      $('#found').innerHTML = `<h2 style="margin-top:16px">Hittade i Visma</h2><div class="table-wrap"><table>
        <thead><tr><th>Namn i Visma</th><th>Mapp</th><th>Databas</th><th></th></tr></thead>
        <tbody>${found.map((f, i) => `<tr><td>${esc(f.name)}</td><td><code>${esc(f.fullPath)}</code></td><td>${esc(f.databaseName)}</td>
          <td>${f.registered ? '<span class="muted">Registrerat</span>' : `<button class="small primary" data-add="${i}">Lägg till</button>`}</td></tr>`).join('')
          || '<tr><td colspan="4" class="muted">Inga bolag hittades.</td></tr>'}</tbody></table></div>`;
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
      ${directory ? `<p class="muted">Mappen heter <b>${esc(directory)}</b> i Visma${c.vismaName && c.vismaName !== directory ? `, bolagsnamn: <b>${esc(c.vismaName)}</b>` : ''}.</p>` : ''}
      <label>Företagsmapp i Visma</label>
      <input id="cp" value="${esc(c.companyPath)}" placeholder="C:\\ProgramData\\SPCS\\SPCS Administration\\Företag\\FTG1" required style="font-family:ui-monospace,Consolas,monospace">
      <label>Visma-användare (om inloggning är aktiverad i Visma)</label>
      <input id="cu" value="${esc(c.vismaUser ?? '')}" autocomplete="off">
      <label>Visma-lösenord ${c.hasPassword ? '(sparat – lämna tomt för att behålla)' : ''}</label>
      <input id="cpw" type="password" autocomplete="new-password">
      ${c.hasPassword ? `<label class="row" style="color:var(--text)"><input type="checkbox" id="cclr" style="width:auto;flex:0"> Ta bort sparat lösenord</label>` : ''}
      <div class="row">
        <div><label>Sorteringsordning</label><input id="cs" type="number" value="${c.sortOrder ?? 0}"></div>
        <label class="row" style="color:var(--text);margin-top:28px"><input type="checkbox" id="ca" style="width:auto;flex:0" ${c.isActive ? 'checked' : ''}> Aktivt</label>
      </div>
      <div id="cdmsg"></div>
      <div class="row" style="margin-top:12px"><button type="button" id="ccancel">Avbryt</button><button class="primary">Spara</button></div>
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
    <div class="card">
      <h2>Ny användare</h2>
      <form id="uf" class="row" style="flex-wrap:wrap" autocomplete="off">
        <input id="un" placeholder="Användarnamn" required autocapitalize="none">
        <input id="dn" placeholder="Namn">
        <select id="ur" class="shrink" style="width:auto"><option value="Counter">Räknare</option><option value="Admin">Admin</option></select>
        <select id="uc" class="shrink" style="width:auto"><option value="">Alla bolag</option>${companies.map((c) => `<option value="${c.id}">Bara ${esc(c.name)}</option>`).join('')}</select>
        <button class="primary shrink">Skapa</button>
      </form>
      <p class="muted">Ett lösenord genereras och visas en gång.</p>
      <div id="umsg"></div>
    </div>
    <div class="card"><h2>Användare</h2><div class="table-wrap"><table>
      <thead><tr><th>Användarnamn</th><th>Namn</th><th>Roll</th><th>Bolag</th><th>Tvåfaktor</th><th>Status</th><th></th></tr></thead>
      <tbody>${users.map((u) => `<tr><td>${esc(u.userName)}</td><td>${esc(u.displayName)}</td><td>${u.role === 'Admin' ? 'Admin' : 'Räknare'}</td>
        <td>${u.role === 'Admin' || !u.companyIds.length ? '<span class="muted">Alla</span>' : esc(u.companyIds.map(cname).join(', '))}
          ${u.role !== 'Admin' ? `<button class="small" data-comp="${u.id}">Ändra</button>` : ''}</td>
        <td>${u.totpEnabled ? 'Ja' : 'Nej'}</td><td>${u.isActive ? (u.locked ? '<span class="neg">Spärrad</span>' : 'Aktiv') : '<span class="muted">Inaktiv</span>'}</td>
        <td class="row" style="justify-content:flex-end">
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
      <p class="muted">Inget markerat = alla bolag.</p>
      ${companies.map((c) => `<label class="row" style="color:var(--text)"><input type="checkbox" value="${c.id}" style="width:auto;flex:0" ${u.companyIds.includes(c.id) ? 'checked' : ''}> ${esc(c.name)}</label>`).join('')}
      <div class="row" style="margin-top:12px"><button type="button" id="cc">Avbryt</button><button class="primary">Spara</button></div></form>`;
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
  main.innerHTML = `<div class="card"><h2>Händelselogg (senaste 500)</h2><div class="table-wrap"><table>
    <thead><tr><th>Tid</th><th>Användare</th><th>Bolag</th><th>Händelse</th><th>Detaljer</th><th>IP</th></tr></thead>
    <tbody>${rows.map((r) => `<tr><td>${fmtTime(r.at)}</td><td>${esc(r.user)}</td><td>${esc(r.company)}</td><td>${esc(r.action)}</td><td>${esc(r.details)}</td><td>${esc(r.remoteIp)}</td></tr>`).join('')}</tbody>
  </table></div></div>`;
}

start().catch((e) => { if (e.message !== 'not admin') showMsg(main, e.message); });
