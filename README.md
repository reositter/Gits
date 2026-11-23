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
| Inventering via Excel: skapa lista att skicka ut, läs in ifylld fil (dra och släpp) som en inlämning | Klar |
| Rapporter: utskriftsvänlig HTML/PDF och Excel (sammanfattning, artiklar, inlämningar, alla rader) | Klar |
| Flera bolag (FTG1, FTG2 …) med egna namn, bolagsval vid inloggning, behörighet per användare | Klar |
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
deploy/                    bygg-paket.ps1 och installera.ps1 (installation med val av certifikat)
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

**1. Bygg paketet** på en utvecklingsdator med .NET 8 SDK. Lägg först `AvSynkLib.dll` och
`AdkNet4Wrapper.dll` i `lib\`.

```powershell
.\deploy\bygg-paket.ps1          # → dist\Inventering (app\, installera.ps1, LÄS MIG.txt)
```

**2. Installera på servern.** Kopiera `dist\Inventering` till servern och kör som administratör:

```powershell
powershell -ExecutionPolicy Bypass -File .\installera.ps1
```

Skriptet frågar efter installations- och datamapp, portar och **hur https ska ordnas**:

| Val | Vad skriptet gör | Passar när |
|---|---|---|
| 1. Egen intern certifikatutfärdare (rekommenderas) | Skapar ett rotcertifikat (10 år) och ett servercertifikat för serverns namn och IP-adresser, signerat av rotcertifikatet. | Tjänsten används på det interna nätet. Varje telefon installerar rotcertifikatet en gång, sedan blir det inga varningar. |
| 2. Självsignerat certifikat | Skapar ett servercertifikat utan utfärdare. | Snabbtest. Webbläsaren varnar, och iPhone glömmer ofta undantaget. |
| 3. Befintligt certifikat (.pfx) | Importerar certifikatet, t.ex. från Let's Encrypt/win-acme eller företagets CA. | Ni har en domän eller en egen CA. |
| 4. Inget https | Bara http. | Bara handskannrar, ingen kamera. |

Skriptet installerar och startar sedan Windows-tjänsten "Inventering" med automatisk omstart
vid fel. Det öppnar portarna i brandväggen, bara för domän- och privata nätverk, och skriver
`appsettings.Production.json`. Till sist visar det adresserna och var första admin-lösenordet finns.

**Telefonerna:** vid val 1 eller 2 öppnar man `http://<servern>/certifikat.html` på varje telefon.
Sidan nås över vanlig http och har en knapp för att hämta certifikatet, steg för iPhone och Android
samt en QR-kod när den visas på en dator. All annan trafik skickas vidare till https.

**Uppdatera:** kör `installera.ps1` från ett nytt paket. Inställningar och data behålls.
**Byta certifikat:** `.\installera.ps1 -BaraCertifikat`. Servercertifikaten gäller i 825 dagar,
vilket är det längsta iPhone godtar. Rotcertifikatet återanvänds vid förnyelse, så telefonerna
behöver inte installera något nytt.

Tjänsten väljer servercertifikat ur `LocalMachine\My` via tumavtryck. Saknas det tar den det
nyaste giltiga certifikatet med samma namn, vilket gör att win-acme-förnyelser fungerar efter
en omstart. Ett rotcertifikat väljs aldrig.

### Öppna från Visma Administration

Lägg in en länk eller integrationspost, enligt befintligt upplägg med `dynamic.xml`, som öppnar
`https://inventering.foretaget.se/admin.html` i webbläsaren.

## Inventera via Excel

På en öppen eller låst inventering finns kortet **Inventera via Excel**.

1. **Skapa Excel-fil** ger en inventeringslista med bolagets lagerartiklar: artikelnr, benämning,
   enhet, EAN, plats, en gul kolumn **Räknat antal** och kommentar. Det finns val för att visa
   saldot (av som standard, så att man räknar utan att se förväntat antal), bara ej räknade
   artiklar och ett intervall av artikelnummer. Bladet är skyddat mot ändringar utom i
   inmatningsfälten och har en dold flik som anger vilken inventering det hör till.
