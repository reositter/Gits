using System.Globalization;
using System.Text.RegularExpressions;

namespace Inventering.Core.Domain;

public enum BarcodeRuleType
{
    /// <summary>Reguljärt uttryck med namngivna grupper: art, ean, qty.</summary>
    Regex = 0,
    /// <summary>GS1-128 / GS1 DataMatrix: (01) GTIN, (30)/(37) antal, (310n) nettovikt kg.</summary>
    Gs1 = 1,
}

/// <summary>Kundspecifik tolkning av streckkoder. Prövas i ordning efter exakt EAN/artikelnr.</summary>
public class BarcodeRule
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public BarcodeRuleType Type { get; set; }
    /// <summary>Regex med grupperna (?&lt;art&gt;…) eller (?&lt;ean&gt;…), valfritt (?&lt;qty&gt;…).</summary>
    public string Pattern { get; set; } = "";
    /// <summary>Antal decimaler i qty-gruppen, t.ex. 3 för vikt i gram → kg.</summary>
    public int QuantityDecimals { get; set; }
    /// <summary>Multipliceras med antalet (eller används ensamt om qty saknas), t.ex. 12 för kartong.</summary>
    public decimal QuantityMultiplier { get; set; } = 1;
    public string? Description { get; set; }
}

/// <summary>Extra streckkod för en artikel, t.ex. kartong-EAN = 12 st. Visma har bara en streckkod per artikel.</summary>
public class BarcodeAlias
{
    public string Code { get; set; } = "";
    public string ArticleNo { get; set; } = "";
    /// <summary>Antal per skanning (förpackningsstorlek).</summary>
    public decimal Quantity { get; set; } = 1;
    public string? Note { get; set; }
}

/// <param name="Quantity">Antal som koden anger (null = användaren anger antal).</param>
/// <param name="Via">Hur koden tolkades, sparas på raden för spårbarhet.</param>
public record ScanResult(Article Article, decimal? Quantity, string Via);

