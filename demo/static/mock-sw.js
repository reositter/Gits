/*
 * DEMO: låtsasserver för inventeringsappen.
 *
 * Körs som service worker i webbläsaren och svarar på alla anrop till /api/* med testdata,
 * så att exakt samma mobil- och admin-sidor som i den riktiga appen kan visas på en statisk
 * webbplats (t.ex. Cloudflare Pages). Ingen koppling till Visma eller någon server.
 * Data sparas i webbläsarens Cache Storage och kan återställas från demobannern.
 */
/* global XLSX */
importScripts('/lib/xlsx.full.min.js');

const STATE_KEY = '/__demo_state__';
const VERSION = 4;

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()));
self.addEventListener('fetch', (e) => {
  const url = new URL(e.request.url);
  if (url.origin === self.location.origin && url.pathname.startsWith('/api/')) e.respondWith(handle(e.request, url));
});

// ---------------------------------------------------------------- state
let state = null;
async function load() {
  if (state) return state;
  try {
    const c = await caches.open('demo');
    const r = await c.match(STATE_KEY);
    if (r) { const s = await r.json(); if (s.version === VERSION) state = s; }
  } catch { /* ignore */ }
  if (!state) { state = seed(); await save(); }
  return state;
}
async function save() {
  const c = await caches.open('demo');
  await c.put(STATE_KEY, new Response(JSON.stringify(state), { headers: { 'Content-Type': 'application/json' } }));
}

const now = () => new Date().toISOString();
const ago = (days, h = 0) => new Date(Date.now() - days * 864e5 - h * 36e5).toISOString();
const uuid = () => crypto.randomUUID();
const ci = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();
const sv = (n, d = 3) => Number(n).toLocaleString('sv-SE', { maximumFractionDigits: d });

function seed() {
  const s = {
    version: VERSION, ids: { user: 10, company: 10, session: 10, submission: 10, rule: 10, result: 1, audit: 1 },
    auth: null,
    users: [
      { id: 1, userName: 'admin', displayName: 'Admin Demo', role: 'Admin', password: 'demo', isActive: true, companyIds: [] },
      { id: 2, userName: 'anna', displayName: 'Anna Lager', role: 'Counter', password: 'demo', isActive: true, companyIds: [] },
      { id: 3, userName: 'bo', displayName: 'Bo Plock', role: 'Counter', password: 'demo', isActive: true, companyIds: [1] },
    ],
    companies: [
      { id: 1, name: 'Demo Bygg AB – Huvudlager', companyPath: 'C:\\ProgramData\\SPCS\\SPCS Administration\\Företag\\FTG1', vismaName: 'Demo Bygg AB', isActive: true, sortOrder: 1 },
      { id: 2, name: 'Demo Livs AB', companyPath: 'C:\\ProgramData\\SPCS\\SPCS Administration\\Företag\\FTG2', vismaName: 'FTG2', isActive: true, sortOrder: 2 },
    ],
    articles: { 1: [], 2: [] }, articlesRefreshed: { 1: ago(0, 1), 2: ago(0, 1) },
    sessions: [], snapshots: {}, lines: [], submissions: [], results: [], rules: [], aliases: [], audit: [],
  };
  const bygg = [
    ['1001', 'Skruv M6x20 förzinkad (100-pack)', '7310000000011', 40, 'st', 79], ['1002', 'Mutter M6 förzinkad (100-pack)', '7310000000028', 25, 'st', 49],
    ['1003', 'Bricka M6 (100-pack)', null, 12, 'st', 29], ['1010', 'Träskruv 4,2x45 (200-pack)', '7310000000103', 64, 'st', 119],
    ['1011', 'Träskruv 5,0x80 (100-pack)', '7310000000110', 38, 'st', 139], ['1020', 'Expander 8 mm (50-pack)', '7310000000202', 20, 'st', 89],
    ['2001', 'Kabel EKK 3G1,5 (m)', '7310000000042', 350, 'm', 12.5], ['2002', 'Kopplingsdosa IP44', null, 48, 'st', 34],
    ['2003', 'Vägguttag dubbelt jordat', '7310000000301', 30, 'st', 89], ['2004', 'Strömbrytare 1-pol', '7310000000318', 26, 'st', 69],
    ['3001', 'Arbetshandske strl 10', '7310000000066', 60, 'par', 45], ['3002', 'Skyddsglasögon klar', '7310000000073', 22, 'st', 59],
    ['3003', 'Hörselkåpa', '7310000000080', 9, 'st', 249], ['4001', 'Gipsskiva 13 mm 1200x2500', '7320000000105', 120, 'st', 129],
    ['4002', 'Regel 45x95 (m)', null, 800, 'm', 24], ['4003', 'Spik 75 mm (kg)', '7320000000303', 35, 'kg', 59],
    ['4004', 'Fogskum 750 ml', '7320000000310', 44, 'st', 119], ['4005', 'Silikon sanitet vit', '7320000000327', 70, 'st', 89],
    ['5001', 'Borr HSS 6 mm', '7330000000017', 55, 'st', 39], ['5002', 'Bits PH2 (10-pack)', '7330000000024', 40, 'st', 79],
  ];
  const livs = [
    ['A100', 'Kaffe mellanrost 500 g', '7340000000014', 96, 'st', 54], ['A101', 'Te earl grey 100 p', '7340000000021', 40, 'st', 42],
    ['A200', 'Mjöl vetemjöl 2 kg', '7340000000106', 150, 'st', 19], ['A201', 'Socker 2 kg', '7340000000113', 120, 'st', 29],
    ['A300', 'Ost hushåll (kg)', null, 34.5, 'kg', 99], ['A301', 'Smör 500 g', '7340000000304', 80, 'st', 59],
    ['A400', 'Olivolja 1 l', '7340000000403', 45, 'st', 89], ['A401', 'Pasta penne 1 kg', '7340000000410', 200, 'st', 24],
  ];
  const mk = (cid, r) => ({ companyId: cid, articleNo: r[0], name: r[1], ean: r[2], stockQuantity: r[3], unit: r[4], purchasePrice: r[5], isStockItem: true });
  s.articles[1] = bygg.map((r) => mk(1, r));
  s.articles[2] = livs.map((r) => mk(2, r));

  s.rules = [
    { id: 1, companyId: null, name: 'GS1-128 / DataMatrix', sortOrder: 10, isActive: true, type: 1, pattern: '', quantityDecimals: 0, quantityMultiplier: 1, description: '(01) GTIN → artikel via EAN, (30)/(37) antal, (310x) vikt i kg.' },
    { id: 2, companyId: 1, name: 'Artikelnr*antal', sortOrder: 20, isActive: true, type: 0, pattern: '^(?<art>[^*]+)\\*(?<qty>\\d+(?:[.,]\\d+)?)$', quantityDecimals: 0, quantityMultiplier: 1, description: 'Egen etikett, t.ex. 1001*12.' },
    { id: 3, companyId: 2, name: 'Viktvara EAN-13 (23 + 5 art + 5 vikt g)', sortOrder: 30, isActive: true, type: 0, pattern: '^23(?<art>\\w{4})\\d?(?<qty>\\d{5})\\d$', quantityDecimals: 3, quantityMultiplier: 1, description: 'Butiksintern vikt-EAN.' },
  ];
  s.aliases = [{ companyId: 1, code: '17310000000018', articleNo: '1001', quantity: 12, note: 'Kartong' }];

  // En skickad inventering (förra året) och en pågående.
  const sent = { id: 1, companyId: 1, name: 'Årsinventering 2025', status: 'Sent', createdAt: ago(330), createdByUserId: 1, lockedAt: ago(329), sentAt: ago(329, -2), sentByUserId: 1 };
  const open = { id: 2, companyId: 1, name: 'Kvartalsinventering Q4', status: 'Open', createdAt: ago(1), createdByUserId: 1, lockedAt: null, sentAt: null, sentByUserId: null };
  s.sessions = [sent, open];
  for (const ses of s.sessions) {
    s.snapshots[ses.id] = {};
    for (const a of s.articles[1]) s.snapshots[ses.id][a.articleNo] = { q: a.stockQuantity, p: a.purchasePrice };
  }
  const add = (sid, uid, art, q, loc, d, h) => s.lines.push({ id: uuid(), sessionId: sid, userId: uid, articleNo: art, quantity: q, location: loc, comment: null, scannedCode: art, scanVia: 'EAN', countedAt: ago(d, h), deviceId: 'demo', submissionId: null, isVoided: false });
  [['1001', 30, 'A1'], ['1001', 8, 'B2'], ['1002', 25, 'A1'], ['1003', 14, 'A2'], ['1010', 60, 'A3'], ['2001', 312.5, 'C1'], ['2002', 48, 'C2'], ['3001', 58, 'D1'], ['4001', 118, 'E1']]
    .forEach(([a, q, l], i) => add(1, i < 5 ? 2 : 3, a, q, l, 329, 5 - i * 0.2));
  [['1001', 40, 'A1'], ['1002', 22, 'A1'], ['2003', 31, 'C3']].forEach(([a, q, l]) => add(2, 2, a, q, l, 0, 3));
  add(2, 3, '4003', 33, 'E2', 0, 2);
  s.submissionsPending = true;
  return s;
}

