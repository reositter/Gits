// DEMO: startar låtsasservern (service worker) innan appen gör sina första anrop,
// och visar en banner med demoinloggningar och knapp för att återställa testdatan.
(function () {
  if (!('serviceWorker' in navigator)) {
    document.addEventListener('DOMContentLoaded', () => {
      document.body.innerHTML = '<p style="padding:24px;font:16px system-ui">Demon kräver en modern webbläsare (service workers).</p>';
    });
    return;
  }
  if (!navigator.serviceWorker.controller) {
    // Första besöket: göm sidan tills låtsasservern tagit över, ladda sedan om.
    document.documentElement.style.visibility = 'hidden';
    navigator.serviceWorker.register('/mock-sw.js', { scope: '/' });
    navigator.serviceWorker.addEventListener('controllerchange', () => location.reload());
    window.fetch = () => new Promise(() => {}); // appens anrop väntar tills omladdningen
    return;
  }
  navigator.serviceWorker.register('/mock-sw.js', { scope: '/' }); // uppdatera vid ny version

  document.addEventListener('DOMContentLoaded', () => {
    const admin = location.pathname.endsWith('admin.html');
    const bar = document.createElement('div');
    bar.className = 'demo-banner';
    bar.innerHTML = `
      <b>DEMO</b>
      <span>Påhittade data – ingen koppling till Visma. Logga in med <code>admin</code>, <code>anna</code> eller <code>bo</code>, lösenord <code>demo</code>.</span>
      <a href="${admin ? '/' : '/admin.html'}">${admin ? 'Till mobilsidan' : 'Till admin'}</a>
      <button type="button" id="demoReset">Återställ demodata</button>`;
    document.body.prepend(bar);
    document.getElementById('demoReset').onclick = async () => {
      if (!confirm('Återställa all demodata?')) return;
      await fetch('/api/__demo/reset', { method: 'POST', headers: { 'X-Requested-With': 'inventering' } });
      try { localStorage.clear(); } catch { /* ignore */ }
      location.reload();
    };
  });
})();
