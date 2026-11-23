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
        // (artikelnr, benämning, EAN, saldo, enhet, inpris)
        (string, string, string?, decimal, string, decimal)[] bygg =
        [
            ("1001", "Skruv M6x20 förzinkad (100-pack)", "7310000000011", 40, "st", 79), ("1002", "Mutter M6 förzinkad (100-pack)", "7310000000028", 25, "st", 49),
            ("1003", "Bricka M6 (100-pack)", null, 12, "st", 29), ("1010", "Träskruv 4,2x45 (200-pack)", "7310000000103", 64, "st", 119),
            ("1011", "Träskruv 5,0x80 (100-pack)", "7310000000110", 38, "st", 139), ("1020", "Expander 8 mm (50-pack)", "7310000000202", 20, "st", 89),
            ("2001", "Kabel EKK 3G1,5 (m)", "7310000000042", 350, "m", 12.5m), ("2002", "Kopplingsdosa IP44", null, 48, "st", 34),
            ("2003", "Vägguttag dubbelt jordat", "7310000000301", 30, "st", 89), ("2004", "Strömbrytare 1-pol", "7310000000318", 26, "st", 69),
            ("3001", "Arbetshandske strl 10", "7310000000066", 60, "par", 45), ("3002", "Skyddsglasögon klar", "7310000000073", 22, "st", 59),
            ("3003", "Hörselkåpa", "7310000000080", 9, "st", 249), ("4001", "Gipsskiva 13 mm 1200x2500", "7320000000105", 120, "st", 129),
            ("4002", "Regel 45x95 (m)", null, 800, "m", 24), ("4003", "Spik 75 mm (kg)", "7320000000303", 35, "kg", 59),
            ("4004", "Fogskum 750 ml", "7320000000310", 44, "st", 119), ("4005", "Silikon sanitet vit", "7320000000327", 70, "st", 89),
            ("5001", "Borr HSS 6 mm", "7330000000017", 55, "st", 39), ("5002", "Bits PH2 (10-pack)", "7330000000024", 40, "st", 79),
        ];
        (string, string, string?, decimal, string, decimal)[] livs =
        [
            ("A100", "Kaffe mellanrost 500 g", "7340000000014", 96, "st", 54), ("A101", "Te earl grey 100 p", "7340000000021", 40, "st", 42),
            ("A200", "Vetemjöl 2 kg", "7340000000106", 150, "st", 19), ("A201", "Strösocker 2 kg", "7340000000113", 120, "st", 29),
            ("A300", "Hushållsost (kg)", null, 34.5m, "kg", 99), ("A301", "Smör 500 g", "7340000000304", 80, "st", 59),
            ("A400", "Olivolja 1 l", "7340000000403", 45, "st", 89), ("A401", "Pasta penne 1 kg", "7340000000410", 200, "st", 24),
            ("1001", "Skruv M6x20 (100-pack)", "7310000000011", 15, "st", 79),
        ];
        foreach (var (no, name, ean, qty, unit, price) in seed % 2 == 0 ? livs : bygg)
            _articles[no] = new Article { ArticleNo = no, Name = name, Ean = ean, StockQuantity = qty, Unit = unit, PurchasePrice = price, UpdatedAt = DateTimeOffset.UtcNow };
    }

    public string Name => "Fake (test)";

    public Task<VismaStatus> GetStatusAsync(CancellationToken ct = default) =>
        // Simulerar Visma med ett inventeringsunderlag som omfattar alla artiklar.
        Task.FromResult(new VismaStatus(Name, "Testläge – simulerad Visma Administration", true, _articles.Count, DateTime.Today));

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
            new("Demo Bygg AB", "FTG1", @"C:\ProgramData\SPCS\SPCS Administration\Företag\FTG1", "SPCS_Adm_FTG1"),
            new("FTG2", "FTG2", @"C:\ProgramData\SPCS\SPCS Administration\Företag\FTG2", "SPCS_Adm_FTG2"),
            new("Demo Service AB", "FTG3", @"C:\ProgramData\SPCS\SPCS Administration\Företag\FTG3", "SPCS_Adm_FTG3"),
        ]);
}
