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
  const [tab, id] = (location.hash.slice(1) || 'sessions').split('/');
  $('#tabs').querySelectorAll('button').forEach((b) => b.classList.toggle('active', b.dataset.tab === tab));
  const r = { sessions: () => id ? sessionView(Number(id)) : sessionsView(), users: usersView, audit: auditView }[tab] ?? sessionsView;
  r().catch((e) => showMsg(main, e.message));
}

// ---------- Inventeringar ----------
async function sessionsView() {
  const [sessions, status] = await Promise.all([api('GET', '/api/admin/sessions'), api('GET', '/api/admin/status')]);
  const active = sessions.some((s) => s.status === 'Open' || s.status === 'Locked');
  main.innerHTML = `
    <div class="card">
      <h2>Visma</h2>
      <p class="muted">Koppling: <b>${esc(status.visma)}</b> · ${status.articles} artiklar i cache · senast uppdaterad ${fmtTime(status.lastRefresh) || 'aldrig'}</p>
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

// ---------- Användare ----------
async function usersView() {
  const users = await api('GET', '/api/admin/users');
  main.innerHTML = `
    <div class="card">
      <h2>Ny användare</h2>
      <form id="uf" class="row" style="flex-wrap:wrap" autocomplete="off">
        <input id="un" placeholder="Användarnamn" required autocapitalize="none">
        <input id="dn" placeholder="Namn">
        <select id="ur" class="shrink" style="width:auto"><option value="Counter">Räknare</option><option value="Admin">Admin</option></select>
        <button class="primary shrink">Skapa</button>
      </form>
      <p class="muted">Ett lösenord genereras och visas en gång.</p>
      <div id="umsg"></div>
    </div>
    <div class="card"><h2>Användare</h2><div class="table-wrap"><table>
      <thead><tr><th>Användarnamn</th><th>Namn</th><th>Roll</th><th>Tvåfaktor</th><th>Status</th><th></th></tr></thead>
      <tbody>${users.map((u) => `<tr><td>${esc(u.userName)}</td><td>${esc(u.displayName)}</td><td>${u.role === 'Admin' ? 'Admin' : 'Räknare'}</td>
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
      const r = await api('POST', '/api/admin/users', { userName: $('#un').value, displayName: $('#dn').value, role: $('#ur').value });
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
    <thead><tr><th>Tid</th><th>Användare</th><th>Händelse</th><th>Detaljer</th><th>IP</th></tr></thead>
    <tbody>${rows.map((r) => `<tr><td>${fmtTime(r.at)}</td><td>${esc(r.user)}</td><td>${esc(r.action)}</td><td>${esc(r.details)}</td><td>${esc(r.remoteIp)}</td></tr>`).join('')}</tbody>
  </table></div></div>`;
}

start().catch((e) => { if (e.message !== 'not admin') showMsg(main, e.message); });
