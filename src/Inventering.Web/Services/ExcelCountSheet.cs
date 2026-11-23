using System.Globalization;
using System.Security.Cryptography;
using ClosedXML.Excel;
using Inventering.Core.Domain;

namespace Inventering.Web.Services;

public record CountSheetOptions(bool IncludeStock = false, bool OnlyUncounted = false, string? ArticleFrom = null, string? ArticleTo = null);

public record ImportRow(int Row, string ArticleNo, string? Name, decimal Quantity, string? Location, string? Comment, string? Error);

public record ImportResult(string FileName, string FileHash, string? CountedBy, int? SessionIdInFile, int? CompanyIdInFile,
    List<ImportRow> Rows, List<string> Warnings)
{
    public IEnumerable<ImportRow> Valid => Rows.Where(r => r.Error == null);
    public IEnumerable<ImportRow> Invalid => Rows.Where(r => r.Error != null);
}

/// <summary>
/// Inventeringslista i Excel: skapas från en inventering, fylls i på papper/dator och läses in igen.
/// Inlästa rader blir en inlämning precis som från mobilen.
/// </summary>
public static class ExcelCountSheet
{
    private const string MetaSheet = "_inventering";
    private const int HeaderRow = 7;
    private static readonly XLColor Accent = XLColor.FromHtml("#16325c");
    private static readonly XLColor InputFill = XLColor.FromHtml("#fff8e1");

    public static byte[] Create(Company company, InventorySession session, IEnumerable<Article> articles, CountSheetOptions o)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Inventering");
        ws.ShowGridLines = false;

        ws.Cell(1, 1).Value = "Inventeringslista";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(16).Font.SetFontColor(Accent);
        ws.Cell(2, 1).Value = company.Name;
        ws.Cell(2, 1).Style.Font.SetFontSize(12);
        ws.Cell(3, 1).Value = $"#{session.Id} {session.Name}";
        ws.Cell(4, 1).Value = "Räknat av:";
        ws.Cell(4, 1).Style.Font.SetBold();
        ws.Range(4, 2, 4, 3).Merge().Style.Fill.SetBackgroundColor(InputFill).Border.SetBottomBorder(XLBorderStyleValues.Thin);
        ws.Cell(5, 1).Value = "Fyll i räknat antal i den gula kolumnen. Tomma rader läses inte in. Ändra inte artikelnummer eller kolumnrubriker.";
        ws.Cell(5, 1).Style.Font.SetItalic().Font.SetFontColor(XLColor.FromHtml("#667085"));

