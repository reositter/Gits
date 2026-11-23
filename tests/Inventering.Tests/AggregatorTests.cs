using Inventering.Core.Domain;

namespace Inventering.Tests;

public class AggregatorTests
{
    private static readonly Dictionary<string, Article> Articles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1001"] = new() { ArticleNo = "1001", Name = "Skruv", StockQuantity = 40 },
        ["1002"] = new() { ArticleNo = "1002", Name = "Mutter", StockQuantity = 25 },
        ["1003"] = new() { ArticleNo = "1003", Name = "Bricka", StockQuantity = 12 },
    };
    private static readonly Dictionary<string, decimal> Snapshot = new(StringComparer.OrdinalIgnoreCase) { ["1001"] = 40, ["1002"] = 25, ["1003"] = 12 };
    private static readonly Dictionary<int, string> Users = new() { [1] = "anna", [2] = "bo" };

    private static CountLine Line(string art, decimal qty, int user = 1, string? loc = null, bool voided = false) =>
        new() { Id = Guid.NewGuid(), ArticleNo = art, Quantity = qty, UserId = user, Location = loc, IsVoided = voided };

    [Fact]
    public void Counts_from_several_phones_are_summed_per_article()
    {
        var lines = new[] { Line("1001", 12, 1, "A1"), Line("1001", 8, 2, "B4"), Line("1002", 25, 2) };
        var s = InventoryAggregator.Summarize(lines, Snapshot, Articles, Users);

        var a = s.Single(x => x.ArticleNo == "1001");
        Assert.Equal(20, a.CountedQuantity);
        Assert.Equal(-20, a.Difference);
        Assert.Equal(2, a.LineCount);
        Assert.Equal(new[] { "anna", "bo" }, a.CountedBy);
        Assert.Equal(0, s.Single(x => x.ArticleNo == "1002").Difference);
    }

    [Fact]
    public void Voided_lines_are_ignored()
    {
        var lines = new[] { Line("1001", 40), Line("1001", 40, voided: true) };
        Assert.Equal(40, InventoryAggregator.Summarize(lines, Snapshot, Articles, Users).Single().CountedQuantity);
    }

    [Fact]
    public void Same_location_counted_by_two_people_is_flagged()
    {
        var lines = new[] { Line("1001", 20, 1, "A1"), Line("1001", 20, 2, "a1 ") };
        var a = InventoryAggregator.Summarize(lines, Snapshot, Articles, Users).Single();
        Assert.Contains(a.Warnings, w => w.Contains("Samma plats"));
    }

    [Fact]
    public void Stock_change_during_count_is_flagged()
    {
        var arts = new Dictionary<string, Article>(Articles, StringComparer.OrdinalIgnoreCase)
        { ["1001"] = new() { ArticleNo = "1001", Name = "Skruv", StockQuantity = 35 } };
        var a = InventoryAggregator.Summarize(new[] { Line("1001", 40) }, Snapshot, arts, Users).Single();
        Assert.True(a.StockChangedSinceStart);
    }

    [Fact]
    public void Uncounted_lists_stock_articles_with_balance_not_counted()
    {
        var u = InventoryAggregator.Uncounted(new[] { Line("1001", 1) }, Articles.Values);
        Assert.Equal(new[] { "1002", "1003" }, u.Select(a => a.ArticleNo));
    }

    [Theory]
    [InlineData("7310000000011", "1001")] // EAN
    [InlineData("1003", "1003")]          // artikelnr för artikel utan EAN
    [InlineData(" 1003 ", "1003")]
    [InlineData("731000000001", null)]    // 12 siffror → normaliseras till 0731… som inte finns
    [InlineData("9999", null)]
    public void Lookup_by_ean_then_article_number(string code, string? expected)
    {
        var a1 = new Article { ArticleNo = "1001", Ean = "7310000000011" };
        var a3 = new Article { ArticleNo = "1003" };
        var byEan = new Dictionary<string, Article> { [a1.Ean!] = a1 };
        var byNo = new Dictionary<string, Article>(StringComparer.OrdinalIgnoreCase) { ["1001"] = a1, ["1003"] = a3 };
        Assert.Equal(expected, CodeLookup.Find(code, byEan, byNo)?.ArticleNo);
    }

    [Fact]
    public void Upc_a_is_normalized_to_ean13()
    {
        Assert.Equal("0012345678905", CodeLookup.NormalizeEan("012345678905"));
    }
}
