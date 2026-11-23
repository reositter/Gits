# Inventering för Visma Administration 1000/2000

Mobilanpassad webbapp för lagerinventering. Räknare skannar streckkoder med mobilens kamera
(eller skriver artikelnr/EAN/söker på namn). En admin granskar, godkänner och skickar resultatet
till Visma Administration via ADK. Bakgrund och designbeslut finns i [docs/ARKITEKTUR.md](docs/ARKITEKTUR.md).

## Status

| Del | Status |
|---|---|
| Mobilsida: kamera-skanning, manuell inmatning, sök på namn, offline-kö | Klar |
| Flera mobiler samtidigt, summering per artikel, varningar | Klar |
| Admin: starta/låsa/granska/stryka rader/korrigera/skicka, CSV-export | Klar |
| Inloggning, roller, kontospärr, TOTP-tvåfaktor, händelselogg | Klar |
| **Koppling mot Visma (ADK)** | **Ej klar** – `AdkVismaGateway` är ett skal. Fylls i med befintlig ADK-kod. |

Tills ADK-delen är klar körs tjänsten mot en låtsas-Visma (`"Visma:Mode": "Fake"`) med några testartiklar.

## Projekt

```
src/Inventering.Core    Domän, summering, IVismaGateway, Fake- och ADK-gateway
src/Inventering.Web     ASP.NET Core (.NET 8): API, SQLite, inloggning, wwwroot (mobil + admin)
tests/Inventering.Tests Enhetstester och API-flödestester
```

## Köra lokalt

```bash
dotnet test
cd src/Inventering.Web
ASPNETCORE_ENVIRONMENT=Development dotnet run --urls http://0.0.0.0:5080
```

- Räknare: `http://<dator>:5080/`
- Admin: `http://<dator>:5080/admin.html`
- Första gången skapas användaren `admin`. Lösenordet skrivs till `forsta-admin-losenord.txt`
  i datakatalogen. Byt lösenordet och radera filen.
- I Development krävs inte tvåfaktor för admin. I produktion krävs det.

## Installera på Visma-servern (Windows)

1. Publicera. Använd `win-x86` om ADK-DLL:en är 32-bitars, vilket den normalt är:
   ```
   dotnet publish src/Inventering.Web -c Release -r win-x86 --self-contained -o C:\Inventering
   ```
2. Ställ in `C:\Inventering\appsettings.json`:
   - `Visma:Mode` = `Adk` och sökvägar under `Visma:Adk`
   - HTTPS, se nedan
3. Installera och starta som tjänst (kör som administratör):
   ```
   sc.exe create Inventering binPath= "C:\Inventering\Inventering.Web.exe" start= delayed-auto
   sc.exe start Inventering
   ```
   Tjänstekontot måste ha läs- och skrivrätt till Visma-företagets katalog.
4. Öppna porten i Windows-brandväggen, bara för lokalnätet.
5. Data (SQLite, nycklar, första lösenord) hamnar i `C:\ProgramData\Inventering`. Ta med katalogen i backupen.

### HTTPS (krävs för kameran)

Mobilens webbläsare tillåter bara kameran över HTTPS. Bästa lösningen är ett riktigt certifikat
för ett internt namn, t.ex. `inventering.foretaget.se`, som i intern DNS pekar på serverns LAN-IP.
Certifikatet tas ut från Let's Encrypt via DNS-validering, t.ex. med win-acme. Se [docs/ARKITEKTUR.md](docs/ARKITEKTUR.md) §8.

```json
"Kestrel": {
  "Endpoints": {
    "Https": {
      "Url": "https://0.0.0.0:443",
      "Certificate": { "Path": "C:\\Inventering\\cert.pfx", "Password": "..." }
    }
  }
}
```

Det går också att läsa certifikatet från Windows certifikatarkiv, med `"Subject"`, `"Store": "My"` och `"Location": "LocalMachine"`.
Utan HTTPS fungerar sökfältet och handskannrar i tangentbordsläge, men inte kameran.

### Öppna från Visma Administration

Lägg in en länk eller integrationspost, enligt befintligt upplägg med `dynamic.xml`, som öppnar
`https://inventering.foretaget.se/admin.html` i webbläsaren.

## Säkerhetsinställningar (`Security`)

| Inställning | Standard | Kommentar |
|---|---|---|
| `RequireTotpForAdmin` | true | Admin måste aktivera TOTP vid första inloggningen |
| `RequireTotpForAll` | false | Slå på om tjänsten nås utanför LAN |
| `MaxFailedLogins` / `LockoutMinutes` | 5 / 15 | Kontospärr |
| `SessionHours` | 12 | Inloggningens livslängd |
| `MinPasswordLength` | 10 | |

Övrigt skydd:
- Lösenorden hashas med PBKDF2, samma metod som ASP.NET Identity.
- Inloggningsförsök begränsas per IP-adress.
- Cookies är SameSite=Strict och kombineras med ett CSRF-huvud på alla ändrande anrop.
- Säkerhetshuvuden (CSP m.fl.) sätts på alla svar.
- Alla sessioner loggas ut när en användares roll, lösenord eller status ändras.
- Viktiga händelser hamnar i händelseloggen.

## Nästa steg

1. Implementera `AdkVismaGateway` med befintlig ADK-kod: läsa artiklar (artikelnr, benämning,
   EAN, enhet, saldo, lagerförd) och skriva inventerat antal. Testa i ett testföretag först.
2. Ta fram certifikat och DNS-namn, och installera på servern.
3. Gå över till EF Core-migreringar innan första produktionssättningen. Schemat skapas nu med `EnsureCreated`.