        var headers = new List<string> { "Artikelnr", "Benämning", "Enhet", "EAN" };
        if (o.IncludeStock) headers.Add("Saldo i Visma");
        headers.AddRange(["Plats", "Räknat antal", "Kommentar"]);
        for (var c = 0; c < headers.Count; c++)
        {
            var cell = ws.Cell(HeaderRow, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.SetBold().Font.SetFontColor(XLColor.White).Fill.SetBackgroundColor(Accent);
        }

        var list = articles.Where(a => a.IsStockItem)
            .Where(a => o.ArticleFrom is null || string.Compare(a.ArticleNo, o.ArticleFrom, StringComparison.OrdinalIgnoreCase) >= 0)
            .Where(a => o.ArticleTo is null || string.Compare(a.ArticleNo, o.ArticleTo, StringComparison.OrdinalIgnoreCase) <= 0)
            .OrderBy(a => a.ArticleNo, StringComparer.OrdinalIgnoreCase).ToList();
        var row = HeaderRow + 1;
        foreach (var a in list)
        {
            var c = 1;
            ws.Cell(row, c++).SetValue(a.ArticleNo);
            ws.Cell(row, c++).Value = a.Name;
            ws.Cell(row, c++).Value = a.Unit ?? "";
            ws.Cell(row, c++).SetValue(a.Ean ?? "");
            if (o.IncludeStock) ws.Cell(row, c++).Value = a.StockQuantity;
            c += 2; // Plats, Räknat antal
            row++;
        }
        var last = Math.Max(row - 1, HeaderRow + 1);
        var qtyCol = headers.IndexOf("Räknat antal") + 1;
        var placeCol = headers.IndexOf("Plats") + 1;
        var commentCol = headers.IndexOf("Kommentar") + 1;

        // Inmatningsceller: gul bakgrund, olåsta, endast tal ≥ 0.
        foreach (var col in new[] { placeCol, qtyCol, commentCol })
            ws.Range(HeaderRow + 1, col, last, col).Style.Protection.SetLocked(false);
        ws.Range(4, 2, 4, 3).Style.Protection.SetLocked(false);
        var qty = ws.Range(HeaderRow + 1, qtyCol, last, qtyCol);
        qty.Style.Fill.SetBackgroundColor(InputFill).NumberFormat.Format = "#,##0.###";
        qty.CreateDataValidation().Decimal.EqualOrGreaterThan(0);
        ws.Range(HeaderRow, 1, last, headers.Count).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin)
            .Border.SetInsideBorder(XLBorderStyleValues.Hair).Border.SetInsideBorderColor(XLColor.FromHtml("#d0d5dd"));
        ws.Range(HeaderRow, 1, last, headers.Count).SetAutoFilter();
        ws.SheetView.FreezeRows(HeaderRow);
        ws.Column(1).Width = 14; ws.Column(2).Width = 42; ws.Column(3).Width = 8; ws.Column(4).Width = 16;
        for (var c = 5; c <= headers.Count; c++) ws.Column(c).Width = 14;
        ws.Column(commentCol).Width = 30;
        // Skydda strukturen men tillåt sortering/filter. Inget lösenord – det är ett skydd mot misstag, inte säkerhet.
        ws.Protect().AllowElement(XLSheetProtectionElements.AutoFilter | XLSheetProtectionElements.Sort | XLSheetProtectionElements.FormatColumns);

        ws.PageSetup.PageOrientation = XLPageOrientation.Portrait;
        ws.PageSetup.FitToPages(1, 0);
        ws.PageSetup.SetRowsToRepeatAtTop(HeaderRow, HeaderRow);
        ws.PageSetup.Header.Center.AddText($"{company.Name} – Inventering #{session.Id}");
        ws.PageSetup.Footer.Left.AddText("Räknat av: ______________________   Datum: ____________");

