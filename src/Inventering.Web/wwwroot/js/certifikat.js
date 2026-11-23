// Sida för att installera tjänstens certifikat i telefonen. Nås även över http.
import { $, esc, icon } from './common.js';

const main = $('#main');
const ua = navigator.userAgent;
const platform = /iPhone|iPad|iPod/.test(ua) ? 'ios' : /Android/.test(ua) ? 'android' : 'desktop';

const STEPS = {
  ios: [
    'Tryck på <b>Hämta certifikat</b> och välj <b>Tillåt</b>.',
    'Öppna <b>Inställningar</b> och tryck på <b>Profil hämtad</b> högst upp. Välj <b>Installera</b> och bekräfta med telefonens lösenkod (den du låser upp telefonen med).',
    'Gå till <b>Inställningar → Allmänt → Om → Certifikatförtroende</b> och slå på <b>Inventering rotcertifikat</b>.',
  ],
  android: [
    'Tryck på <b>Hämta certifikat</b>.',
    'Öppna <b>Inställningar</b>, sök på <b>certifikat</b> och välj <b>Installera certifikat → CA-certifikat</b>. Bekräfta med <b>Installera ändå</b>.',
    'Välj filen <b>inventering-rotcertifikat.cer</b> bland hämtade filer.',
  ],
  desktop: [
    'Hämta certifikatet och dubbelklicka på filen.',
    'Välj <b>Installera certifikat → Lokal dator → Placera alla certifikat i följande arkiv → Betrodda rotcertifikatutfärdare</b>.',
    'Datorer i domänen kan i stället få certifikatet via grupprincip.',
  ],
};

async function start() {
  const info = await fetch('/api/cert-info').then((r) => r.json());
  const steps = (list) => `<ol class="m-steps">${list.map((s) => `<li>${s}</li>`).join('')}</ol>`;
  const openBtn = `<a class="m-btn m-btn-primary" href="${esc(info.httpsUrl)}/">Öppna inventeringen</a>`;

  let body;
  if (!info.enabled) {
    body = `<p class="m-sub">Tjänsten är installerad utan https. Kameran i mobilen fungerar inte, men handskannrar och sökfältet gör det.</p>
      <div class="m-actions"><a class="m-btn m-btn-primary" href="/">Öppna inventeringen</a></div>`;
  } else if (info.isHttps && info.mode !== 'SelfSigned') {
    body = `<div class="m-receipt"><div class="ok">${icon('check', 28)}</div><h2>Certifikatet fungerar</h2>
      <p class="m-sub">Den här enheten litar på tjänsten. Kameran kan användas för skanning.</p></div>
      <div class="m-actions"><a class="m-btn m-btn-primary" href="/">Öppna inventeringen</a></div>`;
  } else if (info.mode === 'InternalCa') {
    body = `<p class="m-sub">Tjänsten använder ett eget certifikat för det interna nätet. Installera det en gång i telefonen, så försvinner säkerhetsvarningarna och kameran fungerar.</p>
      ${info.hasRootCa ? `<div class="m-actions"><a class="m-btn m-btn-secondary" href="/ca.cer" download>${icon('download', 18)}Hämta certifikat</a></div>` : '<p class="m-note bad">Certifikatfilen saknas på servern. Kör installationen igen.</p>'}
      <div class="m-section-h" style="padding:18px 0 4px"><b>${platform === 'ios' ? 'iPhone' : platform === 'android' ? 'Android' : 'Dator'}</b></div>
      ${steps(STEPS[platform])}
      <div class="m-actions">${openBtn}</div>`;
  } else if (info.mode === 'SelfSigned') {
    body = `<p class="m-sub">Tjänsten använder ett självsignerat certifikat. Första gången visar webbläsaren en varning.</p>
      ${steps(platform === 'ios'
        ? ['Tryck på <b>Öppna inventeringen</b>.', 'Välj <b>Visa detaljer → besök webbplatsen</b> och bekräfta.', 'Varningen kan komma tillbaka. Installera gärna certifikatet nedan för att slippa den.']
        : ['Tryck på <b>Öppna inventeringen</b>.', 'Välj <b>Avancerat → Fortsätt till webbplatsen</b>.', 'Varningen kan komma tillbaka. Installera gärna certifikatet nedan för att slippa den.'])}
      <div class="m-actions">${openBtn}
        ${info.hasRootCa ? `<a class="m-btn m-btn-secondary" href="/ca.cer" download>${icon('download', 18)}Hämta certifikat</a>` : ''}</div>`;
  } else {
    body = `<p class="m-sub">Tjänsten använder ett betrott certifikat. Inget behöver installeras.</p><div class="m-actions">${openBtn}</div>`;
  }

  // På en dator: visa QR-kod så att telefonen kan öppna sidan direkt.
  let qr = '';
  if (platform === 'desktop' && window.qrcode) {
    const url = location.href.split('#')[0];
    const q = qrcode(0, 'M'); q.addData(url); q.make();
    qr = `<div class="m-qr"><div class="qr">${q.createImgTag(5, 8)}</div><p class="m-sub">Skanna med telefonens kamera för att öppna den här sidan.</p></div>`;
  }

  main.innerHTML = `<div class="m-hero"><h1>Certifikat</h1><p class="m-meta">Inventering</p></div>
    <div class="m-cert">${body}${qr}</div>`;
}

start().catch((e) => { main.innerHTML = `<p class="m-note bad">${esc(e.message)}</p>`; });
