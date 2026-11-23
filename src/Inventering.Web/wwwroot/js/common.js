// Gemensamt för mobil- och admin-sidan: API-anrop, inloggning, tvåfaktor.
import { icon } from './icons.js';
export { icon };
export const $ = (sel, root = document) => root.querySelector(sel);
export const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
export const fmt = (n) => n == null ? '' : Number(n).toLocaleString('sv-SE', { maximumFractionDigits: 3 });
export const fmtTime = (t) => t ? new Date(t).toLocaleString('sv-SE', { dateStyle: 'short', timeStyle: 'short' }) : '';

export class ApiError extends Error {
  constructor(status, body) { super(body?.error || `Fel ${status}`); this.status = status; this.body = body; }
}

export async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    credentials: 'same-origin',
    headers: { 'X-Requested-With': 'inventering', ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = { error: text }; }
  if (res.status === 409 && data?.needCompany) {
    // Bolaget är inte valt (eller behörigheten har ändrats) – visa bolagsväljaren igen.
    sessionStorage.setItem('inv.needCompany', '1');
    location.reload();
    await new Promise(() => {});
  }
  if (!res.ok) throw new ApiError(res.status, data);
  return data;
}

const MSG_ICON = { ok: 'check', warn: 'alert', bad: 'alert' };
export function showMsg(el, text, kind = 'bad') {
  el.innerHTML = text ? `<div class="msg ${kind}">${icon(MSG_ICON[kind] ?? 'alert', 16)}<div class="grow">${esc(text)}</div></div>` : '';
}

/** Visar inloggning i <main> och resolvar med användaren när man är inne (inkl. ev. TOTP-aktivering). */
export async function ensureLoggedIn(main, { requireAdmin = false } = {}) {
  let me = await api('GET', '/api/auth/me').catch(() => null);
  while (!me) {
    await loginForm(main);
    me = await api('GET', '/api/auth/me').catch(() => null);
  }
  if (me.mustSetupTotp) {
    await totpSetup(main, me);
    me = await api('GET', '/api/auth/me');
  }
  if (!me.company || sessionStorage.getItem('inv.needCompany')) {
    sessionStorage.removeItem('inv.needCompany');
    await companyPicker(main, me);
    me = await api('GET', '/api/auth/me');
  }
  showCompany(me);
  if (requireAdmin && me.role !== 'Admin') {
    main.innerHTML = `<div class="card"><h2>Ingen behörighet</h2><p>Sidan kräver administratör.</p><a href="/">Till räkning</a></div>`;
    throw new Error('not admin');
  }
  if (requireAdmin && me.totpEnabled && !me.mfa) {
    // Inloggad utan TOTP (t.ex. aktiverat i annan flik) – logga in igen.
    await api('POST', '/api/auth/logout');
    return ensureLoggedIn(main, { requireAdmin });
  }
  return me;
}

/** Visar valt bolag i sidhuvudet med knapp för att byta. */
function showCompany(me) {
  const el = document.getElementById('companyBtn');
  if (!el || !me.company) return;
  el.innerHTML = `${icon('building', 16)}<span>${esc(me.company.name)}</span>${me.companies.length > 1 ? icon('down', 14) : ''}`;
  el.title = me.companies.length > 1 ? 'Byt bolag' : 'Bolag';
  el.classList.remove('hidden');
  el.disabled = me.companies.length < 2;
  el.onclick = async () => {
    if (window.beforeCompanySwitch && !window.beforeCompanySwitch()) return;
    await companyPicker(document.getElementById('main'), me);
    location.reload();
  };
}