        // Dold metadata så att importen vet vilken inventering filen hör till.
        var meta = wb.AddWorksheet(MetaSheet);
        meta.Cell(1, 1).Value = "sessionId"; meta.Cell(1, 2).Value = session.Id;
        meta.Cell(2, 1).Value = "companyId"; meta.Cell(2, 2).Value = company.Id;
        meta.Cell(3, 1).Value = "created"; meta.Cell(3, 2).Value = DateTime.Now;
        meta.Cell(4, 1).Value = "format"; meta.Cell(4, 2).Value = 1;
        meta.Visibility = XLWorksheetVisibility.VeryHidden;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Läser en ifylld lista. Fungerar även med egna Excel-filer som har kolumnerna
    /// "Artikelnr" och "Räknat antal" (eller "Antal").
    /// </summary>
    public static ImportResult Read(Stream file, string fileName, Func<string, Article?> lookup)
    {
        using var ms = new MemoryStream();
        file.CopyTo(ms);
        var hash = Convert.ToHexString(SHA256.HashData(ms.ToArray()));
        ms.Position = 0;

        XLWorkbook wb;
        try { wb = new XLWorkbook(ms); }
        catch (Exception) { throw new InvalidDataException("Filen kunde inte läsas som Excel (.xlsx)."); }
        using var _ = wb;

        int? sessionId = null, companyId = null;
        if (wb.Worksheets.TryGetWorksheet(MetaSheet, out var meta))
        {
            if (meta.Cell(1, 2).TryGetValue<int>(out var sid)) sessionId = sid;
            if (meta.Cell(2, 2).TryGetValue<int>(out var cid)) companyId = cid;
        }

        var warnings = new List<string>();
        foreach (var ws in wb.Worksheets.Where(w => w.Name != MetaSheet && w.Visibility == XLWorksheetVisibility.Visible))
        {
            var (headerRow, cols) = FindHeader(ws);
            if (headerRow is null) continue;

            string? countedBy = null;
            for (var r = 1; r < headerRow; r++)
                if (ws.Cell(r, 1).GetString().Trim().StartsWith("Räknat av", StringComparison.OrdinalIgnoreCase))
                    countedBy = ws.Cell(r, 2).GetString().Trim() is { Length: > 0 } n ? n : null;

            var rows = new List<ImportRow>();
            var lastRow = ws.LastRowUsed()?.RowNumber() ?? 0;
            for (var r = headerRow.Value + 1; r <= lastRow; r++)
            {
                var qtyCell = ws.Cell(r, cols["qty"]);
                var no = ws.Cell(r, cols["art"]).GetString().Trim();
                if (qtyCell.IsEmpty() || string.IsNullOrWhiteSpace(qtyCell.GetString())) continue; // ej ifylld = ej räknad
                var location = cols.TryGetValue("loc", out var lc) ? Blank(ws.Cell(r, lc).GetString()) : null;
                var comment = cols.TryGetValue("comment", out var cc) ? Blank(ws.Cell(r, cc).GetString()) : null;

                string? error = null;
                decimal qty = 0;
                if (!TryDecimal(qtyCell, out qty)) error = $"Antalet \"{qtyCell.GetString()}\" är inte ett tal";
                else if (qty < 0) error = "Negativt antal";
                else if (qty > 1_000_000) error = "Orimligt antal";
                var art = no.Length == 0 ? null : lookup(no);
                if (error == null && no.Length == 0) error = "Artikelnummer saknas";
                else if (error == null && art is null) error = $"Artikel {no} finns inte i bolaget";
                else if (error == null && !art!.IsStockItem) error = $"Artikel {no} är inte lagerförd";
                rows.Add(new ImportRow(r, art?.ArticleNo ?? no, art?.Name, qty, location, comment, error));
            }
            if (rows.Count == 0) warnings.Add("Inga ifyllda rader hittades – kolumnen \"Räknat antal\" är tom.");
            return new ImportResult(fileName, hash, countedBy, sessionId, companyId, rows, warnings);
        }
        throw new InvalidDataException("Hittade ingen flik med kolumnerna \"Artikelnr\" och \"Räknat antal\".");
    }

    private static (int? Row, Dictionary<string, int> Cols) FindHeader(IXLWorksheet ws)
    {
        var lastRow = Math.Min(ws.LastRowUsed()?.RowNumber() ?? 0, 30);
        var lastCol = Math.Min(ws.LastColumnUsed()?.ColumnNumber() ?? 0, 40);
        for (var r = 1; r <= lastRow; r++)
        {
            var cols = new Dictionary<string, int>();
            for (var c = 1; c <= lastCol; c++)
            {
                var h = ws.Cell(r, c).GetString().Trim().ToLowerInvariant();
                if (h is "artikelnr" or "artikelnummer" or "art.nr" or "artnr") cols.TryAdd("art", c);
                else if (h is "räknat antal" or "räknat" or "antal" or "inventerat antal") cols.TryAdd("qty", c);
                else if (h is "plats" or "lagerplats" or "hylla") cols.TryAdd("loc", c);
                else if (h is "kommentar" or "anmärkning") cols.TryAdd("comment", c);
            }
            if (cols.ContainsKey("art") && cols.ContainsKey("qty")) return (r, cols);
        }
        return (null, new());
    }

    private static bool TryDecimal(IXLCell cell, out decimal value)
    {
        if (cell.DataType == XLDataType.Number) { value = (decimal)cell.GetDouble(); return true; }
        var s = cell.GetString().Trim().Replace(" ", "").Replace(' '.ToString(), "").Replace(',', '.');
        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    private static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
