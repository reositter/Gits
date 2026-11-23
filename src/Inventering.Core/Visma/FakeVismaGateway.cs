using System.Collections.Concurrent;
using Inventering.Core.Domain;

namespace Inventering.Core.Visma;

/// <summary>Låtsas-Visma för utveckling och test utan ADK.</summary>
public class FakeVismaGateway : IVismaGateway
{
    private readonly ConcurrentDictionary<string, Article> _articles = new(StringComparer.OrdinalIgnoreCase);

    public FakeVismaGateway() : this(1) { }

    /// <summary>Olika testdata per bolag så att det syns vilket bolag man jobbar i.</summary>
    public FakeVismaGateway(int seed)
    {
        if (seed % 2 == 0)
        {
            void Add2(string no, string name, string? ean, decimal qty, string unit = "st") =>
                _articles[no] = new Article { ArticleNo = no, Name = name, Ean = ean, StockQuantity = qty, Unit = unit, PurchasePrice = 10 + no.Sum(c => c) % 90, UpdatedAt = DateTimeOffset.UtcNow };
            Add2("A100", "Gipsskiva 13 mm", "7320000000105", 120);
            Add2("A200", "Regel 45x95 (m)", null, 800, "m");
            Add2("A300", "Spik 75 mm (kg)", "7320000000303", 35, "kg");
            Add2("1001", "Skruv M6x20 (100-pack)", "7310000000011", 15);
            return;
        }
        void Add(string no, string name, string? ean, decimal qty, string unit = "st") =>
            _articles[no] = new Article { ArticleNo = no, Name = name, Ean = ean, StockQuantity = qty, Unit = unit, PurchasePrice = 5 + no.Sum(c => c) % 120, UpdatedAt = DateTimeOffset.UtcNow };
        Add("1001", "Skruv M6x20 (100-pack)", "7310000000011", 40);
        Add("1002", "Mutter M6 (100-pack)", "7310000000028", 25);
        Add("1003", "Bricka M6 (100-pack)", null, 12);
        Add("2001", "Kabel 3x1,5 (m)", "7310000000042", 350, "m");
        Add("2002", "Kopplingsdosa", null, 0);
        Add("3001", "Arbetshandske strl 10", "7310000000066", 60, "par");
    }

    public string Name => "Fake (test)";

    public Task<VismaStatus> GetStatusAsync(CancellationToken ct = default) =>
        Task.FromResult(new VismaStatus(Name, "Testdata – inget skrivs till Visma", false, null, null));

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
        StockQuantity = a.StockQuantity, PurchasePrice = a.PurchasePrice, IsStockItem = a.IsStockItem, UpdatedAt = DateTimeOffset.UtcNow,
    };
}

public class FakeVismaGatewayFactory : IVismaGatewayFactory
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, FakeVismaGateway> _byCompany = new();
    public string Mode => "Fake";
    public IVismaGateway For(Company company) => _byCompany.GetOrAdd(company.Id, id => new FakeVismaGateway(id));
    public FakeVismaGateway ForId(int companyId) => _byCompany.GetOrAdd(companyId, id => new FakeVismaGateway(id));
    public Task<IReadOnlyList<VismaCompanyInfo>> DiscoverCompaniesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VismaCompanyInfo>>(
        [
            new("Demo AB", "FTG1", @"C:\Demo\Företag\FTG1", "SPCS_Adm_FTG1"),
            new("Testbygg AB", "FTG2", @"C:\Demo\Företag\FTG2", "SPCS_Adm_FTG2"),
            new("FTG3", "FTG3", @"C:\Demo\Företag\FTG3", "SPCS_Adm_FTG3"),
        ]);
}
