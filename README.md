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
| Streckkodsregler per kund (regex, GS1-128/DataMatrix) och extra streckkoder (t.ex. kartong = 12 st) | Klar |
| Signerad inlämning per användare, kontrollsumma, spårbarhet på radnivå | Klar |
| Koppling mot Visma via ADK (AvSynkLib), läser artiklar och skriver till inventeringsunderlaget | Byggd, **ej testad mot riktig Visma** |

`"Visma:Mode": "Fake"` ger en låtsas-Visma med testartiklar (standard). `"Adk"` kopplar mot riktig Visma.

## Projekt

```
src/Inventering.Core       Domän, summering, IVismaGateway, Fake- och ADK-gateway
src/Inventering.Web        ASP.NET Core (.NET 8): API, SQLite, inloggning, wwwroot (mobil + admin)
src/Inventering.AdkBridge  .NET Framework 4.8 x86-konsol som gör ADK-anropen (AvSynkLib/AdkNet4Wrapper)
lib/                       AvSynkLib.dll + AdkNet4Wrapper.dll – kopieras hit, checkas inte in
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

## Visma-kopplingen (ADK)

`AdkNet4Wrapper.dll` är C++/CLI för .NET Framework och kan inte laddas i .NET 8. Därför gör en
separat 32-bitars process, `Inventering.AdkBridge.exe` (.NET Framework 4.8), allt ADK-arbete.
Webbtjänsten startar den per anrop, skickar JSON via stdin och läser JSON från stdout. Lösenord
skickas aldrig som argument. Det isolerar också ADK:s minnesläckor och krascher.

| Kommando | ADK | Vad |
|---|---|---|
| `status` | `AvCompany`, `AvInventoryArticle` | Produkt, ADK-version och antal rader i inventeringsunderlaget |
| `articles` | `AvArticle` | Artikelnr, benämning, `BarCode` (EAN), enhet, `QuantityInStock`, `StockGoods`, `Inactive` |
| `inventory-write` | `AvInventoryArticle` | Sätter `Quantity` och `Date` per artikel i **Vismas inventeringsunderlag** |

**Arbetsgång i Visma:**
1. Skapa inventeringsunderlaget i Visma Administration.
2. Räkna med mobilerna, lämna in, granska och skicka från admin-sidan. Räknat antal hamnar då i underlaget.
3. Slutför inventeringen i Visma, så att lagret uppdateras från underlaget. Det är ett sista
   godkännande i Visma, och Vismas egen inventeringshistorik och lagervärdering används.

Artiklar som saknas i underlaget rapporteras som fel per artikel. Under **Kontrollera Visma** på
admin-sidan syns om underlaget finns.

## Installera på Visma-servern (Windows)

1. Kopiera `AvSynkLib.dll` och `AdkNet4Wrapper.dll` till `lib/`, t.ex. från `_lib` i ett befintligt ADK-projekt.
2. Publicera webbtjänsten och bryggan:
   ```
   dotnet publish src/Inventering.Web -c Release -r win-x64 --self-contained -o C:\Inventering
   dotnet publish src/Inventering.AdkBridge -c Release -o C:\Inventering\AdkBridge
   ```
   Kopiera även `lib\*.dll` till `C:\Inventering\AdkBridge` om de inte kom med.
3. Ställ in `C:\Inventering\appsettings.json`:
   - `Visma:Mode` = `Adk`
   - `Visma:Adk:CompanyPath` = företagsmappen, t.ex. `...\Företag\FTG3`
   - `Visma:Adk:User` och `Password` om inloggning är aktiverad i Visma
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

## Streckkoder

Admin → **Streckkoder**. En skannad kod tolkas i den här ordningen:

1. **Extra streckkod.** En kod kopplas till en artikel och ett antal per skanning, t.ex. kartong-EAN = 12 st.
   Visma har bara ett streckkodsfält per artikel.
2. **EAN eller artikelnr** från Visma, med exakt träff.
3. **Regler** uppifrån och ned:
   - **Regex** med grupperna `(?<art>…)` eller `(?<ean>…)`, och valfritt `(?<qty>…)`. Det finns även inställningar för
     decimaler (t.ex. gram → kg) och en multiplikator.
   - **GS1-128 / DataMatrix:** (01) GTIN → artikel via EAN, (30)/(37) antal, (310x) vikt.
   - Mallar finns för GS1, `artikelnr*antal`, viktvaror (EAN-13 med prefix 2x) och prefix + artikelnr.

Ger koden ett antal fylls det i automatiskt men kan ändras. I snabbläget används det direkt.
Det sparas på varje rad hur koden tolkades ("Tolkad via"), och det syns i radexporten.
Regler kan testas innan de sparas.

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

1. Testa ADK-bryggan mot ett **testföretag** i Visma. Kör först `status`, sedan `articles`, sedan
   `inventory-write` på några artiklar. Kontrollera underlaget i Visma.
2. Ta fram certifikat och DNS-namn, och installera på servern.
3. Gå över till EF Core-migreringar innan första produktionssättningen. Schemat skapas nu med `EnsureCreated`.
