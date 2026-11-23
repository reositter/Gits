using Inventering.Core.Domain;

namespace Inventering.Tests;

public class BarcodeParserTests
{
    private static readonly Article Skruv = new() { ArticleNo = "1001", Name = "Skruv", Ean = "7310000000011" };
    private static readonly Article Ost = new() { ArticleNo = "10030", Name = "Ost" };
    private static readonly Article Bricka = new() { ArticleNo = "1003", Name = "Bricka" };

    private static readonly Dictionary<string, Article> ByNo = new(StringComparer.OrdinalIgnoreCase)
        { ["1001"] = Skruv, ["10030"] = Ost, ["1003"] = Bricka };
    private static readonly Dictionary<string, Article> ByEan = new() { ["7310000000011"] = Skruv };

    private static ScanResult? Parse(string code, BarcodeRule[]? rules = null, Dictionary<string, BarcodeAlias>? aliases = null) =>
        BarcodeParser.Parse(code, aliases ?? new(), rules ?? [], e => ByEan.GetValueOrDefault(e), n => ByNo.GetValueOrDefault(n));

    [Fact]
    public void Plain_ean_and_article_number_have_no_quantity()
    {
        Assert.Equal(("1001", (decimal?)null, "EAN"), Parse("7310000000011") is { } r ? (r.Article.ArticleNo, r.Quantity, r.Via) : default);
        Assert.Equal("1003", Parse("1003")!.Article.ArticleNo);
        Assert.Null(Parse("999"));
    }

    [Fact]
    public void Alias_gives_pack_quantity()
    {
        var aliases = new Dictionary<string, BarcodeAlias> { ["17310000000018"] = new() { Code = "17310000000018", ArticleNo = "1001", Quantity = 12 } };
        var r = Parse("17310000000018", aliases: aliases)!;
        Assert.Equal("1001", r.Article.ArticleNo);
        Assert.Equal(12, r.Quantity);
    }

    [Fact]
    public void Regex_rule_with_article_and_quantity()
    {
        var rule = new BarcodeRule { Name = "art*qty", Pattern = @"^(?<art>[^*]+)\*(?<qty>\d+(?:[.,]\d+)?)$" };
        var r = Parse("1003*24", [rule])!;
        Assert.Equal("1003", r.Article.ArticleNo);
        Assert.Equal(24, r.Quantity);
        Assert.Equal(2.5m, Parse("1003*2,5", [rule])!.Quantity);
    }

    [Fact]
    public void Variable_weight_ean_with_decimals()
    {
        var rule = new BarcodeRule { Name = "vikt", Pattern = @"^23(?<art>\d{5})(?<qty>\d{5})\d$", QuantityDecimals = 3 };
        var r = Parse("2310030012508", [rule])!;
        Assert.Equal("10030", r.Article.ArticleNo);
        Assert.Equal(1.25m, r.Quantity);
    }

    [Fact]
    public void Multiplier_without_quantity_group()
    {
        var rule = new BarcodeRule { Name = "kartong", Pattern = @"^K(?<art>.+)$", QuantityMultiplier = 6 };
        Assert.Equal(6, Parse("K1003", [rule])!.Quantity);
    }

    [Fact]
    public void Inactive_rules_and_order_are_respected()
    {
        var a = new BarcodeRule { Id = 1, Name = "a", Pattern = @"^X(?<art>.+)$", SortOrder = 20, QuantityMultiplier = 2 };
        var b = new BarcodeRule { Id = 2, Name = "b", Pattern = @"^X(?<art>.+)$", SortOrder = 10, QuantityMultiplier = 3 };
        Assert.Equal(3, Parse("X1003", [a, b])!.Quantity);
        b.IsActive = false;
        Assert.Equal(2, Parse("X1003", [a, b])!.Quantity);
    }

    [Theory]
    [InlineData("(01)07310000000011(30)12", 12)]
    [InlineData("0107310000000011" + "3012", 12)]
    [InlineData("01073100000000111725123110ABC\u001d3712", 12)]
    [InlineData("(01)07310000000011(3103)001250", 1.25)]
    public void Gs1_codes(string code, double qty)
    {
        var rule = new BarcodeRule { Name = "GS1", Type = BarcodeRuleType.Gs1 };
        var r = Parse(code, [rule])!;
        Assert.Equal("1001", r.Article.ArticleNo);
        Assert.Equal((decimal)qty, r.Quantity);
    }

    [Fact]
    public void Gs1_without_quantity_lets_user_enter_it()
    {
        var r = Parse("(01)07310000000011(10)LOT1", [new BarcodeRule { Name = "GS1", Type = BarcodeRuleType.Gs1 }])!;
        Assert.Null(r.Quantity);
    }

    [Theory]
    [InlineData("", "Ange ett namn.")]
    [InlineData("^(?<x>.+)$", "Mönstret måste")]
    [InlineData("^(?<art>.+$", "Ogiltigt mönster")]
    public void Validation(string pattern, string expected)
    {
        var rule = new BarcodeRule { Name = pattern == "" ? "" : "r", Pattern = pattern };
        Assert.StartsWith(expected, BarcodeParser.Validate(rule));
    }
}