2. Listan skickas ut, skrivs ut eller fylls i på datorn. Räknaren skriver sitt namn under **Räknat av**.
3. **Dra och släpp** den ifyllda filen, eller klicka för att välja den. En förhandsgranskning
   visar rader, antal och eventuella fel, t.ex. text i antalskolumnen eller okända artiklar.
4. **Läs in** gör raderna till en inlämning, precis som från mobilen. Inlämningen får typen
   "Excel-import", namnet från filen och en kontrollsumma. Samma fil kan inte läsas in två
   gånger av misstag.

Egna Excel-filer fungerar också, om de har kolumnerna **Artikelnr** och **Räknat antal** (eller **Antal**).

## Rapporter

På inventeringens sida finns knapparna **📄 Rapport** och **📊 Excel**.

- **Rapport (HTML)** är byggd för utskrift (A4 liggande). Den har bolag, status och datum, nyckeltal
  (differens i antal och kronor, räknat lagervärde), alla räknade artiklar med saldo vid start,
  räknat antal, differens, inpris och värde, ej räknade artiklar, inlämningar med kontrollsumma och
  en rad för underskrift. "Skriv ut / spara som PDF" ger en PDF. Den kan också visa bara differenser.
- **Excel (.xlsx)** har flikarna Sammanfattning, Artiklar (tabell med filter och summarad), Ej räknade,
  Inlämningar och Alla rader. Den sista ger full spårbarhet: vem, enhet, tid, skannad kod och
  inlämning för varje rad.

Inpriset är Vismas beräknade inpris (`EstimatedPurchasePrice`), sparat när inventeringen startades.

## Flera bolag

Admin → **Bolag**:
- **Sök bolag i Visma** listar alla företag som ADK hittar (`AvCompany.GetAvailableCompanies`).
  Klicka **Lägg till** och ge bolaget ett namn som användarna känner igen, t.ex.
  "Bygg AB – Lager Göteborg" i stället för FTG3.
- Det går också att lägga till bolag manuellt med företagsmappen.
- Visma-användare och lösenord anges per bolag om inloggning är aktiverad i Visma. Lösenordet
  krypteras med DataProtection och lagras aldrig i klartext.
- **Testa** loggar in mot bolaget och läser in artiklarna.

Efter inloggning väljer användaren bolag i en lista. Har användaren bara ett bolag väljs det
automatiskt. Det valda bolaget visas i sidhuvudet och kan bytas där. Under Admin → Användare kan
en räknare begränsas till vissa bolag, och utan begränsning har räknaren tillgång till alla.

Varje bolag har egna inventeringar, en egen artikelcache och egna extra streckkoder.
Streckkodsregler kan gälla ett bolag eller alla bolag.

## Snabb sökning och cache

Skanning och sökning går **aldrig** mot Visma. ADK:s `Find` är långsam, så hela artikelregistret
per bolag hålls i minnet med index på artikelnr och EAN, plus en förberäknad söktext för
fritextsökning. Registret sparas även i SQLite så att det finns direkt vid omstart, även om Visma
inte svarar. Det uppdateras i bakgrunden från Visma var 15:e minut (`Visma:ArticleRefreshMinutes`),
när en inventering startas och skickas, och manuellt med en knapp i admin.

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

## Databas och uppdateringar

SQLite-databasen (`inventory.db` i datakatalogen) hanteras med EF Core-migreringar
(`src/Inventering.Web/Data/Migrations`). Vid varje start körs väntande migreringar automatiskt.

- **Före en uppdatering** tas en säkerhetskopia till `backups/`, t.ex.
  `inventory-20270115-0830-fore-20270110_NyKolumn.db`. De tio senaste sparas.
- En databas från en tidig testversion, utan migreringshistorik, flyttas undan till
  `inventory.db.fore-migreringar-<tid>` och en ny skapas.
- Ett test säkerställer att ingen modelländring saknar migrering.

Ny schemaändring (utvecklare):

```bash
dotnet tool restore
dotnet ef migrations add <Namn> --project src/Inventering.Web --output-dir Data/Migrations
```

Återställ från säkerhetskopia: stoppa tjänsten, ersätt `inventory.db` med kopian (ta bort
`inventory.db-wal` och `-shm`) och installera den version av tjänsten som hörde till kopian.

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
2. Installera på servern med `installera.ps1` och välj certifikat. Installera rotcertifikatet på telefonerna.

