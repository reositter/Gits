using System.Collections.Concurrent;
using Inventering.Core.Domain;

namespace Inventering.Core.Visma;

/// <summary>Låtsas-Visma för utveckling och test utan ADK.</summary>
public class FakeVismaGateway : IVismaGateway
{
    private readonly ConcurrentDictionary<string, Article> _articles = new(StringComparer.OrdinalIgnoreCase);

    public FakeVismaGateway()
    {
        void Add(string no, string name, string? ean, decimal qty, string unit = "st") =>
            _articles[no] = new Article { ArticleNo = no, Name = name, Ean = ean, StockQuantity = qty, Unit = unit, UpdatedAt = DateTimeOffset.UtcNow };
        Add("1001", "Skruv M6x20 (100-pack)", "7310000000011", 40);
        Add("1002", "Mutter M6 (100-pack)", "7310000000028", 25);
        Add("1003", "Bricka M6 (100-pack)", null, 12);
        Add("2001", "Kabel 3x1,5 (m)", "7310000000042", 350, "m");
        Add("2002", "Kopplingsdosa", null, 0);
        Add("3001", "Arbetshandske strl 10", "7310000000066", 60, "par");
    }

    public string Name => "Fake (test)";

    public IReadOnlyCollection<Article> Current => _articles.Values.ToList();

    public void SetStock(string articleNo, decimal qty) => _articles[articleNo].StockQuantity = qty;

    public Task<IReadOnlyList<Article>> GetArticlesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Article>>(_articles.Values.Select(Clone).ToList());

    public Task<IReadOnlyList<InventoryWriteResult>> WriteInventoryAsync(IReadOnlyList<InventoryWrite> rows, string reference, CancellationToken ct = default)
    {
        var result = rows.Select(r =>
        {
            if (!_articles.TryGetValue(r.ArticleNo, out var a))
                return new InventoryWriteResult(r.ArticleNo, false, null, "Artikeln finns inte");
            var before = a.StockQuantity;
            a.StockQuantity = r.CountedQuantity;
            return new InventoryWriteResult(r.ArticleNo, true, before, null);
        }).ToList();
        return Task.FromResult<IReadOnlyList<InventoryWriteResult>>(result);
    }

    private static Article Clone(Article a) => new()
    {
        ArticleNo = a.ArticleNo, Name = a.Name, Ean = a.Ean, Unit = a.Unit,
        StockQuantity = a.StockQuantity, IsStockItem = a.IsStockItem, UpdatedAt = DateTimeOffset.UtcNow,
    };
}