/** Låter användaren välja bolag. Ett bolag väljs automatiskt. */
function companyPicker(main, me) {
  return new Promise((resolve) => {
    if (me.companies.length === 0) {
      main.innerHTML = `<div class="card login"><h2>Inga bolag</h2><p>Du har inte behörighet till något bolag, eller så är inga bolag upplagda.
        ${me.role === 'Admin' ? 'Lägg till bolag under Admin → Bolag.' : 'Kontakta administratören.'}</p>
        ${me.role === 'Admin' ? '<a href="/admin.html#companies">Till admin</a>' : ''}
        <button id="lo" style="margin-top:12px">Logga ut</button></div>`;
      document.getElementById('lo').onclick = logout;
      if (me.role === 'Admin' && location.pathname.endsWith('admin.html')) resolve();
      return;
    }
    if (me.companies.length === 1) {
      api('POST', '/api/auth/company', { companyId: me.companies[0].id }).then(resolve);
      return;
    }
    main.innerHTML = `
      <div class="login"><div class="card">
        <h2>Välj bolag</h2>
        <p class="muted" style="margin:0 0 8px">Vilket bolag ska du arbeta i?</p>
        <ul class="list">${me.companies.map((c) => `
          <li><span class="secondary">${icon('building', 18)}</span><div class="grow"><b>${esc(c.name)}</b></div>
          <button class="${me.company?.id === c.id ? '' : 'primary'} shrink small" data-id="${c.id}">${me.company?.id === c.id ? 'Valt' : 'Välj'}</button></li>`).join('')}</ul>
      </div></div>`;
    main.querySelectorAll('button[data-id]').forEach((b) => b.onclick = async () => {
      await api('POST', '/api/auth/company', { companyId: Number(b.dataset.id) });
      resolve();
    });
  });
}

function loginForm(main) {
  return new Promise((resolve) => {
    main.innerHTML = `
      <div class="login"><form class="card" id="login" autocomplete="on">
        <h2>Logga in</h2>
        <p class="muted" style="margin:0 0 4px">Lagerinventering mot Visma Administration</p>
        <label for="u">Användarnamn</label>
        <input id="u" name="username" autocomplete="username" autocapitalize="none" required>
        <label for="p">Lösenord</label>
        <input id="p" name="password" type="password" autocomplete="current-password" required>
        <div id="totpRow" class="hidden">
          <label for="t">Engångskod från autentiseringsappen</label>
          <input id="t" name="otp" inputmode="numeric" autocomplete="one-time-code" maxlength="6" pattern="[0-9 ]*">
        </div>
        <div id="lmsg"></div>
        <button class="primary big" style="margin-top:16px">Logga in</button>
      </form></div>`;
    const f = $('#login', main);
    $('#u', f).focus();
    f.addEventListener('submit', async (e) => {
      e.preventDefault();
      const btn = f.querySelector('button'); btn.disabled = true;
      try {
        const r = await api('POST', '/api/auth/login', { userName: $('#u', f).value, password: $('#p', f).value, totpCode: $('#t', f).value || null });
        if (r?.needTotp) {
          $('#totpRow', f).classList.remove('hidden'); $('#t', f).focus();
          showMsg($('#lmsg', f), 'Ange koden från din autentiseringsapp.', 'warn');
        } else resolve();
      } catch (err) {
        if (err.body?.needTotp) $('#totpRow', f).classList.remove('hidden');
        showMsg($('#lmsg', f), err.status === 429 ? 'För många försök, vänta en minut.' : err.message);
      } finally { btn.disabled = false; }
    });
  });
}

async function totpSetup(main, me) {
  const s = await api('POST', '/api/auth/totp/setup');
  const qr = qrcode(0, 'M'); // från /lib/qrcode.js
  qr.addData(s.uri); qr.make();
  return new Promise((resolve) => {
    main.innerHTML = `
      <div class="login" style="max-width:460px"><form class="card" id="totp">
        <h2>Aktivera tvåfaktorsinloggning</h2>
        <p>Kontot <b>${esc(me.userName)}</b> kräver tvåfaktor. Skanna koden med Microsoft Authenticator,
        Google Authenticator eller liknande och ange sedan den sexsiffriga koden.</p>
        <div class="qr">${qr.createImgTag(5, 8)}</div>
        <p class="muted">Kan du inte skanna? Ange nyckeln manuellt:<br><code>${esc(s.secret.replace(/(.{4})/g, '$1 ').trim())}</code></p>
        <label for="c">Kod</label>
        <input id="c" inputmode="numeric" autocomplete="one-time-code" maxlength="7" required>
        <div id="tmsg"></div>
        <button class="primary big" style="margin-top:16px">Aktivera</button>
      </form></div>`;
    const f = $('#totp', main);
    $('#c', f).focus();
    f.addEventListener('submit', async (e) => {
      e.preventDefault();
      try { await api('POST', '/api/auth/totp/confirm', { code: $('#c', f).value }); resolve(); }
      catch (err) { showMsg($('#tmsg', f), err.message); }
    });
  });
}

export async function logout() {
  await api('POST', '/api/auth/logout').catch(() => {});
  location.reload();
}