public static class BarcodeParser
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const char Gs = '\u001d'; // FNC1/GS-separator i GS1-koder

    /// <summary>Kontrollerar att en regel går att använda. Returnerar felmeddelande eller null.</summary>
    public static string? Validate(BarcodeRule r)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Ange ett namn.";
        if (r.QuantityMultiplier <= 0) return "Multiplikatorn måste vara större än 0.";
        if (r.QuantityDecimals is < 0 or > 6) return "Decimaler måste vara 0–6.";
        if (r.Type != BarcodeRuleType.Regex) return null;
        try
        {
            var rx = new Regex(r.Pattern, RegexOptions.None, RegexTimeout);
            var names = rx.GetGroupNames();
            if (!names.Contains("art") && !names.Contains("ean"))
                return "Mönstret måste innehålla gruppen (?<art>…) eller (?<ean>…).";
        }
        catch (ArgumentException ex) { return "Ogiltigt mönster: " + ex.Message; }
        return null;
    }

    /// <summary>
    /// Tolkar en kod. Ordning: extra streckkod (alias) → exakt EAN/artikelnr → regler i ordning.
    /// </summary>
    public static ScanResult? Parse(string code, IReadOnlyDictionary<string, BarcodeAlias> aliases,
        IReadOnlyList<BarcodeRule> rules, Func<string, Article?> byEan, Func<string, Article?> byArticleNo)
    {
        code = code.Trim();
        if (code.Length == 0) return null;

        if (aliases.TryGetValue(code, out var alias) && byArticleNo(alias.ArticleNo) is { } aa)
            return new ScanResult(aa, alias.Quantity, $"Extra streckkod ({Fmt(alias.Quantity)} st)");

        if (byEan(CodeLookup.NormalizeEan(code)) is { } e) return new ScanResult(e, null, "EAN");
        if (byArticleNo(code) is { } a) return new ScanResult(a, null, "Artikelnr");

        foreach (var r in rules.Where(r => r.IsActive).OrderBy(r => r.SortOrder).ThenBy(r => r.Id))
        {
            var res = r.Type == BarcodeRuleType.Gs1
                ? ParseGs1(code, r, aliases, byEan, byArticleNo)
                : ParseRegex(code, r, aliases, byEan, byArticleNo);
            if (res != null) return res;
        }
        return null;
    }

    private static ScanResult? ParseRegex(string code, BarcodeRule r, IReadOnlyDictionary<string, BarcodeAlias> aliases,
        Func<string, Article?> byEan, Func<string, Article?> byArticleNo)
    {
        Match m;
        try { m = Regex.Match(code, r.Pattern, RegexOptions.None, RegexTimeout); }
        catch (Exception) { return null; }
        if (!m.Success) return null;

        Article? art = null;
        decimal packQty = 1;
        if (m.Groups["art"] is { Success: true } g1) art = byArticleNo(g1.Value.Trim()) ?? byArticleNo(g1.Value.TrimStart('0'));
        if (art is null && m.Groups["ean"] is { Success: true } g2)
        {
            var ean = g2.Value.Trim();
            art = byEan(CodeLookup.NormalizeEan(ean));
            if (art is null && aliases.TryGetValue(ean, out var al)) { art = byArticleNo(al.ArticleNo); packQty = al.Quantity; }
        }
        if (art is null) return null;

        decimal? qty = null;
        if (m.Groups["qty"] is { Success: true } gq && decimal.TryParse(gq.Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var q))
            qty = q / Pow10(r.QuantityDecimals) * r.QuantityMultiplier * packQty;
        else if (r.QuantityMultiplier != 1 || packQty != 1)
            qty = r.QuantityMultiplier * packQty;

        return new ScanResult(art, qty, $"Regel: {r.Name}");
    }

    /// <summary>GS1 Application Identifiers. Stöder koder med parenteser "(01)…(30)…" och råa med GS-tecken.</summary>
    private static ScanResult? ParseGs1(string code, BarcodeRule r, IReadOnlyDictionary<string, BarcodeAlias> aliases,
        Func<string, Article?> byEan, Func<string, Article?> byArticleNo)
    {
        var ais = ParseAis(code);
        if (ais is null || !ais.TryGetValue("01", out var gtin) && !ais.TryGetValue("02", out gtin)) return null;

        // GTIN-14 → prova som EAN-13 (utan inledande 0), GTIN-14 och extra streckkod.
        decimal packQty = 1;
        var ean13 = gtin.Length == 14 && gtin[0] == '0' ? gtin[1..] : gtin;
        var art = byEan(ean13) ?? byEan(gtin);
        if (art is null)
        {
            if (aliases.TryGetValue(gtin, out var al) || aliases.TryGetValue(ean13, out al)) { art = byArticleNo(al.ArticleNo); packQty = al.Quantity; }
        }
        if (art is null) return null;

        decimal? qty = null;
        if (ais.TryGetValue("30", out var v) || ais.TryGetValue("37", out v))
        {
            if (decimal.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var q)) qty = q;
        }
        else
        {
            var weight = ais.Keys.FirstOrDefault(k => k.Length == 4 && k.StartsWith("310"));
            if (weight != null && decimal.TryParse(ais[weight], NumberStyles.None, CultureInfo.InvariantCulture, out var w))
                qty = w / Pow10(weight[3] - '0');
        }
        if (qty is { } x) qty = x * r.QuantityMultiplier * packQty;
        else if (r.QuantityMultiplier != 1 || packQty != 1) qty = r.QuantityMultiplier * packQty;

        return new ScanResult(art, qty, $"Regel: {r.Name} (GS1)");
    }

    // Fast längd per AI (utan AI-siffrorna); null = variabel längd (avslutas med GS eller slut).
    private static int? FixedLength(string ai) => ai switch
    {
        "00" => 18, "01" or "02" => 14, "11" or "12" or "13" or "15" or "16" or "17" => 6, "20" => 2,
        _ when ai.Length == 4 && ai[0] == '3' && ai[1] is >= '1' and <= '6' => 6,
        _ => null,
    };

    private static readonly string[] KnownVariable = ["10", "21", "22", "30", "37", "240", "241", "250", "251", "400", "401", "402", "403", "410", "411", "412", "413", "414", "415", "420", "421", "422", "90", "91", "92", "93", "94", "95", "96", "97", "98", "99"];

    public static Dictionary<string, string>? ParseAis(string code)
    {
        var result = new Dictionary<string, string>();
        code = code.Replace("]C1", "").Replace("]d2", "").Replace("]Q3", ""); // symbologiprefix från vissa skannrar

        if (code.StartsWith('('))
        {
            foreach (Match m in Regex.Matches(code, @"\((\d{2,4})\)([^(]*)", RegexOptions.None, RegexTimeout))
                result[m.Groups[1].Value] = m.Groups[2].Value.Trim(Gs, ' ');
            return result.Count > 0 ? result : null;
        }

        var i = 0;
        while (i < code.Length)
        {
            if (code[i] == Gs) { i++; continue; }
            string? ai = null;
            foreach (var len in new[] { 2, 3, 4 })
            {
                if (i + len > code.Length) break;
                var cand = code.Substring(i, len);
                if (!cand.All(char.IsAsciiDigit)) break;
                if (FixedLength(cand) != null && (len != 2 || !(cand[0] == '3' && cand[1] is >= '1' and <= '6'))) { ai = cand; break; }
                if (KnownVariable.Contains(cand)) { ai = cand; break; }
            }
            if (ai is null) return result.Count > 0 ? result : null;
            i += ai.Length;
            var fixedLen = FixedLength(ai);
            string value;
            if (fixedLen is { } fl)
            {
                if (i + fl > code.Length) return null;
                value = code.Substring(i, fl);
                i += fl;
            }
            else
            {
                var end = code.IndexOf(Gs, i);
                if (end < 0) end = code.Length;
                value = code[i..end];
                i = end;
            }
            result[ai] = value;
        }
        return result.Count > 0 ? result : null;
    }

    private static decimal Pow10(int n) { decimal d = 1; for (var i = 0; i < n; i++) d *= 10; return d; }
    private static string Fmt(decimal d) => d.ToString("0.###", CultureInfo.GetCultureInfo("sv-SE"));
}
