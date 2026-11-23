# Inventering mot Visma Administration – arkitekturförslag

Status: utkast att diskutera. Första version byggd, se README.md. Punkter markerade **[VERIFIERA]** måste kontrolleras mot er
ADK-version och befintlig kod innan vi bygger.

## 1. Mål

- Inventera med mobiler: skanna streckkod (kamera eller handskanner), ange artikelnr eller EAN manuellt.
- Flera mobiler samtidigt i samma inventering.
- En superanvändare granskar och godkänner innan något skrivs till Visma Administration.
- All skrivning till Visma går via API:t (ADK). Inget direkt mot databasfilerna.
- Körs som tjänst på Visma-servern, i första hand bara på lokalnätet.

## 2. Översikt

```
 Mobil (webbläsare/PWA)          Visma-servern (Windows)
 ┌──────────────────┐   HTTPS   ┌───────────────────────────────────────────┐
 │ Skanna / sök     │ ────────▶ │ InventoryService (ASP.NET Core, Windows-  │
 │ Ange antal       │           │ tjänst, x86 om ADK kräver 32-bit)         │
 │ Offline-kö       │           │  ├─ REST-API + statisk webbapp            │
 └──────────────────┘           │  ├─ SQLite (inventory.db)                 │
 Admin (PC-webbläsare)          │  └─ VismaGateway ── ADK ──▶ Visma Adm.    │
 ┌──────────────────┐ ────────▶ │      (en tråd, kö, alla ADK-anrop här)    │
 │ Granska/godkänn  │           └───────────────────────────────────────────┘
 └──────────────────┘
```

**Rekommendation: mobilanpassad webbapp (PWA), inte native app.**
Ingen App Store/MDM, en kodbas, uppdatering sker på servern. Kameraskanning fungerar i
webbläsaren (BarcodeDetector-API där det finns, annars ZXing/`html5-qrcode` som fallback).
Bluetooth-/handskannrar fungerar som tangentbord och kräver inget särskilt.

## 3. Frågan: slå ihop eller skicka flera?

**Svar: vi slår ihop i vår tjänst och skriver *ett* värde per artikel (och lagerställe) till Visma.**

Skälet: en inventering anger *räknat totalantal*, inte en förändring. Om mobil A skickar
"artikel 1001 = 12" och mobil B skickar "artikel 1001 = 8" (för att den står på en annan hylla)
blir resultatet i Visma 8 (sista vinner), inte 20. Skickar man i stället differenser får man
problem med dubbelskanningar och försäljning under pågående inventering.

Därför:

1. Varje mobil skickar **räkningsrader** (vem, när, artikel, antal, ev. plats/hylla) till tjänsten.
   Raderna sparas oförändrade i SQLite – full spårbarhet.
2. Tjänsten **summerar per artikel** i inventeringen.
3. Admin ser per artikel: systemsaldo vid start, räknat totalt, differens, vem som räknat vad.
   Flaggor: stor avvikelse, samma artikel räknad på samma plats av två personer (möjlig dubbelräkning),
   artiklar i sortimentet som inte räknats alls.
4. Admin kan justera/stryka rader, sedan **Godkänn → Skicka till Visma**. Då skrivs en rad per
   artikel via ADK. Resultatet (ok/fel per artikel) loggas.

Saldoförändringar under inventeringen: vi sparar Visma-saldot när inventeringen startas
("ögonblicksbild") och visar om saldot ändrats innan godkännande. Bäst är att inventera
när det inte sker leveranser, alternativt per lagerställe/zon.

## 4. Skrivning till Visma via ADK  [VERIFIERA]

Två möjliga vägar – vilken som gäller beror på vad er ADK-version exponerar och hur ni gjort tidigare:

- **A. Inventeringsunderlaget i Visma** (om ADK exponerar det): vi fyller i räknat antal och
  användaren uppdaterar lagret inne i Visma. Ger Vismas egen inventeringshistorik/rapport. Föredras.
- **B. Uppdatera artikelns lagersaldo** (artikelregistret, `ADK_DB_ARTICLE`) till räknat antal.
  Enkelt men måste kontrolleras: lagervärde, lagerhändelselogg och lagerställen.

Övrigt att verifiera i befintlig kod:
- ADK är 32-bit → tjänsten byggs som x86. ADK-anrop är inte trådsäkra → alla anrop serialiseras
  genom en enda `VismaGateway`-tråd/kö.
- Uppslag artikelnr ↔ EAN/streckkod (vilket fält används, finns flera streckkoder per artikel?).
- Licens: tar ADK-anslutningen en användarlicens? Låser den företaget för andra användare?
- Lagerställen (flera lager) och ev. batch/serienummer.

För snabb skanning cachar tjänsten artikelregistret (artikelnr, benämning, EAN, enhet) i SQLite
och uppdaterar periodiskt – mobilen ska inte vänta på ADK vid varje skanning.

## 5. Databas: SQLite räcker

Lagrar bara inventeringar, räkningsrader, användare, logg och artikelcache. Låg samtidighet
(några mobiler). SQLite i WAL-läge, en fil, enkel backup. Ingen SQL Server behövs.

Tabeller (utkast): `Users`, `InventorySessions` (status: Öppen → Låst → Godkänd → Skickad),
`CountLines` (session, användare, enhet, tidpunkt, artikelnr, EAN, antal, plats, struken),
`ArticleCache`, `StockSnapshot`, `SyncLog`, `AuditLog`.

