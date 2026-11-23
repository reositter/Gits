using Inventering.Core.Domain;

namespace Inventering.Core.Visma;

public record InventoryWrite(string ArticleNo, decimal CountedQuantity);

public record InventoryWriteResult(string ArticleNo, bool Success, decimal? StockBefore, string? Message);

/// <param name="WritesToInventoryList">
/// True = resultatet skrivs till Vismas inventeringsunderlag och lagret uppdateras först när
/// användaren slutför inventeringen i Visma.
/// </param>
public record VismaStatus(string Name, string? Details, bool WritesToInventoryList, int? InventoryListRows, DateTime? InventoryListDate);

/// <summary>
/// All kommunikation med Visma Administration går genom detta gränssnitt.
/// Implementationen mot ADK ska vara den enda koden som rör ADK.
/// </summary>
public interface IVismaGateway
{
    string Name { get; }

    /// <summary>Kontrollerar kopplingen och (för ADK) att ett inventeringsunderlag finns i Visma.</summary>
    Task<VismaStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Hämtar alla artiklar (artikelnr, benämning, EAN, enhet, lagersaldo, lagerförd).</summary>
    Task<IReadOnlyList<Article>> GetArticlesAsync(CancellationToken ct = default);

    /// <summary>Skriver inventerat antal (absolut värde) per artikel. Ett anrop per inventering.</summary>
    Task<IReadOnlyList<InventoryWriteResult>> WriteInventoryAsync(IReadOnlyList<InventoryWrite> rows, string reference, CancellationToken ct = default);
}

/// <summary>Ett bolag som hittats i Visma Administration.</summary>
public record VismaCompanyInfo(string Name, string Directory, string FullPath, string? DatabaseName);

/// <summary>Skapar en gateway per bolag och hittar vilka bolag som finns i Visma.</summary>
public interface IVismaGatewayFactory
{
    string Mode { get; }
    IVismaGateway For(Company company);
    Task<IReadOnlyList<VismaCompanyInfo>> DiscoverCompaniesAsync(CancellationToken ct = default);
}
