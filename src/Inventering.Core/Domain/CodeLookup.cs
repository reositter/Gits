namespace Inventering.Core.Domain;

public static class CodeLookup
{
    /// <summary>
    /// Slår upp en skannad eller inskriven kod. Ordning: exakt EAN → exakt artikelnr.
    /// Artiklar utan EAN hittas därmed på artikelnr (t.ex. egna etiketter med Code128 av artikelnr).
    /// </summary>
    public static Article? Find(string code, IReadOnlyDictionary<string, Article> byEan, IReadOnlyDictionary<string, Article> byArticleNo)
    {
        code = code.Trim();
        if (code.Length == 0) return null;
        if (byEan.TryGetValue(NormalizeEan(code), out var a)) return a;
        if (byArticleNo.TryGetValue(code, out a)) return a;
        return null;
    }

    /// <summary>UPC-A (12 siffror) skannas ibland som EAN-13 med inledande 0 – normalisera till 13 siffror.</summary>
    public static string NormalizeEan(string code)
    {
        code = code.Trim();
        return code.Length == 12 && code.All(char.IsAsciiDigit) ? "0" + code : code;
    }
}