## 6. Inloggning och roller

- Roller: **Räknare** (skanna/räkna i öppna inventeringar) och **Admin** (skapa, granska, godkänna, användare).
- Inloggning med användarnamn/lösenord (ASP.NET Core Identity, hashade lösenord, låsning vid felförsök).
- **TOTP-tvåfaktor** (Microsoft Authenticator/Google Authenticator) – obligatoriskt för Admin
  från start, och för alla om tjänsten exponeras utanför LAN.
- Alternativ för räknare på lagret: PIN-kod per användare + enhetsregistrering (snabbare med handskar).

## 7. Öppna från Visma Administration

Visma Administration har ingen inbyggd webbvy att bädda in en sida i, så realistiskt:
en länk/integrationspost (via `dynamic.xml` enligt ert tidigare upplägg [VERIFIERA]) som öppnar
admin-sidan i standardwebbläsaren, t.ex. `https://inventering.lokal/admin`. Ev. kan länken
skicka med företag/användare som parameter, men inloggning sker ändå i webbappen.

## 8. Nätverk, HTTPS och certifikat

Viktigt: **kamerans streckkodsläsning i webbläsaren kräver HTTPS**, även på lokalnätet
(webbläsare tillåter bara kamera i "secure context"). Alternativ i prioritetsordning:

1. **Riktigt certifikat för ett internt namn** – t.ex. `inventering.foretaget.se` som i intern DNS
   pekar på serverns LAN-IP. Certifikat från Let's Encrypt via DNS-validering (servern behöver
   inte vara nåbar utifrån). Fungerar på alla mobiler utan extra steg. **Rekommenderas.**
2. Egen intern CA (eller företagets AD CS) vars rotcertifikat installeras på mobilerna.
3. Självsignerat certifikat – fungerar dåligt: iOS/Android kräver manuell installation och
   förtroendeinställning per enhet, och vissa webbläsare vägrar ändå kamera.
4. Endast handskannrar (tangentbordsläge) – fungerar över HTTP, men inte rekommenderat.

**Publik åtkomst:** öppna helst inte en port i brandväggen mot Visma-servern. Använd i stället
VPN (WireGuard/Tailscale) eller en tunnel/reverse proxy (t.ex. Cloudflare Tunnel med
åtkomstpolicy). Om den ändå publiceras: riktigt certifikat (inte självsignerat), 2FA för alla,
rate limiting, låsning vid felförsök, HSTS, säkra cookies, CSRF-skydd, revisionslogg, och
att Admin-delen bara är nåbar från LAN.

## 9. Mobilflöde (räknare)

1. Logga in → välj öppen inventering (och ev. plats/hylla).
2. Skanna (kamera eller handskanner) eller skriv artikelnr/EAN → artikel visas med benämning.
3. Ange antal (standard 1, "+1 per skanning"-läge för styckvaror) → spara.
4. Lista över egna rader med möjlighet att ändra/ta bort innan inventeringen låses.
5. Offline-kö: rader sparas lokalt och skickas när wifi är tillbaka (vanligt problem i lager).
   Varje rad har ett unikt id från mobilen → ingen dubbellagring vid omsändning.

## 10. Teknikval

| Del | Val |
|---|---|
| Backend | ASP.NET Core (.NET 8 LTS), Windows-tjänst, Kestrel med HTTPS |
| ADK | Befintlig wrapper/kod från tidigare projekt, isolerad i `VismaGateway` |
| Databas | SQLite + EF Core |
| Frontend | PWA (t.ex. React/Vite eller enkel Blazor) – mobilanpassad, stora knappar |
| Skanning | BarcodeDetector-API + ZXing fallback; EAN-13/EAN-8/Code128/QR |
| Auth | ASP.NET Core Identity + TOTP |

## 11. Föreslagna etapper

1. **Spike mot ADK**: läs artiklar inkl. EAN och saldo, skriv inventerat antal på en testartikel
   i ett testföretag. Avgör väg A eller B.
2. Backend + SQLite + inloggning, mobilsida för skanning/manuell inmatning.
3. Admin-sida: sammanställning, avvikelser, godkänn, skicka till Visma, logg.
4. HTTPS/certifikat på servern, installation som tjänst, länk från Visma.
5. (Vid behov) 2FA för alla, extern åtkomst via VPN/tunnel.

## 12. Besked hittills

- Visma Administration 1000/2000, **ett lagerställe** → ingen lagerställe-dimension behövs; valfritt fält
  "plats/hylla" i mobilen används bara för att upptäcka dubbelräkning.
- **Vissa artiklar saknar EAN** → uppslag sker på EAN först, sedan artikelnr, och annars fritextsök på
  artikelnr/benämning i mobilen. Egna etiketter med artikelnr som Code128/QR fungerar också.
- **Mobilens kamera** i första hand → HTTPS är ett krav (se §8).

## 13. Öppna frågor

1. Vilken ADK-version?
2. Var finns den befintliga ADK-koden (repo)? .NET Framework eller .NET (Core)? x86?
3. Har ni gjort inventering via ADK tidigare, eller bara artiklar/order?
4. Kan en artikel ha flera streckkoder (t.ex. styck och förpackning)?
5. Finns intern DNS och en egen domän (för riktigt certifikat)?
