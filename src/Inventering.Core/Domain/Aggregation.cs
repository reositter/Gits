namespace Inventering.Core.Domain;

public record ArticleSummary(
    string ArticleNo,
    string Name,
    string? Ean,
    decimal SnapshotQuantity,
    decimal CurrentStock,
    decimal CountedQuantity,
    int LineCount,
    IReadOnlyList<string> CountedBy,
    IReadOnlyList<string> Warnings)
{
    public decimal Difference => CountedQuantity - SnapshotQuantity;
    /// <summary>Saldot i Visma har ändrats sedan inventeringen startades (försäljning/leverans under räkning).</summary>
    public bool StockChangedSinceStart => CurrentStock != SnapshotQuantity;
}

public static class InventoryAggregator
{
    /// <summary>
    /// Summerar räkningsrader per artikel. Flera mobiler som räknar samma artikel
    /// (t.ex. på olika hyllor) adderas – Visma får ett totalvärde per artikel.
    /// </summary>
    public static IReadOnlyList<ArticleSummary> Summarize(
        IEnumerable<CountLine> lines,
        IReadOnlyDictionary<string, decimal> snapshot,
        IReadOnlyDictionary<string, Article> articles,
        IReadOnlyDictionary<int, string> userNames,
        decimal largeDeviationRatio = 0.5m,
        bool compareCurrentStock = true,
        Func<CountLine, string?>? lineCounter = null)
    {
        var active = lines.Where(l => !l.IsVoided).ToList();

        return active
            .GroupBy(l => l.ArticleNo, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                articles.TryGetValue(g.Key, out var art);
                var snap = snapshot.TryGetValue(g.Key, out var s) ? s : 0m;
                var counted = g.Sum(l => l.Quantity);
                var warnings = new List<string>();

                // Samma plats räknad av olika personer → trolig dubbelräkning.
                var dupLocations = g.Where(l => !string.IsNullOrWhiteSpace(l.Location))
                    .GroupBy(l => l.Location!.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Where(lg => lg.Select(l => l.UserId).Distinct().Count() > 1)
                    .Select(lg => lg.Key)
                    .ToList();
                if (dupLocations.Count > 0)
                    warnings.Add($"Samma plats räknad av flera personer: {string.Join(", ", dupLocations)}");

                if (art is null) warnings.Add("Artikeln finns inte i artikelcachen");
                else if (!art.IsStockItem) warnings.Add("Artikeln är inte lagerförd");

                if (counted < 0) warnings.Add("Negativt räknat antal");

                var diff = Math.Abs(counted - snap);
                if (snap == 0 ? counted != 0 && diff >= 10 : diff / Math.Abs(snap) >= largeDeviationRatio)
                    warnings.Add("Stor avvikelse mot saldo");

                // Efter skickad inventering är "aktuellt saldo" inventeringens eget resultat – jämför inte då.
                var current = compareCurrentStock ? art?.StockQuantity ?? snap : snap;
                if (current != snap)
                    warnings.Add($"Saldot i Visma har ändrats under inventeringen ({Fmt(snap)} → {Fmt(current)})");

                return new ArticleSummary(
                    g.Key,
                    art?.Name ?? "",
                    art?.Ean,
                    snap,
                    current,
                    counted,
                    g.Count(),
                    g.Select(l => lineCounter?.Invoke(l) ?? (userNames.TryGetValue(l.UserId, out var n) ? n : $"#{l.UserId}")).Distinct().ToList(),
                    warnings);
            })
            .OrderBy(a => a.ArticleNo, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Fmt(decimal d) => d.ToString("#,0.###", System.Globalization.CultureInfo.GetCultureInfo("sv-SE"));

    /// <summary>Lagerförda artiklar med saldo som inte räknats alls i inventeringen.</summary>
    public static IReadOnlyList<Article> Uncounted(IEnumerable<CountLine> lines, IEnumerable<Article> articles)
    {
        var counted = lines.Where(l => !l.IsVoided).Select(l => l.ArticleNo)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return articles.Where(a => a.IsStockItem && a.StockQuantity != 0 && !counted.Contains(a.ArticleNo))
            .OrderBy(a => a.ArticleNo, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
