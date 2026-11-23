# Demo för Cloudflare Pages

En statisk demo av inventeringsappen. Mobil- och admin-sidorna är **samma filer** som i den
riktiga appen (`src/Inventering.Web/wwwroot`), men API:t ersätts av en låtsasserver som körs i
webbläsaren (`static/mock-sw.js`, en service worker) med påhittade data.

- Ingen server, ingen databas och ingen koppling till Visma.
- Varje besökare får egna demodata i sin webbläsare, och knappen **Återställ demodata** nollställer dem.
- Inloggningar: `admin`, `anna` och `bo`, alla med lösenordet `demo`.
  Användarna hanteras i appen och är inte Visma-användare.
- Rapporten (HTML/PDF) och Excel-filen skapas i webbläsaren. Excel byggs med SheetJS, så
  formateringen är enklare än i den riktiga appen.
- Kameraskanning fungerar på mobilen, eftersom Cloudflare Pages kör HTTPS.

## Bygga

```bash
node demo/build.mjs      # → demo/dist
```

Testa lokalt med `cd demo/dist && python3 -m http.server 8090` och öppna http://localhost:8090.
Service workers kräver https eller localhost.

## Publicera på Cloudflare Pages

**Alternativ A: koppla GitHub-repot (rekommenderas, ingen token behövs)**

1. dash.cloudflare.com → **Workers & Pages** → **Create** → **Pages** → **Connect to Git**.
2. Välj repot `reositter/Gits` och grenen `claude/visma-inventory-mobile-scan-bqdoyv`, eller `main` efter merge.
3. Bygginställningar:
   - Framework preset: **None**
   - Build command: `node demo/build.mjs`
   - Build output directory: `demo/dist`
4. **Save and Deploy**. Demon hamnar på `https://<projektnamn>.pages.dev`. Den byggs om automatiskt vid varje push.

**Alternativ B: från kommandoraden**

```bash
node demo/build.mjs
npx wrangler pages deploy demo/dist --project-name inventering-demo
```

Vill ni begränsa åtkomsten till demon kan **Cloudflare Access** läggas framför den, t.ex. inloggning med e-postkod.
