using Inventering.Core.Domain;

namespace Inventering.Core.Visma;

public class AdkOptions
{
    /// <summary>Sökväg till Visma Administrations systemkatalog (Gemensamma filer), t.ex. C:\ProgramData\SPCS\SPCS Administration\Gemensamma filer.</summary>
    public string SystemPath { get; set; } = "";
    /// <summary>Sökväg till företagskatalogen, t.ex. ...\Företag\FTG1.</summary>
    public string CompanyPath { get; set; } = "";
}

/// <summary>
/// Implementation mot Visma Administration 1000/2000 via ADK.
///
/// TODO: fylls i med er befintliga ADK-kod (wrapper, öppna/stäng företag, felhantering).
/// Alla anrop körs på <see cref="SingleThreadExecutor"/>. Processen måste vara x86 om
/// ADK-DLL:en är 32-bitars (publicera med -r win-x86).
///
/// Att verifiera i spike:
///  - Läsa artikelregistret: artikelnr, benämning, EAN/streckkod, enhet, i lager, lagerförd.
///  - Skriva inventering: antingen via Vismas inventeringsunderlag (föredras, ger Vismas
///    egen historik) eller genom att sätta artikelns lagersaldo till räknat antal.
///  - Licens/låsning: kan ADK vara öppet samtidigt som användare jobbar i Visma?
/// </summary>
public sealed class AdkVismaGateway : IVismaGateway, IDisposable
{
    private readonly AdkOptions _options;
    private readonly SingleThreadExecutor _adk = new("ADK");

    public AdkVismaGateway(AdkOptions options) => _options = options;

    public string Name => "Visma Administration (ADK)";

    public Task<IReadOnlyList<Article>> GetArticlesAsync(CancellationToken ct = default) =>
        _adk.RunAsync<IReadOnlyList<Article>>(() =>
            throw new NotImplementedException("ADK-läsning av artiklar är inte implementerad ännu."));

    public Task<IReadOnlyList<InventoryWriteResult>> WriteInventoryAsync(IReadOnlyList<InventoryWrite> rows, string reference, CancellationToken ct = default) =>
        _adk.RunAsync<IReadOnlyList<InventoryWriteResult>>(() =>
            throw new NotImplementedException("ADK-skrivning av inventering är inte implementerad ännu."));

    public void Dispose() => _adk.Dispose();
}
