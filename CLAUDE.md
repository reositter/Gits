# Inventering för Visma Administration

Lagerinventering med mobil (kamera/handskanner), Excel och webbadmin mot Visma Administration 1000/2000.
Mer i `README.md` (drift, funktioner) och `docs/ARKITEKTUR.md` (bakgrund och beslut).

## Struktur
- `src/Inventering.Web` – ASP.NET Core 8: minimal API (`Endpoints/`), tjänster (`Services/`), EF Core + SQLite (`Data/`, migreringar i `Data/Migrations`), frontend i `wwwroot` (vanilla JS, inga byggsteg).
  - `index.html` + `js/count.js` + `css/mobile.css` – mobilsidan (app-känsla, ark underifrån, docka nere).
  - `admin.html` + `js/admin.js` – administration. `js/common.js` – inloggning, bolagsval, tvåfaktor. `js/icons.js` – SVG-ikoner.
- `src/Inventering.Core` – domän (entiteter, summering, streckkodstolkning, kontrollsumma) och `IVismaGateway` (Fake + ADK).
- `src/Inventering.AdkBridge` – .NET Framework 4.8 **x86**-konsol som gör alla ADK-anrop via AvSynkLib/AdkNet4Wrapper (C++/CLI, kan inte laddas i .NET 8). Webbtjänsten startar den per anrop, JSON via stdin/stdout.
- `lib/` – `AvSynkLib.dll`, `AdkNet4Wrapper.dll` (licensierade, **checkas inte in** – repot är publikt).
- `tests/Inventering.Tests` – xUnit, inkl. API-flöden med WebApplicationFactory.
- `deploy/` – `bygg-paket.ps1`, `installera.ps1`, `Installera.cmd`, `Byt certifikat.cmd`.
- `demo/` – statisk demo för Cloudflare Pages; samma wwwroot + låtsasserver i service worker (`demo/static/mock-sw.js`). Håll mock-API:t i takt med riktiga API:t.

## Kommandon
```
dotnet test                                   # alla tester
cd src/Inventering.Web && dotnet run          # Development, Fake-Visma, http://localhost:5080
dotnet tool restore
dotnet ef migrations add <Namn> --project src/Inventering.Web --output-dir Data/Migrations
.\deploy\bygg-paket.ps1                        # installationspaket → dist\Inventering
node demo/build.mjs                            # demo → demo/dist
```
Första start skapar användaren `admin`; lösenordet skrivs till `forsta-admin-losenord.txt` i datakatalogen (`bin/Debug/net8.0/data` i utveckling).

## Viktiga beslut
- All skrivning till Visma via ADK. Räknat antal skrivs till **Vismas inventeringsunderlag** (`AvInventoryArticle.Quantity/Date`); lagret uppdateras när inventeringen slutförs i Visma.
- Flera mobiler → räkningsrader summeras per artikel i tjänsten; ett värde per artikel skickas.
- Bara **inlämnade** (signerade med lösenord) rader räknas. Inlämning har SHA-256-kontrollsumma; rader stryks aldrig bort, bara `IsVoided`.
- Flera bolag (FTG1, FTG2 …): allt är per bolag (`CompanyId`), valt bolag ligger i inloggningscookien, `CompanyFilter` på API-grupper.
- Artikelregistret cachas i minnet + SQLite per bolag; skanning/sök går aldrig mot Visma.
- Användare hanteras i tjänsten (inte Visma). TOTP-tvåfaktor: krävs för admin som standard, kan krävas per användare.
- Https: egen webbserver (Kestrel), inte IIS. Certifikat väljs ur `LocalMachine\My` via tumavtryck (`ServerCertificateProvider`), kan bytas i Admin → Server utan omstart. Kameran kräver https.
- Databasen: SQLite med EF Core-migreringar; säkerhetskopia (VACUUM INTO) före varje migrering.

## Konventioner
- All text i gränssnitt, loggar och kommentarer på **svenska**. Inga emojis i UI – använd `icon()` från `js/icons.js`.
- Frontend utan ramverk/byggsteg; tredjepartsbibliotek ligger i `wwwroot/lib`.
- Ny entitetsändring kräver migrering (ett test larmar annars).
- Ändrande API-anrop kräver huvudet `X-Requested-With: inventering` (CSRF-skydd).
- Lägg till tester för ny backendlogik; kör `dotnet test` innan commit.

## Ej verifierat mot riktig Visma
ADK-bryggan (artiklar, bolagslista, skrivning till inventeringsunderlaget) och installationsskriptets Windows-delar (certifikat, tjänst, brandvägg) är byggda men inte körda på Windows/Visma. Testa i ett testföretag först.
