// Bygger den statiska demon: kopierar appens wwwroot och lägger till låtsasservern.
// Kör: node demo/build.mjs   → demo/dist (publiceras till Cloudflare Pages)
import { cpSync, rmSync, readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const dist = join(here, 'dist');
rmSync(dist, { recursive: true, force: true });
mkdirSync(dist, { recursive: true });
cpSync(join(here, '..', 'src', 'Inventering.Web', 'wwwroot'), dist, { recursive: true });
cpSync(join(here, 'static'), dist, { recursive: true });

for (const page of ['index.html', 'admin.html']) {
  const file = join(dist, page);
  let html = readFileSync(file, 'utf8');
  html = html.replace('<head>', '<head>\n  <script src="/demo-boot.js"></script>\n  <link rel="stylesheet" href="/demo.css">');
  html = html.replace(/<title>(.*?)<\/title>/, '<title>$1 – DEMO</title>');
  writeFileSync(file, html);
}
console.log('Demo byggd i', dist);
