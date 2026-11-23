using Inventering.Core.Domain;

namespace Inventering.Core.Visma;

public record InventoryWrite(string ArticleNo, decimal CountedQuantity);

public record InventoryWriteResult(string ArticleNo, bool Success, decimal? StockBefore, string? Message);

/// <summary>
/// All kommunikation med Visma Administration går genom detta gränssnitt.
/// Implementationen mot ADK ska vara den enda koden som rör ADK.
/// </summary>
public interface IVismaGateway
{
    string Name { get; }

    /// <summary>Hämtar alla artiklar (artikelnr, benämning, EAN, enhet, lagersaldo, lagerförd).</summary>
    Task<IReadOnlyList<Article>> GetArticlesAsync(CancellationToken ct = default);

    /// <summary>Skriver inventerat antal (absolut värde) per artikel. Ett anrop per inventering.</summary>
    Task<IReadOnlyList<InventoryWriteResult>> WriteInventoryAsync(IReadOnlyList<InventoryWrite> rows, string reference, CancellationToken ct = default);
}