// Inlämningar i seed kräver SHA-256 (asynkront) – görs första gången state laddas.
async function finishSeed() {
  if (!state.submissionsPending) return;
  delete state.submissionsPending;
  const group = (sid, uid, t) => state.lines.filter((l) => l.sessionId === sid && l.userId === uid && !l.submissionId);
  await createSubmission(1, 2, group(1, 2), 'password', 'Lager A', ago(329, 3));
  await createSubmission(1, 3, group(1, 3), 'password', 'Lager C–E', ago(329, 2.5));
  await createSubmission(2, 2, group(2, 2), 'password', null, ago(0, 2));
  const sent = state.sessions[0];
  for (const a of summarize(sent).articles) {
    state.results.push({ id: state.ids.result++, sessionId: 1, articleNo: a.articleNo, countedQuantity: a.countedQuantity, snapshotQuantity: a.snapshotQuantity, stockBeforeWrite: a.snapshotQuantity, success: true, message: null, at: sent.sentAt });
  }
  log(1, 1, 'session.sent', `#1 ok=${state.results.length} fel=0`, ago(329, -2));
  log(2, 1, 'login.ok', 'anna', ago(0, 3));
  await save();
}

function log(userId, companyId, action, details, at) {
  state.audit.push({ id: state.ids.audit++, at: at ?? now(), userId, companyId, action, details, remoteIp: 'demo' });
}

// ---------------------------------------------------------------- http helpers
const json = (data, status = 200) => new Response(JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json; charset=utf-8', 'Cache-Control': 'no-store' } });
const err = (msg, status = 400, extra = {}) => json({ error: msg, ...extra }, status);
const ok = () => json({ ok: true });

async function handle(req, url) {
  await load();
  await finishSeed();
  try {
    const res = await route(req, url);
    if (req.method !== 'GET') await save();
    return res;
  } catch (e) {
    return err('Demo-fel: ' + e.message, 500);
  }
}

function currentUser() { return state.auth ? state.users.find((u) => u.id === state.auth.userId && u.isActive) : null; }
function companiesFor(u) {
  const active = state.companies.filter((c) => c.isActive).sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
  return u.role === 'Admin' || !u.companyIds.length ? active : active.filter((c) => u.companyIds.includes(c.id));
}
const display = (id) => { const u = state.users.find((x) => x.id === id); return u ? (u.displayName || u.userName) : `#${id}`; };
const uname = (id) => state.users.find((x) => x.id === id)?.userName ?? null;
const arts = (cid) => state.articles[cid] ?? [];
const art = (cid, no) => arts(cid).find((a) => ci(a.articleNo, String(no).trim()));
const byEan = (cid, ean) => arts(cid).find((a) => a.ean && a.ean === ean);

async function body(req) { try { return await req.json(); } catch { return {}; } }

async function route(req, url) {
  const p = url.pathname, m = req.method, q = url.searchParams;
  let r;
  const match = (method, re) => m === method && (r = p.match(re));

  if (match('GET', /^\/api\/health$/)) return json({ ok: true, demo: true });
  if (match('POST', /^\/api\/__demo\/reset$/)) { state = seed(); await finishSeed(); return ok(); }

  // ---- auth ----
  if (match('POST', /^\/api\/auth\/login$/)) {
    const b = await body(req);
    const u = state.users.find((x) => ci(x.userName, (b.userName ?? '').trim()));
    if (!u || !u.isActive || u.password !== b.password) return err('Fel användarnamn eller lösenord. (Demo: admin / demo)', 401);
    const cs = companiesFor(u);
    state.auth = { userId: u.id, companyId: cs.length === 1 ? cs[0].id : null };
    log(u.id, state.auth.companyId, 'login.ok', u.userName);
    return json({ ok: true, mustSetupTotp: false });
  }
  if (match('POST', /^\/api\/auth\/logout$/)) { state.auth = null; return ok(); }

  const user = currentUser();
  if (!user) return err('Inte inloggad', 401);

  if (match('GET', /^\/api\/auth\/me$/)) {
    const cs = companiesFor(user);
    const cur = cs.find((c) => c.id === state.auth.companyId);
    return json({
      id: user.id, userName: user.userName, displayName: user.displayName, role: user.role,
      totpEnabled: false, totpRequired: !!user.requireTotp, mfa: true, mustSetupTotp: false,
      company: cur ? { id: cur.id, name: cur.name } : null, companies: cs.map((c) => ({ id: c.id, name: c.name })),
    });
  }
  if (match('POST', /^\/api\/auth\/company$/)) {
    const b = await body(req);
    const c = companiesFor(user).find((x) => x.id === b.companyId);
    if (!c) return err('Du har inte behörighet till bolaget.', 403);
    state.auth.companyId = c.id;
    log(user.id, c.id, 'company.selected', c.name);
    return json({ id: c.id, name: c.name });
  }
  if (p.startsWith('/api/auth/totp/')) return err('Tvåfaktor kan inte provas i demon – i den riktiga tjänsten används en autentiseringsapp.');
  if (match('POST', /^\/api\/auth\/password$/)) {
    const b = await body(req);
    if (b.currentPassword !== user.password) return err('Nuvarande lösenord är fel.');
    if ((b.newPassword ?? '').length < 4) return err('Lösenordet måste vara minst 4 tecken (demo).');
    user.password = b.newPassword; return ok();
  }

  const isAdmin = user.role === 'Admin';

  // ---- admin, bolagsoberoende ----
  if (p.startsWith('/api/admin/') && !isAdmin) return err('Ingen behörighet', 403);
  const global = await routeGlobal(req, url, user, match, () => r);
  if (global) return global;

  // ---- bolag krävs ----
  const company = companiesFor(user).find((c) => c.id === state.auth.companyId);
  if (!company) return err('Välj bolag först.', 409, { needCompany: true });
  const cid = company.id;
  const sessionById = (id) => state.sessions.find((s) => s.id === Number(id) && s.companyId === cid);

  // ---- räkning ----
  if (match('GET', /^\/api\/sessions\/open$/))
    return json(state.sessions.filter((s) => s.companyId === cid && s.status === 'Open').sort((a, b) => b.id - a.id).map((s) => ({ id: s.id, name: s.name, createdAt: s.createdAt })));
  if (match('GET', /^\/api\/articles\/lookup$/)) {
    const res = parseBarcode(cid, q.get('code') ?? '', rulesFor(cid));
    if (!res) return err(`Ingen artikel med kod "${q.get('code')}".`, 404);
    const a = res.article;
    return json({ articleNo: a.articleNo, name: a.name, ean: a.ean, unit: a.unit, isStockItem: a.isStockItem, quantity: res.quantity, via: res.via });
  }
  if (match('GET', /^\/api\/articles\/search$/)) {
    const terms = (q.get('q') ?? '').toLowerCase().split(/\s+/).filter(Boolean);
    const hits = arts(cid).filter((a) => terms.length && terms.every((t) => `${a.articleNo} ${a.name} ${a.ean ?? ''}`.toLowerCase().includes(t)));
    return json(hits.slice(0, 25).map((a) => ({ articleNo: a.articleNo, name: a.name, ean: a.ean, unit: a.unit, isStockItem: a.isStockItem })));
  }
  if (match('POST', /^\/api\/sessions\/(\d+)\/lines$/)) {
    const s = sessionById(r[1]);
    if (!s) return err('Inventeringen finns inte i det valda bolaget.', 404);
    if (s.status !== 'Open' && !(isAdmin && s.status === 'Locked')) return err('Inventeringen är stängd för räkning.', 409);
    const input = await body(req);
    const accepted = [], rejected = [], added = [];
    for (const l of input) {
      if (state.lines.some((x) => x.id === l.id)) { accepted.push(l.id); continue; }
      const a = art(cid, l.articleNo ?? '');
      const e = !a ? `Okänd artikel ${l.articleNo}` : (l.quantity < 0 && !isAdmin) ? 'Negativt antal' : null;
      if (e) { rejected.push({ id: l.id, error: e }); continue; }
      const line = { id: l.id, sessionId: s.id, userId: user.id, articleNo: a.articleNo, quantity: Number(l.quantity), location: l.location ?? null, comment: l.comment ?? null, scannedCode: l.scannedCode ?? null, scanVia: l.scanVia ?? null, countedAt: l.countedAt ?? now(), deviceId: l.deviceId ?? null, submissionId: null, isVoided: false };
      state.lines.push(line); added.push(line); accepted.push(l.id);
    }
    if (isAdmin && s.status === 'Locked' && added.length) await createSubmission(s.id, user.id, added, 'admin-correction', 'Korrigering av admin');
    return json({ accepted, rejected });
  }
  if (match('GET', /^\/api\/sessions\/(\d+)\/mylines$/)) {
    if (!sessionById(r[1])) return err('', 404);
    return json(state.lines.filter((l) => l.sessionId === Number(r[1]) && l.userId === user.id && !l.isVoided)
      .sort((a, b) => b.countedAt.localeCompare(a.countedAt))
      .map((l) => ({ ...l, name: art(cid, l.articleNo)?.name })));
  }
  if (match('PATCH', /^\/api\/sessions\/(\d+)\/lines\/([\w-]+)$/)) {
    const s = sessionById(r[1]); const l = state.lines.find((x) => x.id === r[2] && x.sessionId === Number(r[1]));
    if (!s || !l) return err('', 404);
    if (l.userId !== user.id) return err('', 403);
    if (s.status !== 'Open') return err('Inventeringen är stängd.', 409);
    if (l.isVoided || l.submissionId) return err('Raden är inlämnad eller ändrad och kan inte ändras.', 409);
    const b = await body(req);
    l.isVoided = true; l.voidedAt = now(); l.voidedByUserId = user.id;
    const copy = { ...l, id: uuid(), quantity: Number(b.quantity), comment: b.comment ?? l.comment, isVoided: false, voidedAt: null, voidedByUserId: null };
    state.lines.push(copy);
    return json({ id: copy.id });
  }
  if (match('DELETE', /^\/api\/sessions\/(\d+)\/lines\/([\w-]+)$/)) {
    const s = sessionById(r[1]); const l = state.lines.find((x) => x.id === r[2] && x.sessionId === Number(r[1]));
    if (!s || !l) return err('', 404);
    if (l.userId !== user.id) return err('', 403);
    if (s.status !== 'Open') return err('Inventeringen är stängd.', 409);
    if (l.submissionId) return err('Raden är inlämnad och kan inte tas bort. Kontakta admin.', 409);
    l.isVoided = true; l.voidedAt = now(); l.voidedByUserId = user.id;
    return ok();
  }
  if (match('POST', /^\/api\/sessions\/(\d+)\/submit$/)) {
    const s = sessionById(r[1]);
    if (!s) return err('', 404);
    if (s.status !== 'Open') return err('Inventeringen är stängd för inlämning.', 409);
    const b = await body(req);
    if (b.password !== user.password) return err('Fel lösenord. (Demo: demo)', 401);
    const drafts = state.lines.filter((l) => l.sessionId === s.id && l.userId === user.id && !l.isVoided && !l.submissionId);
    if (!drafts.length) return err('Det finns inga rader att lämna in.');
    const want = new Set(b.lineIds ?? []);
    if (want.size !== drafts.length || drafts.some((d) => !want.has(d.id))) return err('Raderna har ändrats sedan listan visades. Uppdatera och försök igen.', 409);
    const sub = await createSubmission(s.id, user.id, drafts, 'password', b.comment || null);
    return json({ id: sub.id, submittedAt: sub.submittedAt, lineCount: sub.lineCount, totalQuantity: sub.totalQuantity, hash: sub.hash });
  }
  if (match('GET', /^\/api\/sessions\/(\d+)\/mysubmissions$/))
    return json(state.submissions.filter((x) => x.sessionId === Number(r[1]) && x.userId === user.id).sort((a, b) => b.id - a.id));

  // ---- admin, valt bolag ----
  if (!p.startsWith('/api/admin/')) return err('Okänt anrop i demon: ' + p, 404);

  if (match('GET', /^\/api\/admin\/status$/))
    return json({ company: { id: cid, name: company.name, companyPath: company.companyPath }, articles: arts(cid).length, lastRefresh: state.articlesRefreshed[cid], lastError: null });
  if (match('GET', /^\/api\/admin\/visma-status$/)) {
    await sleep(500);
    return json({ name: 'DEMO – ingen Visma', details: 'Visma Administration 2000, ADK 2023.1 (simulerat)', writesToInventoryList: true, inventoryListRows: arts(cid).length, inventoryListDate: ago(1) });
  }
  if (match('POST', /^\/api\/admin\/articles\/refresh$/)) { await sleep(600); state.articlesRefreshed[cid] = now(); return json({ count: arts(cid).length }); }
  if (match('GET', /^\/api\/admin\/sessions$/)) {
    return json(state.sessions.filter((s) => s.companyId === cid).sort((a, b) => b.id - a.id).map((s) => {
      const ls = state.lines.filter((l) => l.sessionId === s.id && !l.isVoided && l.submissionId);
      return { ...s, lines: ls.length, articles: new Set(ls.map((l) => l.articleNo)).size };
    }));
  }
  if (match('POST', /^\/api\/admin\/sessions$/)) {
    const b = await body(req);
    if (!b.name?.trim()) return err('Ange ett namn.');
    if (state.sessions.some((s) => s.companyId === cid && (s.status === 'Open' || s.status === 'Locked')))
      return err('Det finns redan en pågående inventering i bolaget. Skicka eller avbryt den först.', 409);
    await sleep(700);
    const s = { id: state.ids.session++, companyId: cid, name: b.name.trim(), status: 'Open', createdAt: now(), createdByUserId: user.id, lockedAt: null, sentAt: null, sentByUserId: null };
    state.sessions.push(s);
    state.snapshots[s.id] = Object.fromEntries(arts(cid).filter((a) => a.isStockItem).map((a) => [a.articleNo, { q: a.stockQuantity, p: a.purchasePrice }]));
    log(user.id, cid, 'session.created', `#${s.id} ${s.name}`);
    return json({ id: s.id });
  }
  if (match('POST', /^\/api\/admin\/sessions\/(\d+)\/(lock|reopen|cancel)$/)) {
    const s = sessionById(r[1]); if (!s) return err('', 404);
    const [from, to] = r[2] === 'lock' ? ['Open', 'Locked'] : r[2] === 'reopen' ? ['Locked', 'Open'] : [s.status, 'Cancelled'];
    if (s.status !== from || s.status === 'Sent' || s.status === 'Cancelled') return err(`Kan inte ${r[2]} när status är ${s.status}.`, 409);
    s.status = to; if (to === 'Locked') s.lockedAt = now();
    log(user.id, cid, 'session.' + r[2], `#${s.id}`);
    return ok();
  }
  if (match('GET', /^\/api\/admin\/sessions\/(\d+)\/summary$/)) {
    const s = sessionById(r[1]); if (!s) return err('', 404);
    const sum = summarize(s);
    const drafts = {};
    state.lines.filter((l) => l.sessionId === s.id && !l.isVoided && !l.submissionId).forEach((l) => { drafts[l.userId] = (drafts[l.userId] ?? 0) + 1; });
    return json({
      drafts: Object.entries(drafts).map(([uid, n]) => ({ user: uname(Number(uid)), lines: n })),
      session: { id: s.id, name: s.name, status: s.status, createdAt: s.createdAt, lockedAt: s.lockedAt, sentAt: s.sentAt },
      articles: sum.articles, uncounted: sum.uncounted.map((a) => ({ articleNo: a.articleNo, name: a.name, stockQuantity: a.stockQuantity })),
    });
  }
  if (match('GET', /^\/api\/admin\/sessions\/(\d+)\/submissions$/)) {
    const s = sessionById(r[1]); if (!s) return err('', 404);
    const out = [];
    for (const x of state.submissions.filter((y) => y.sessionId === s.id)) {
      const ls = state.lines.filter((l) => l.submissionId === x.id);
      out.push({ ...x, user: display(x.userId), intact: (await hashSubmission(x, ls)) === x.hash && ls.length === x.lineCount, voidedLines: ls.filter((l) => l.isVoided).length });
    }
    return json(out);
  }
  if (match('GET', /^\/api\/admin\/sessions\/(\d+)\/lines$/)) {
    const s = sessionById(r[1]); if (!s) return err('', 404);
    let ls = state.lines.filter((l) => l.sessionId === s.id);
    if (q.get('articleNo')) ls = ls.filter((l) => ci(l.articleNo, q.get('articleNo')));
    if (q.get('submissionId')) ls = ls.filter((l) => l.submissionId === Number(q.get('submissionId')));
    return json(ls.sort((a, b) => a.countedAt.localeCompare(b.countedAt)).map((l) => ({ ...l, name: art(cid, l.articleNo)?.name, user: uname(l.userId) })));
  }
  if (match('POST', /^\/api\/admin\/lines\/([\w-]+)\/(void|unvoid)$/)) {
    const l = state.lines.find((x) => x.id === r[1]); const s = l && sessionById(l.sessionId);
    if (!l || !s) return err('', 404);
    if (s.status === 'Sent' || s.status === 'Cancelled') return err('Inventeringen är redan avslutad.', 409);
    l.isVoided = r[2] === 'void'; l.voidedAt = l.isVoided ? now() : null; l.voidedByUserId = l.isVoided ? user.id : null;
    log(user.id, cid, 'line.' + r[2], `${l.id} art ${l.articleNo} antal ${l.quantity}`);
    return ok();
  }
  if (match('POST', /^\/api\/admin\/sessions\/(\d+)\/send$/)) {
    const s = sessionById(r[1]); if (!s) return err('', 404);
    const b = await body(req);
    if (!b.confirm) return err('Bekräftelse saknas.');
    if (s.status !== 'Locked') return err('Inventeringen måste vara låst innan den skickas.', 409);
    await sleep(1200);
    const sum = summarize(s);
    const rows = sum.articles.map((a) => ({ articleNo: a.articleNo, q: a.countedQuantity, snap: a.snapshotQuantity }));
    if (b.zeroUncounted) sum.uncounted.forEach((a) => rows.push({ articleNo: a.articleNo, q: 0, snap: state.snapshots[s.id]?.[a.articleNo]?.q ?? 0 }));
    for (const row of rows) {
      const a = art(cid, row.articleNo);
      state.results.push({ id: state.ids.result++, sessionId: s.id, articleNo: row.articleNo, countedQuantity: row.q, snapshotQuantity: row.snap, stockBeforeWrite: a?.stockQuantity ?? null, success: true, message: null, at: now() });
      if (a) a.stockQuantity = row.q; // i demon "slutförs" inventeringen direkt
    }
    s.status = 'Sent'; s.sentAt = now(); s.sentByUserId = user.id;
    log(user.id, cid, 'session.sent', `#${s.id} ok=${rows.length} fel=0`);
    return json({ ok: rows.length, failed: 0 });
  }
  if (match('GET', /^\/api\/admin\/sessions\/(\d+)\/results$/))
    return json(state.results.filter((x) => x.sessionId === Number(r[1])));
  if (match('GET', /^\/api\/admin\/sessions\/(\d+)\/report\.html$/)) {
    const s = sessionById(r[1]); if (!s) return err('', 404);
    return new Response(await reportHtml(await reportModel(s, company, user), q.get('onlyDiff') === '1'), { headers: { 'Content-Type': 'text/html; charset=utf-8' } });
  }
  if (match('GET', /^\/api\/admin\/sessions\/(\d+)\/report\.xlsx$/)) {
    const s = sessionById(r[1]); if (!s) return err('', 404);
    const bytes = reportXlsx(await reportModel(s, company, user));
    return new Response(bytes, { headers: {
      'Content-Type': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      'Content-Disposition': `attachment; filename="inventering-${s.id}-rapport.xlsx"`,
    } });
  }
  if (match('GET', /^\/api\/admin\/sessions\/(\d+)\/lines\.csv$/)) {
    const s = sessionById(r[1]); if (!s) return err('', 404);
    const rows = [['Rad-id', 'Artikelnr', 'Benämning', 'Antal', 'Plats', 'Räknad', 'Användare', 'Inlämning', 'Struken']];
    state.lines.filter((l) => l.sessionId === s.id).forEach((l) => rows.push([l.id, l.articleNo, art(cid, l.articleNo)?.name ?? '', String(l.quantity).replace('.', ','), l.location ?? '', l.countedAt, uname(l.userId), l.submissionId ?? 'UTKAST', l.isVoided ? 'Ja' : '']));
    const csv = '\ufeff' + rows.map((x) => x.map((v) => /[;"\n]/.test(String(v)) ? `"${String(v).replace(/"/g, '""')}"` : v).join(';')).join('\r\n');
    return new Response(csv, { headers: { 'Content-Type': 'text/csv; charset=utf-8', 'Content-Disposition': `attachment; filename="inventering-${s.id}-rader.csv"` } });
  }

  // ---- Excel: inventeringslista och inläsning ----
  if (match('GET', /^\/api\/admin\/sessions\/(\d+)\/countsheet\.xlsx$/)) {
    const s = sessionById(r[1]); if (!s) return err('', 404);
    const withStock = q.get('includeStock') === 'true';
    let list = arts(cid).filter((a) => a.isStockItem);
    if (q.get('onlyUncounted') === 'true') {
      const counted = new Set(state.lines.filter((l) => l.sessionId === s.id && l.submissionId && !l.isVoided).map((l) => l.articleNo.toLowerCase()));
      list = list.filter((a) => !counted.has(a.articleNo.toLowerCase()));
    }
    const head = ['Artikelnr', 'Benämning', 'Enhet', 'EAN', ...(withStock ? ['Saldo i Visma'] : []), 'Plats', 'Räknat antal', 'Kommentar'];
    const rows = [['Inventeringslista'], [company.name], [`#${s.id} ${s.name}`], ['Räknat av:', ''],
      ['Fyll i räknat antal. Tomma rader läses inte in. Ändra inte artikelnummer eller kolumnrubriker.'], [], head,
      ...list.map((a) => [a.articleNo, a.name, a.unit ?? '', a.ean ?? '', ...(withStock ? [a.stockQuantity] : []), '', '', ''])];
    const wb = XLSX.utils.book_new();
    const ws = XLSX.utils.aoa_to_sheet(rows);
    ws['!cols'] = head.map((h) => ({ wch: h === 'Benämning' ? 40 : h === 'Kommentar' ? 28 : 14 }));
    XLSX.utils.book_append_sheet(wb, ws, 'Inventering');
    XLSX.utils.book_append_sheet(wb, XLSX.utils.aoa_to_sheet([['sessionId', s.id], ['companyId', cid], ['format', 1]]), '_inventering');
    wb.Workbook = { Sheets: [{ Hidden: 0 }, { Hidden: 2 }] };
    return new Response(XLSX.write(wb, { type: 'array', bookType: 'xlsx' }), { headers: {
      'Content-Type': 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet',
      'Content-Disposition': `attachment; filename="Inventeringslista-${s.id}.xlsx"` } });
  }
  if (match('POST', /^\/api\/admin\/sessions\/(\d+)\/import$/)) {
    const s = sessionById(r[1]); if (!s) return err('', 404);
    if (s.status !== 'Open' && s.status !== 'Locked') return err('Inventeringen är avslutad – inget kan läsas in.', 409);
    const form = await req.formData();
    const file = form.get('file');
    if (!file || !file.size) return err('Ingen fil.');
    const buf = await file.arrayBuffer();
    const fileHash = await sha256(String.fromCharCode(...new Uint8Array(await crypto.subtle.digest('SHA-256', buf))));
    let wb; try { wb = XLSX.read(buf, { type: 'array' }); } catch { return err('Filen kunde inte läsas som Excel (.xlsx).'); }
    const meta = wb.Sheets._inventering ? XLSX.utils.sheet_to_json(wb.Sheets._inventering, { header: 1 }) : null;
    const metaVal = (k) => meta?.find((x) => x[0] === k)?.[1];
    if (metaVal('companyId') != null && Number(metaVal('companyId')) !== cid) return err('Filen är skapad för ett annat bolag.');
    let parsed = null;
    for (const name of wb.SheetNames.filter((n) => n !== '_inventering')) {
      const grid = XLSX.utils.sheet_to_json(wb.Sheets[name], { header: 1, raw: true, defval: '' });
      for (let hr = 0; hr < Math.min(grid.length, 30); hr++) {
        const cols = {};
        grid[hr].forEach((h, c) => {
          h = String(h).trim().toLowerCase();
          if (['artikelnr', 'artikelnummer', 'art.nr', 'artnr'].includes(h)) cols.art ??= c;
          else if (['räknat antal', 'räknat', 'antal', 'inventerat antal'].includes(h)) cols.qty ??= c;
          else if (['plats', 'lagerplats', 'hylla'].includes(h)) cols.loc ??= c;
          else if (['kommentar', 'anmärkning'].includes(h)) cols.comment ??= c;
        });
        if (cols.art == null || cols.qty == null) continue;
        let countedBy = null;
        for (let i = 0; i < hr; i++) if (String(grid[i][0]).trim().toLowerCase().startsWith('räknat av')) countedBy = String(grid[i][1] ?? '').trim() || null;
        const rows = [];
        for (let i = hr + 1; i < grid.length; i++) {
          const raw = grid[i][cols.qty];
          if (raw === '' || raw == null) continue;
          const no = String(grid[i][cols.art] ?? '').trim();
          const qty = typeof raw === 'number' ? raw : Number(String(raw).replace(/\s/g, '').replace(',', '.'));
          const a = no ? art(cid, no) : null;
          const error = !Number.isFinite(qty) ? `Antalet "${raw}" är inte ett tal` : qty < 0 ? 'Negativt antal'
            : !no ? 'Artikelnummer saknas' : !a ? `Artikel ${no} finns inte i bolaget` : null;
          rows.push({ row: i + 1, articleNo: a?.articleNo ?? no, name: a?.name ?? null, quantity: Number.isFinite(qty) ? qty : 0,
            location: cols.loc != null ? String(grid[i][cols.loc] ?? '').trim() || null : null,
            comment: cols.comment != null ? String(grid[i][cols.comment] ?? '').trim() || null : null, error });
        }
        parsed = { rows, countedBy };
        break;
      }
      if (parsed) break;
    }
    if (!parsed) return err('Hittade ingen flik med kolumnerna "Artikelnr" och "Räknat antal".');
    const warnings = [];
    if (metaVal('sessionId') != null && Number(metaVal('sessionId')) !== s.id) warnings.push(`Filen skapades för inventering #${metaVal('sessionId')}, men läses in i #${s.id}.`);
    if (!meta) warnings.push('Filen är inte skapad från systemet – kolumnerna tolkades automatiskt.');
    const countedBy = (form.get('countedBy') || parsed.countedBy || '').trim() || null;
    if (!countedBy) warnings.push('"Räknat av" är inte ifyllt i filen.');
    if (!parsed.rows.length) warnings.push('Inga ifyllda rader hittades – kolumnen "Räknat antal" är tom.');
    const dup = state.submissions.find((x) => x.sessionId === s.id && x.sourceHash === fileHash);
    if (dup) warnings.push(`Exakt samma fil lästes redan in (inlämning #${dup.id}).`);
    const valid = parsed.rows.filter((x) => !x.error);
    if (q.get('commit') === 'true') {
      if (dup && q.get('allowDuplicate') !== 'true') return err(`Filen är redan inläst (inlämning #${dup.id}).`, 409);
      if (!valid.length) return err('Inga giltiga rader att läsa in.');
      const at = now();
      const lines = valid.map((x) => ({ id: uuid(), sessionId: s.id, userId: user.id, articleNo: x.articleNo, quantity: x.quantity, location: x.location, comment: x.comment,
        scannedCode: null, scanVia: `Excel-import rad ${x.row}`, countedAt: at, deviceId: 'Excel: ' + file.name, submissionId: null, isVoided: false }));
      state.lines.push(...lines);
      const sub = await createSubmission(s.id, user.id, lines, 'excel-import', `Excel: ${file.name}${countedBy ? ' – räknat av ' + countedBy : ''}`);
      Object.assign(sub, { sourceFile: file.name, sourceHash: fileHash, countedByName: countedBy });
      return json({ submissionId: sub.id, imported: lines.length, skipped: parsed.rows.length - valid.length });
    }
    return json({ fileName: file.name, countedBy, valid: valid.length, invalid: parsed.rows.length - valid.length,
      total: valid.reduce((n, x) => n + x.quantity, 0), articles: new Set(valid.map((x) => x.articleNo.toLowerCase())).size,
      duplicate: !!dup, warnings, rows: parsed.rows });
  }

  // ---- streckkoder ----
  if (match('GET', /^\/api\/admin\/barcodes\/rules$/))
    return json(state.rules.filter((x) => x.companyId == null || x.companyId === cid).sort((a, b) => a.sortOrder - b.sortOrder || a.id - b.id));
  if (match('POST', /^\/api\/admin\/barcodes\/rules$/) || match('PUT', /^\/api\/admin\/barcodes\/rules\/(\d+)$/)) {
    const b = await body(req);
    const isNew = m === 'POST';
    const rule = isNew ? { id: state.ids.rule++, sortOrder: Math.max(0, ...state.rules.map((x) => x.sortOrder)) + 10 } : state.rules.find((x) => x.id === Number(r[1]));
    if (!rule) return err('', 404);
    const e = applyRule(rule, b, cid); if (e) return err(e);
    if (isNew) state.rules.push(rule);
    return json({ id: rule.id });
  }
  if (match('POST', /^\/api\/admin\/barcodes\/rules\/(\d+)\/move\/(up|down)$/)) {
    const list = state.rules.filter((x) => x.companyId == null || x.companyId === cid).sort((a, b) => a.sortOrder - b.sortOrder || a.id - b.id);
    const i = list.findIndex((x) => x.id === Number(r[1])), j = r[2] === 'up' ? i - 1 : i + 1;
    if (i >= 0 && j >= 0 && j < list.length) [list[i], list[j]] = [list[j], list[i]];
    list.forEach((x, k) => { x.sortOrder = (k + 1) * 10; });
    return ok();
  }
  if (match('DELETE', /^\/api\/admin\/barcodes\/rules\/(\d+)$/)) { state.rules = state.rules.filter((x) => x.id !== Number(r[1])); return ok(); }
  if (match('GET', /^\/api\/admin\/barcodes\/aliases$/))
    return json(state.aliases.filter((a) => a.companyId === cid).map((a) => ({ ...a, name: art(cid, a.articleNo)?.name })));
  if (match('POST', /^\/api\/admin\/barcodes\/aliases$/)) {
    const b = await body(req);
    const a = art(cid, b.articleNo ?? '');
    if (!b.code?.trim()) return err('Ange en streckkod.');
    if (!a) return err(`Artikel ${b.articleNo} finns inte.`);
    if (byEan(cid, normEan(b.code.trim()))) return err('Koden är redan EAN för en artikel i Visma.');
    state.aliases = state.aliases.filter((x) => !(x.companyId === cid && x.code === b.code.trim()));
    state.aliases.push({ companyId: cid, code: b.code.trim(), articleNo: a.articleNo, quantity: Number(b.quantity) || 1, note: b.note ?? null });
    return ok();
  }
  if (match('DELETE', /^\/api\/admin\/barcodes\/aliases\/(.+)$/)) {
    const code = decodeURIComponent(r[1]); state.aliases = state.aliases.filter((x) => !(x.companyId === cid && x.code === code)); return ok();
  }
  if (match('POST', /^\/api\/admin\/barcodes\/test$/)) {
    const b = await body(req);
    let rules = rulesFor(cid);
    if (b.rule) { const t = { id: 0, sortOrder: 0 }; const e = applyRule(t, b.rule, cid); if (e) return err(e); rules = [t]; }
    const res = parseBarcode(cid, b.code ?? '', rules);
    const ais = parseAis(b.code ?? '');
    return json({ found: !!res, articleNo: res?.article.articleNo, name: res?.article.name, quantity: res?.quantity ?? null, via: res?.via, gs1: ais && (ais['01'] || ais['02']) ? ais : null });
  }

  return err('Okänt anrop i demon: ' + m + ' ' + p, 404);
}

async function routeGlobal(req, url, user, match, rr) {
  if (match('GET', /^\/api\/admin\/server$/))
    return json({ mode: 'InternalCa', enabled: true, publicHost: 'inventering.lager.local', port: 443, httpsUrl: 'https://inventering.lager.local',
      certificatePage: 'http://inventering.lager.local/certifikat.html', windows: true, canUpload: false, rootCa: true,
      certificate: { subject: 'inventering.lager.local', issuer: 'Inventering rotcertifikat (VISMA01)', notBefore: ago(30), notAfter: ago(-795), daysLeft: 795,
        thumbprint: '3F2A9C0D5E7B41A6C8D9E0F1A2B3C4D5E6F70812', names: ['inventering.lager.local', 'visma01', 'visma01.lager.local'] } });
  if (match('POST', /^\/api\/admin\/server\/certificate$/)) return err('Certifikat kan inte bytas i demon.');
  if (match('GET', /^\/api\/admin\/companies$/))
    return json(state.companies.map((c) => ({ ...c, hasPassword: false, articles: arts(c.id).length, lastRefresh: state.articlesRefreshed[c.id] ?? null, lastError: null })));
  if (match('GET', /^\/api\/admin\/companies\/discover$/)) {
    await sleep(700);
    const found = [
      { name: 'Demo Bygg AB', directory: 'FTG1', fullPath: state.companies[0]?.companyPath ?? '', databaseName: 'SPCS_Adm_FTG1' },
      { name: 'FTG2', directory: 'FTG2', fullPath: 'C:\\ProgramData\\SPCS\\SPCS Administration\\Företag\\FTG2', databaseName: 'SPCS_Adm_FTG2' },
      { name: 'Demo Service AB', directory: 'FTG3', fullPath: 'C:\\ProgramData\\SPCS\\SPCS Administration\\Företag\\FTG3', databaseName: 'SPCS_Adm_FTG3' },
    ];
    return json(found.map((f) => ({ ...f, registered: state.companies.some((c) => ci(c.companyPath, f.fullPath)) })));
  }
  if (match('POST', /^\/api\/admin\/companies$/) || match('PUT', /^\/api\/admin\/companies\/(\d+)$/)) {
    const b = await req.json();
    const r = rr();
    const isNew = req.method === 'POST';
    const c = isNew ? { id: state.ids.company++ } : state.companies.find((x) => x.id === Number(r[1]));
    if (!c) return err('', 404);
    if (!b.name?.trim()) return err('Ange ett namn.');
    if (!b.companyPath?.trim()) return err('Ange företagsmappen i Visma.');
    if (state.companies.some((x) => x.id !== c.id && ci(x.companyPath, b.companyPath.trim()))) return err('Bolaget (företagsmappen) är redan registrerat.');
    Object.assign(c, { name: b.name.trim(), companyPath: b.companyPath.trim(), vismaName: b.vismaName ?? null, vismaUser: b.vismaUser || null, isActive: !!b.isActive, sortOrder: Number(b.sortOrder) || 0 });
    if (isNew) {
      state.companies.push(c);
      // Nya demobolag får några påhittade artiklar.
      state.articles[c.id] = [['S1', 'Servicetimme', null, 0, 'tim', 450], ['S2', 'Reservdel filter', '7350000000016', 18, 'st', 120], ['S3', 'Packning 32 mm', '7350000000023', 75, 'st', 15]]
        .map((x) => ({ companyId: c.id, articleNo: x[0], name: x[1], ean: x[2], stockQuantity: x[3], unit: x[4], purchasePrice: x[5], isStockItem: true }));
      state.articlesRefreshed[c.id] = now();
    }
    log(user.id, c.id, isNew ? 'company.created' : 'company.updated', `${c.name} (${c.companyPath})`);
    return json({ id: c.id });
  }
  if (match('POST', /^\/api\/admin\/companies\/(\d+)\/test$/)) {
    await sleep(900);
    const id = Number(rr()[1]);
    state.articlesRefreshed[id] = now();
    return json({ status: { name: 'DEMO', details: 'Visma Administration 2000 (simulerat)', writesToInventoryList: true, inventoryListRows: arts(id).length }, articles: arts(id).length });
  }
  if (match('GET', /^\/api\/admin\/users$/))
    return json(state.users.map((u) => ({ id: u.id, userName: u.userName, displayName: u.displayName, role: u.role, isActive: u.isActive, totpEnabled: false, requireTotp: !!u.requireTotp, locked: false, companyIds: u.companyIds })));
  if (match('POST', /^\/api\/admin\/users$/)) {
    const b = await req.json();
    if (!b.userName?.trim()) return err('Ange användarnamn.');
    if (state.users.some((u) => ci(u.userName, b.userName.trim()))) return err('Användarnamnet finns redan.', 409);
    const pw = b.password || 'demo';
    const u = { id: state.ids.user++, userName: b.userName.trim(), displayName: b.displayName ?? '', role: b.role === 'Admin' ? 'Admin' : 'Counter', password: pw, isActive: true, companyIds: b.companyIds ?? [] };
    state.users.push(u);
    log(user.id, null, 'user.created', u.userName);
    return json({ id: u.id, password: b.password ? null : pw });
  }
  if (match('PUT', /^\/api\/admin\/users\/(\d+)$/)) {
    const b = await req.json();
    const u = state.users.find((x) => x.id === Number(rr()[1])); if (!u) return err('', 404);
    const self = u.id === user.id;
    if (b.displayName != null) u.displayName = b.displayName;
    if (b.role === 'Admin' || b.role === 'Counter') { if (self && b.role !== 'Admin') return err('Du kan inte ta bort din egen admin-roll.'); u.role = b.role; }
    if (b.isActive != null) { if (self && !b.isActive) return err('Du kan inte inaktivera dig själv.'); u.isActive = b.isActive; }
    if (b.password) u.password = b.password;
    if (b.companyIds) u.companyIds = b.companyIds;
    if (b.requireTotp != null) u.requireTotp = !!b.requireTotp;
    return ok();
  }
  if (match('POST', /^\/api\/admin\/users\/(\d+)\/reset-totp$/)) return ok();
  if (match('GET', /^\/api\/admin\/audit$/))
    return json([...state.audit].reverse().slice(0, 500).map((a) => ({ ...a, user: uname(a.userId), company: state.companies.find((c) => c.id === a.companyId)?.name ?? null })));
  return null;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// ---------------------------------------------------------------- domän (samma logik som i C#)
async function sha256(text) {
  const buf = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return [...new Uint8Array(buf)].map((b) => b.toString(16).padStart(2, '0')).join('').toUpperCase();
}
async function hashSubmission(sub, lines) {
  const sorted = [...lines].sort((a, b) => a.id.localeCompare(b.id));
  let s = `v1|${sub.id}|${sub.sessionId}|${sub.userId}|${Date.parse(sub.submittedAt)}|${sub.signedWith}\n`;
  for (const l of sorted) s += `${l.id}|${l.articleNo}|${l.quantity}|${l.location ?? ''}|${l.comment ?? ''}|${Date.parse(l.countedAt)}|${l.userId}\n`;
  return sha256(s);
}
async function createSubmission(sessionId, userId, lines, signedWith, comment, at) {
  const sub = { id: state.ids.submission++, sessionId, userId, deviceId: 'demo', submittedAt: at ?? now(), lineCount: lines.length,
    totalQuantity: lines.reduce((n, l) => n + Number(l.quantity), 0), signedWith, comment, hash: '' };
  lines.forEach((l) => { l.submissionId = sub.id; });
  sub.hash = await hashSubmission(sub, lines);
  state.submissions.push(sub);
  const s = state.sessions.find((x) => x.id === sessionId);
  log(userId, s?.companyId, 'submission.created', `#${sub.id}: ${sub.lineCount} rader, ${signedWith}`, sub.submittedAt);
  return sub;
}

function summarize(s) {
  const cid = s.companyId;
  const snap = state.snapshots[s.id] ?? {};
  const lines = state.lines.filter((l) => l.sessionId === s.id && l.submissionId && !l.isVoided);
  const groups = new Map();
  for (const l of lines) { const k = l.articleNo.toLowerCase(); if (!groups.has(k)) groups.set(k, []); groups.get(k).push(l); }
  const compare = s.status === 'Open' || s.status === 'Locked';
  const articles = [...groups.values()].map((g) => {
    const no = g[0].articleNo, a = art(cid, no), sq = snap[no]?.q ?? 0;
    const counted = g.reduce((n, l) => n + Number(l.quantity), 0);
    const warnings = [];
    const byLoc = {};
    g.filter((l) => l.location).forEach((l) => { const k = l.location.trim().toLowerCase(); (byLoc[k] ??= new Set()).add(l.userId); });
    const dup = Object.entries(byLoc).filter(([, u]) => u.size > 1).map(([k]) => k.toUpperCase());
    if (dup.length) warnings.push('Samma plats räknad av flera personer: ' + dup.join(', '));
    const diff = Math.abs(counted - sq);
    if (sq === 0 ? counted !== 0 && diff >= 10 : diff / Math.abs(sq) >= 0.5) warnings.push('Stor avvikelse mot saldo');
    const cur = compare ? a?.stockQuantity ?? sq : sq;
    if (cur !== sq) warnings.push(`Saldot i Visma har ändrats under inventeringen (${sv(sq)} → ${sv(cur)})`);
    return { articleNo: no, name: a?.name ?? '', ean: a?.ean, unit: a?.unit, snapshotQuantity: sq, currentStock: cur, countedQuantity: counted,
      difference: counted - sq, lineCount: g.length, countedBy: [...new Set(g.map((l) => state.submissions.find((x) => x.id === l.submissionId)?.countedByName ?? display(l.userId)))], warnings, price: snap[no]?.p ?? a?.purchasePrice ?? null };
  }).sort((a, b) => a.articleNo.localeCompare(b.articleNo, 'sv'));
  const countedSet = new Set(articles.map((a) => a.articleNo.toLowerCase()));
  const uncounted = arts(cid).filter((a) => a.isStockItem && a.stockQuantity !== 0 && !countedSet.has(a.articleNo.toLowerCase()));
  return { articles, uncounted };
}

// Streckkoder
const normEan = (c) => (/^\d{12}$/.test(c) ? '0' + c : c);
const rulesFor = (cid) => state.rules.filter((x) => x.companyId == null || x.companyId === cid);
function applyRule(rule, b, cid) {
  Object.assign(rule, { name: (b.name ?? '').trim(), type: b.type === 'Gs1' || b.type === 1 ? 1 : 0, pattern: (b.pattern ?? '').trim(),
    quantityDecimals: Number(b.quantityDecimals) || 0, quantityMultiplier: Number(b.quantityMultiplier) || 1, isActive: !!b.isActive,
    description: b.description ?? null, companyId: b.allCompanies ? null : cid });
  if (!rule.name) return 'Ange ett namn.';
  if (rule.type === 0) {
    try { const rx = new RegExp(rule.pattern); if (!/\(\?<(art|ean)>/.test(rule.pattern)) return 'Mönstret måste innehålla gruppen (?<art>…) eller (?<ean>…).'; rx.test(''); }
    catch (e) { return 'Ogiltigt mönster: ' + e.message; }
  }
  return null;
}
function parseBarcode(cid, code, rules) {
  code = code.trim(); if (!code) return null;
  const alias = state.aliases.find((a) => a.companyId === cid && a.code === code);
  if (alias && art(cid, alias.articleNo)) return { article: art(cid, alias.articleNo), quantity: alias.quantity, via: `Extra streckkod (${sv(alias.quantity)} st)` };
  const e = byEan(cid, normEan(code)); if (e) return { article: e, quantity: null, via: 'EAN' };
  const a = art(cid, code); if (a) return { article: a, quantity: null, via: 'Artikelnr' };
  for (const r of rules.filter((x) => x.isActive).sort((x, y) => x.sortOrder - y.sortOrder || x.id - y.id)) {
    const res = r.type === 1 ? parseGs1Rule(cid, code, r) : parseRegexRule(cid, code, r);
    if (res) return res;
  }
  return null;
}
function parseRegexRule(cid, code, r) {
  let m; try { m = code.match(new RegExp(r.pattern)); } catch { return null; }
  if (!m) return null;
  const g = m.groups ?? {};
  let a = null, pack = 1;
  if (g.art) a = art(cid, g.art.trim()) ?? art(cid, g.art.replace(/^0+/, ''));
  if (!a && g.ean) { a = byEan(cid, normEan(g.ean)); const al = state.aliases.find((x) => x.companyId === cid && x.code === g.ean); if (!a && al) { a = art(cid, al.articleNo); pack = al.quantity; } }
  if (!a) return null;
  let qty = null;
  if (g.qty) qty = Number(g.qty.replace(',', '.')) / 10 ** r.quantityDecimals * r.quantityMultiplier * pack;
  else if (r.quantityMultiplier !== 1 || pack !== 1) qty = r.quantityMultiplier * pack;
  return { article: a, quantity: qty, via: `Regel: ${r.name}` };
}
function parseGs1Rule(cid, code, r) {
  const ais = parseAis(code); if (!ais) return null;
  const gtin = ais['01'] ?? ais['02']; if (!gtin) return null;
  const ean13 = gtin.length === 14 && gtin[0] === '0' ? gtin.slice(1) : gtin;
  let a = byEan(cid, ean13) ?? byEan(cid, gtin), pack = 1;
  if (!a) { const al = state.aliases.find((x) => x.companyId === cid && (x.code === gtin || x.code === ean13)); if (al) { a = art(cid, al.articleNo); pack = al.quantity; } }
  if (!a) return null;
  let qty = null;
  if (ais['30'] ?? ais['37']) qty = Number(ais['30'] ?? ais['37']);
  else { const w = Object.keys(ais).find((k) => /^310\d$/.test(k)); if (w) qty = Number(ais[w]) / 10 ** Number(w[3]); }
  if (qty != null) qty *= r.quantityMultiplier * pack; else if (r.quantityMultiplier !== 1 || pack !== 1) qty = r.quantityMultiplier * pack;
  return { article: a, quantity: qty, via: `Regel: ${r.name} (GS1)` };
}
function parseAis(code) {
  code = code.replace(/\]C1|\]d2|\]Q3/g, '');
  const out = {};
  if (code.startsWith('(')) {
    for (const m of code.matchAll(/\((\d{2,4})\)([^(]*)/g)) out[m[1]] = m[2].replace(/[\u001d ]/g, '');
    return Object.keys(out).length ? out : null;
  }
  const fixed = (ai) => ({ '00': 18, '01': 14, '02': 14, 11: 6, 12: 6, 13: 6, 15: 6, 16: 6, 17: 6, 20: 2 }[ai] ?? (/^3[1-6]\d\d$/.test(ai) ? 6 : null));
  const variable = ['10', '21', '22', '30', '37', '240', '241', '250', '251', '400', '401', '402', '403', '410', '411', '412', '413', '414', '415', '420', '421', '422', '90', '91', '92', '93', '94', '95', '96', '97', '98', '99'];
  let i = 0;
  while (i < code.length) {
    if (code[i] === '\u001d') { i++; continue; }
    let ai = null;
    for (const len of [2, 3, 4]) {
      const c = code.substr(i, len);
      if (c.length < len || !/^\d+$/.test(c)) break;
      if ((len !== 2 || !/^3[1-6]$/.test(c)) && fixed(c) != null) { ai = c; break; }
      if (variable.includes(c)) { ai = c; break; }
    }
    if (!ai) return Object.keys(out).length ? out : null;
    i += ai.length;
    const fl = fixed(ai);
    if (fl) { if (i + fl > code.length) return null; out[ai] = code.substr(i, fl); i += fl; }
    else { let end = code.indexOf('\u001d', i); if (end < 0) end = code.length; out[ai] = code.slice(i, end); i = end; }
  }
  return Object.keys(out).length ? out : null;
}

// ---------------------------------------------------------------- rapporter
const STATUS = { Open: 'Öppen', Locked: 'Låst – under granskning', Sent: 'Skickad till Visma', Cancelled: 'Avbruten' };
async function reportModel(s, company, user) {
  const sum = summarize(s);
  const results = state.results.filter((x) => x.sessionId === s.id);
  const subs = state.submissions.filter((x) => x.sessionId === s.id);
  const lines = state.lines.filter((l) => l.sessionId === s.id).sort((a, b) => a.countedAt.localeCompare(b.countedAt));
  const subsOut = [];
  for (const x of subs) {
    const ls = lines.filter((l) => l.submissionId === x.id);
    subsOut.push({ ...x, user: x.countedByName ?? display(x.userId), signed: { password: 'Lösenord', 'excel-import': 'Excel-import' }[x.signedWith] ?? 'Admin-korrigering', intact: (await hashSubmission(x, ls)) === x.hash && ls.length === x.lineCount });
  }
  const articles = sum.articles.map((a) => {
    const res = results.filter((x) => ci(x.articleNo, a.articleNo)).pop();
    return { ...a, diffValue: a.price != null ? Math.round(a.difference * a.price * 100) / 100 : null, sendStatus: res ? (res.success ? 'OK' : 'Fel: ' + res.message) : null };
  });
  const sumBy = (arr, f) => arr.reduce((n, x) => n + (f(x) ?? 0), 0);
  return {
    company, s, user, generatedAt: now(), articles,
    uncounted: sum.uncounted.map((a) => ({ ...a, value: a.purchasePrice != null ? a.stockQuantity * a.purchasePrice : null })),
    subs: subsOut, lines,
    drafts: lines.filter((l) => !l.isVoided && !l.submissionId).length,
    sendOk: results.filter((x) => x.success).length, sendFailed: results.filter((x) => !x.success).length,
    withDiff: articles.filter((a) => a.difference !== 0).length,
    plus: sumBy(articles.filter((a) => a.difference > 0), (a) => a.difference),
    minus: sumBy(articles.filter((a) => a.difference < 0), (a) => a.difference),
    net: sumBy(articles, (a) => a.diffValue),
    countedValue: sumBy(articles, (a) => a.countedQuantity * (a.price ?? 0)),
  };
}
const E = (x) => String(x ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const T = (t) => t ? new Date(t).toLocaleString('sv-SE', { dateStyle: 'short', timeStyle: 'short' }) : '–';
const N = (n) => n == null ? '' : Number(n).toLocaleString('sv-SE', { maximumFractionDigits: 3 });
const KR = (n) => n == null ? '' : Number(n).toLocaleString('sv-SE', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + ' kr';
const SG = (n) => (n > 0 ? '+' : '') + N(n);
const CL = (n) => n > 0 ? 'pos' : n < 0 ? 'neg' : '';

async function reportHtml(m, onlyDiff) {
  const s = m.s;
  const rows = onlyDiff ? m.articles.filter((a) => a.difference !== 0) : m.articles;
  const tot = (f) => rows.reduce((n, a) => n + (f(a) ?? 0), 0);
  return `<!doctype html><html lang="sv"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Inventeringsrapport – ${E(m.company.name)} – #${s.id} ${E(s.name)}</title><link rel="stylesheet" href="/css/report.css"><script src="/js/report.js" defer></script></head><body>
<div class="toolbar no-print"><button id="printBtn">Skriv ut / spara som PDF</button>
<a href="?onlyDiff=${onlyDiff ? 0 : 1}">${onlyDiff ? 'Visa alla artiklar' : 'Visa bara differenser'}</a><a href="report.xlsx">Ladda ner Excel</a>
<span style="margin-left:auto;color:#b26a00">DEMO – påhittade data</span></div>
<header class="head"><div><div class="company">${E(m.company.name)}</div><h1>Inventeringsrapport</h1><div class="sub">#${s.id} ${E(s.name)}</div></div>
<table class="meta"><tr><th>Status</th><td>${E(STATUS[s.status])}</td></tr><tr><th>Startad</th><td>${T(s.createdAt)} av ${E(display(s.createdByUserId))}</td></tr>
<tr><th>Låst</th><td>${T(s.lockedAt)}</td></tr><tr><th>Skickad till Visma</th><td>${T(s.sentAt)}${s.sentByUserId ? ' av ' + E(display(s.sentByUserId)) : ''}</td></tr>
<tr><th>Utskriven</th><td>${T(m.generatedAt)} av ${E(m.user.userName)}</td></tr></table></header>
<section class="kpis"><div><span>${m.articles.length}</span>räknade artiklar</div><div><span>${m.withDiff}</span>med differens</div>
<div><span class="pos">${SG(m.plus)}</span>övertal (antal)</div><div><span class="neg">${N(m.minus)}</span>undertal (antal)</div>
<div><span class="${CL(m.net)}">${KR(m.net)}</span>nettodifferens i värde</div><div><span>${KR(m.countedValue)}</span>räknat lagervärde</div>
<div><span>${m.subs.length}</span>inlämningar</div><div><span>${m.uncounted.length}</span>ej räknade med saldo</div></section>
${m.drafts ? `<p class="warn">${m.drafts === 1 ? '1 rad är inte inlämnad och ingår' : m.drafts + ' rader är inte inlämnade och ingår'} inte i rapporten.</p>` : ''}
${s.status === 'Sent' ? `<p class="ok">Skickat till Vismas inventeringsunderlag: ${m.sendOk} artiklar OK.</p>` : ''}
<h2>${onlyDiff ? 'Artiklar med differens' : 'Räknade artiklar'} (${rows.length})</h2>
<table class="data"><thead><tr><th>Artikelnr</th><th>Benämning</th><th>Enhet</th><th class="n">Saldo vid start</th><th class="n">Räknat</th><th class="n">Differens</th><th class="n">Inpris</th><th class="n">Värde diff.</th><th>Räknat av</th><th>Anm.</th></tr></thead><tbody>
${rows.map((a) => `<tr><td>${E(a.articleNo)}</td><td>${E(a.name)}</td><td>${E(a.unit)}</td><td class="n">${N(a.snapshotQuantity)}</td><td class="n b">${N(a.countedQuantity)}</td>
<td class="n ${CL(a.difference)}">${SG(a.difference)}</td><td class="n">${KR(a.price)}</td><td class="n ${CL(a.diffValue)}">${KR(a.diffValue)}</td><td>${E(a.countedBy.join(', '))}</td><td class="note">${E(a.warnings.join('; '))}</td></tr>`).join('')}
</tbody><tfoot><tr><td colspan="3">Summa</td><td class="n">${N(tot((a) => a.snapshotQuantity))}</td><td class="n">${N(tot((a) => a.countedQuantity))}</td><td class="n">${SG(tot((a) => a.difference))}</td><td></td><td class="n">${KR(tot((a) => a.diffValue))}</td><td colspan="2"></td></tr></tfoot></table>
${m.uncounted.length && !onlyDiff ? `<h2>Lagerartiklar med saldo som inte räknats (${m.uncounted.length})</h2><table class="data"><thead><tr><th>Artikelnr</th><th>Benämning</th><th>Enhet</th><th class="n">Saldo</th><th class="n">Inpris</th><th class="n">Värde</th></tr></thead><tbody>
${m.uncounted.map((u) => `<tr><td>${E(u.articleNo)}</td><td>${E(u.name)}</td><td>${E(u.unit)}</td><td class="n">${N(u.stockQuantity)}</td><td class="n">${KR(u.purchasePrice)}</td><td class="n">${KR(u.value)}</td></tr>`).join('')}</tbody></table>` : ''}
<h2>Inlämningar (signerade av räknarna)</h2><table class="data"><thead><tr><th>#</th><th>Räknare</th><th>Inlämnad</th><th class="n">Rader</th><th class="n">Totalt antal</th><th>Signerad med</th><th>Kontrollsumma</th><th>Kommentar</th></tr></thead><tbody>
${m.subs.map((x) => `<tr><td>${x.id}</td><td>${E(x.user)}</td><td>${T(x.submittedAt)}</td><td class="n">${x.lineCount}</td><td class="n">${N(x.totalQuantity)}</td><td>${E(x.signed)}</td><td>${x.intact ? '<span class="ok">Intakt</span>' : '<span class="neg">Ändrad efter signering</span>'} <code>${E(x.hash.slice(0, 12))}</code></td><td>${E(x.comment)}</td></tr>`).join('') || '<tr><td colspan="8">Inga inlämningar.</td></tr>'}
</tbody></table>
<section class="sign"><div><div class="line"></div>Inventeringsansvarig – namnförtydligande</div><div><div class="line"></div>Underskrift</div><div><div class="line"></div>Datum</div></section>
<footer>${E(m.company.name)} · Inventering #${s.id} · ${E(m.company.companyPath)} · Utskriven ${T(m.generatedAt)} · DEMO</footer></body></html>`;
}

function reportXlsx(m) {
  const s = m.s;
  const wb = XLSX.utils.book_new();
  const sheet = (name, rows, widths, filter = true) => {
    const ws = XLSX.utils.aoa_to_sheet(rows);
    ws['!cols'] = widths.map((w) => ({ wch: w }));
    if (filter && rows.length > 1) ws['!autofilter'] = { ref: XLSX.utils.encode_range({ s: { r: 0, c: 0 }, e: { r: rows.length - 1, c: rows[0].length - 1 } }) };
    XLSX.utils.book_append_sheet(wb, ws, name);
  };
  const d = (t) => (t ? new Date(t) : '');
  sheet('Sammanfattning', [
    ['Inventeringsrapport', ''], [m.company.name, ''], ['', ''],
    ['Inventering', `#${s.id} ${s.name}`], ['Status', STATUS[s.status]], ['Startad', d(s.createdAt)], ['Startad av', display(s.createdByUserId)],
    ['Låst', d(s.lockedAt)], ['Skickad till Visma', d(s.sentAt)], ['Visma-mapp', m.company.companyPath], ['Utskriven', d(m.generatedAt)], ['', ''],
    ['Räknade artiklar', m.articles.length], ['Med differens', m.withDiff], ['Övertal (antal)', m.plus], ['Undertal (antal)', m.minus],
    ['Nettodifferens i värde (kr)', m.net], ['Räknat lagervärde (kr)', m.countedValue], ['Inlämningar', m.subs.length],
    ['Ej räknade lagerartiklar med saldo', m.uncounted.length], ['', ''], ['DEMO – påhittade data', ''],
  ], [34, 50], false);
  sheet('Artiklar', [['Artikelnr', 'Benämning', 'Enhet', 'Saldo vid start', 'Räknat', 'Differens', 'Inpris', 'Värde differens', 'Räknat av', 'Varningar', 'Visma'],
    ...m.articles.map((a) => [a.articleNo, a.name, a.unit ?? '', a.snapshotQuantity, a.countedQuantity, a.difference, a.price ?? '', a.diffValue ?? '', a.countedBy.join(', '), a.warnings.join('; '), a.sendStatus ?? '']),
    ['Summa', '', '', m.articles.reduce((n, a) => n + a.snapshotQuantity, 0), m.articles.reduce((n, a) => n + a.countedQuantity, 0), m.plus + m.minus, '', m.net, '', '', '']],
  [12, 36, 7, 14, 10, 10, 10, 14, 24, 40, 10]);
  sheet('Ej räknade', [['Artikelnr', 'Benämning', 'Enhet', 'Saldo', 'Inpris', 'Värde'], ...m.uncounted.map((u) => [u.articleNo, u.name, u.unit ?? '', u.stockQuantity, u.purchasePrice ?? '', u.value ?? ''])], [12, 36, 7, 10, 10, 12]);
  sheet('Inlämningar', [['Inlämning', 'Räknare', 'Inlämnad', 'Rader', 'Totalt antal', 'Signerad med', 'Intakt', 'Kontrollsumma (SHA-256)', 'Kommentar'],
    ...m.subs.map((x) => [x.id, x.user, d(x.submittedAt), x.lineCount, x.totalQuantity, x.signed, x.intact ? 'Ja' : 'NEJ – ändrad', x.hash, x.comment ?? ''])], [10, 18, 18, 8, 12, 16, 10, 68, 30]);
  sheet('Alla rader', [['Artikelnr', 'Benämning', 'Antal', 'Plats', 'Kommentar', 'Skannad kod', 'Tolkad via', 'Räknad', 'Räknare', 'Inlämning', 'Struken', 'Rad-id'],
    ...m.lines.map((l) => [l.articleNo, art(s.companyId, l.articleNo)?.name ?? '', l.quantity, l.location ?? '', l.comment ?? '', l.scannedCode ?? '', l.scanVia ?? '', d(l.countedAt), display(l.userId), l.submissionId ?? 'Utkast', l.isVoided ? 'Ja' : '', l.id])],
  [12, 36, 8, 8, 20, 16, 18, 18, 16, 10, 8, 38]);
  return XLSX.write(wb, { type: 'array', bookType: 'xlsx', cellDates: true });
}
