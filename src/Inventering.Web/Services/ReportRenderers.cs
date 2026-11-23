using System.Globalization;
using System.Net;
using System.Text;
using ClosedXML.Excel;

namespace Inventering.Web.Services;

/// <summary>Utskriftsvänlig HTML-rapport (A4, liggande). Öppnas i webbläsaren och skrivs ut / sparas som PDF.</summary>
public static class HtmlReport
{
    private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string N(decimal d) => d.ToString("#,0.###", Sv);
    private static string N(decimal? d) => d is { } x ? N(x) : "";
    private static string Kr(decimal? d) => d is { } x ? x.ToString("#,0.00", Sv) + " kr" : "";
    private static string Signed(decimal d) => d > 0 ? "+" + N(d) : N(d);
    private static string Cls(decimal d) => d > 0 ? "pos" : d < 0 ? "neg" : "";
    private static string T(DateTimeOffset? t) => t?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", Sv) ?? "–";

    public static string Render(ReportModel m, bool onlyDifferences)
    {
        var s = m.Session;
        var rows = onlyDifferences ? m.Articles.Where(a => a.Difference != 0).ToList() : m.Articles;
        var priceCols = m.HasPrices;
        var sb = new StringBuilder();
        sb.Append($$"""
<!doctype html>
<html lang="sv"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Inventeringsrapport – {{E(m.CompanyName)}} – #{{s.Id}} {{E(s.Name)}}</title>
<link rel="stylesheet" href="/css/report.css">
<script src="/js/report.js" defer></script>
</head><body>
<div class="toolbar no-print">
  <button id="printBtn">Skriv ut / spara som PDF</button>
  <a href="?onlyDiff={{(onlyDifferences ? "0" : "1")}}">{{(onlyDifferences ? "Visa alla artiklar" : "Visa bara differenser")}}</a>
  <a href="report.xlsx">Ladda ner Excel</a>
</div>
<header class="head">
  <div>
    <div class="company">{{E(m.CompanyName)}}</div>
    <h1>Inventeringsrapport</h1>
    <div class="sub">#{{s.Id}} {{E(s.Name)}}</div>
  </div>
  <table class="meta">
    <tr><th>Status</th><td>{{E(ReportModel.StatusText(s.Status))}}</td></tr>
    <tr><th>Startad</th><td>{{T(s.CreatedAt)}}{{(m.CreatedBy is null ? "" : " av " + E(m.CreatedBy))}}</td></tr>
    <tr><th>Låst</th><td>{{T(s.LockedAt)}}</td></tr>
    <tr><th>Skickad till Visma</th><td>{{T(s.SentAt)}}{{(m.SentBy is null ? "" : " av " + E(m.SentBy))}}</td></tr>
    <tr><th>Utskriven</th><td>{{T(m.GeneratedAt)}} av {{E(m.GeneratedBy)}}</td></tr>
  </table>
</header>

<section class="kpis">
  <div><span>{{m.Articles.Count}}</span>räknade artiklar</div>
  <div><span>{{m.WithDifference}}</span>med differens</div>
  <div><span class="pos">{{Signed(m.PlusQuantity)}}</span>övertal (antal)</div>
  <div><span class="neg">{{N(m.MinusQuantity)}}</span>undertal (antal)</div>
""");
        if (priceCols)
            sb.Append($$"""
  <div><span class="{{Cls(m.NetValue)}}">{{Kr(m.NetValue)}}</span>nettodifferens i värde</div>
  <div><span>{{Kr(m.CountedValue)}}</span>räknat lagervärde</div>
""");
        sb.Append($$"""
  <div><span>{{m.Submissions.Count}}</span>inlämningar</div>
  <div><span>{{m.Uncounted.Count}}</span>ej räknade med saldo</div>
</section>
""");
        if (m.DraftLines > 0)
            sb.Append($"<p class=\"warn\">{(m.DraftLines == 1 ? "1 rad är inte inlämnad och ingår" : $"{m.DraftLines} rader är inte inlämnade och ingår")} inte i rapporten.</p>");
        if (s.Status == Core.Domain.SessionStatus.Sent)
            sb.Append($"<p class=\"{(m.SendFailed > 0 ? "warn" : "ok")}\">Skickat till Vismas inventeringsunderlag: {m.SendOk} artiklar OK{(m.SendFailed > 0 ? $", {m.SendFailed} misslyckades" : "")}.</p>");

        sb.Append($$"""
<h2>{{(onlyDifferences ? "Artiklar med differens" : "Räknade artiklar")}} ({{rows.Count}})</h2>
<table class="data">
<thead><tr><th>Artikelnr</th><th>Benämning</th><th>Enhet</th><th class="n">Saldo vid start</th><th class="n">Räknat</th><th class="n">Differens</th>
{{(priceCols ? "<th class=\"n\">Inpris</th><th class=\"n\">Värde diff.</th>" : "")}}<th>Räknat av</th><th>Anm.</th></tr></thead><tbody>
""");
        foreach (var a in rows)
        {
            var note = string.Join(" ", new[] { a.Warnings, a.SendStatus is { } st && st != "OK" ? st : null }.Where(x => !string.IsNullOrEmpty(x)));
            sb.Append($"<tr><td>{E(a.ArticleNo)}</td><td>{E(a.Name)}</td><td>{E(a.Unit)}</td><td class=\"n\">{N(a.SnapshotQuantity)}</td>" +
                      $"<td class=\"n b\">{N(a.CountedQuantity)}</td><td class=\"n {Cls(a.Difference)}\">{Signed(a.Difference)}</td>" +
                      (priceCols ? $"<td class=\"n\">{Kr(a.Price)}</td><td class=\"n {Cls(a.DifferenceValue ?? 0)}\">{Kr(a.DifferenceValue)}</td>" : "") +
                      $"<td>{E(a.CountedBy)}</td><td class=\"note\">{E(note)}</td></tr>");
        }
        sb.Append($$"""
</tbody><tfoot><tr><td colspan="3">Summa</td><td class="n">{{N(rows.Sum(a => a.SnapshotQuantity))}}</td><td class="n">{{N(rows.Sum(a => a.CountedQuantity))}}</td>
<td class="n">{{Signed(rows.Sum(a => a.Difference))}}</td>{{(priceCols ? $"<td></td><td class=\"n\">{Kr(rows.Sum(a => a.DifferenceValue ?? 0))}</td>" : "")}}<td colspan="2"></td></tr></tfoot>
</table>
""");

        if (m.Uncounted.Count > 0 && !onlyDifferences)
        {
            sb.Append($"<h2>Lagerartiklar med saldo som inte räknats ({m.Uncounted.Count})</h2><table class=\"data\"><thead><tr><th>Artikelnr</th><th>Benämning</th><th>Enhet</th><th class=\"n\">Saldo</th>{(priceCols ? "<th class=\"n\">Inpris</th><th class=\"n\">Värde</th>" : "")}</tr></thead><tbody>");
            foreach (var u in m.Uncounted)
                sb.Append($"<tr><td>{E(u.ArticleNo)}</td><td>{E(u.Name)}</td><td>{E(u.Unit)}</td><td class=\"n\">{N(u.Stock)}</td>{(priceCols ? $"<td class=\"n\">{Kr(u.Price)}</td><td class=\"n\">{Kr(u.Value)}</td>" : "")}</tr>");
            sb.Append("</tbody></table>");
        }

        sb.Append("<h2>Inlämningar (signerade av räknarna)</h2><table class=\"data\"><thead><tr><th>#</th><th>Räknare</th><th>Inlämnad</th><th class=\"n\">Rader</th><th class=\"n\">Totalt antal</th><th>Signerad med</th><th>Kontrollsumma</th><th>Kommentar</th></tr></thead><tbody>");
        foreach (var x in m.Submissions)
            sb.Append($"<tr><td>{x.Id}</td><td>{E(x.User)}</td><td>{T(x.SubmittedAt)}</td><td class=\"n\">{x.Lines}</td><td class=\"n\">{N(x.Total)}</td><td>{E(x.SignedWith)}</td>" +
                      $"<td>{(x.Intact ? "<span class=\"ok\">Intakt</span>" : "<span class=\"neg\">Ändrad efter signering</span>")} <code>{E(x.Hash[..12])}</code></td><td>{E(x.Comment)}</td></tr>");
        if (m.Submissions.Count == 0) sb.Append("<tr><td colspan=\"8\">Inga inlämningar.</td></tr>");
        sb.Append("</tbody></table>");

        sb.Append($$"""
<section class="sign">
  <div><div class="line"></div>Inventeringsansvarig – namnförtydligande</div>
  <div><div class="line"></div>Underskrift</div>
  <div><div class="line"></div>Datum</div>
</section>
<footer>{{E(m.CompanyName)}} · Inventering #{{s.Id}} · {{E(m.CompanyPath)}} · Utskriven {{T(m.GeneratedAt)}}</footer>
</body></html>
""");
        return sb.ToString();
    }
}

/// <summary>Excel-rapport med flikar: Sammanfattning, Artiklar, Ej räknade, Inlämningar, Alla rader.</summary>
public static class ExcelReport
{
    private const string Qty = "#,##0.###;-#,##0.###;0";
    private const string Money = "#,##0.00 \"kr\";-#,##0.00 \"kr\";0.00 \"kr\"";

    public static byte[] Render(ReportModel m)
    {
        using var wb = new XLWorkbook();
        var s = m.Session;

        // ---- Sammanfattning ----
        var ws = wb.AddWorksheet("Sammanfattning");
        ws.Cell(1, 1).Value = "Inventeringsrapport";
        ws.Cell(1, 1).Style.Font.SetBold().Font.SetFontSize(18);
        ws.Cell(2, 1).Value = m.CompanyName;
        ws.Cell(2, 1).Style.Font.SetFontSize(13);
        var info = new (string, XLCellValue)[]
        {
            ("Inventering", $"#{s.Id} {s.Name}"),
            ("Status", ReportModel.StatusText(s.Status)),
            ("Startad", s.CreatedAt.LocalDateTime), ("Startad av", m.CreatedBy ?? ""),
            ("Låst", s.LockedAt is { } l ? l.LocalDateTime : ""),
            ("Skickad till Visma", s.SentAt is { } t ? t.LocalDateTime : ""), ("Skickad av", m.SentBy ?? ""),
            ("Visma-mapp", m.CompanyPath),
            ("Utskriven", m.GeneratedAt.LocalDateTime), ("Utskriven av", m.GeneratedBy),
            ("", ""),
            ("Räknade artiklar", m.Articles.Count), ("Med differens", m.WithDifference),
            ("Övertal (antal)", m.PlusQuantity), ("Undertal (antal)", m.MinusQuantity),
            ("Nettodifferens i värde", m.HasPrices ? m.NetValue : ""), ("Räknat lagervärde", m.HasPrices ? m.CountedValue : ""),
            ("Inlämningar", m.Submissions.Count), ("Räknare", string.Join(", ", m.Counters)),
            ("Ej räknade lagerartiklar med saldo", m.Uncounted.Count), ("Ej inlämnade rader (ingår ej)", m.DraftLines),
            ("Skickat OK / fel", s.Status == Core.Domain.SessionStatus.Sent ? $"{m.SendOk} / {m.SendFailed}" : ""),
        };
        for (var i = 0; i < info.Length; i++)
        {
            ws.Cell(4 + i, 1).Value = info[i].Item1;
            ws.Cell(4 + i, 1).Style.Font.SetBold();
            var c = ws.Cell(4 + i, 2);
            c.Value = info[i].Item2;
            c.Style.Alignment.SetHorizontal(XLAlignmentHorizontalValues.Left);
            if (info[i].Item2.IsDateTime) c.Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
            if (info[i].Item1.Contains("värde")) c.Style.NumberFormat.Format = Money;
        }
        ws.Column(1).Width = 34;
        ws.Column(2).Width = 50;

        // ---- Artiklar ----
        var headers = new List<string> { "Artikelnr", "Benämning", "Enhet", "Saldo vid start", "Räknat", "Differens" };
        if (m.HasPrices) headers.AddRange(["Inpris", "Värde differens"]);
        headers.AddRange(["Räknat av", "Varningar", "Visma"]);
        var wa = wb.AddWorksheet("Artiklar");
        var rows = m.Articles.Select(a =>
        {
            var r = new List<XLCellValue> { a.ArticleNo, a.Name, a.Unit ?? "", a.SnapshotQuantity, a.CountedQuantity, a.Difference };
            if (m.HasPrices) { r.Add(a.Price is { } p ? p : ""); r.Add(a.DifferenceValue is { } v ? v : ""); }
            r.AddRange([a.CountedBy, a.Warnings, a.SendStatus ?? ""]);
            return r;
        }).ToList();
        var table = Fill(wa, headers, rows, "Artiklar");
        Format(wa, table, ["Saldo vid start", "Räknat", "Differens"], Qty);
        if (m.HasPrices) Format(wa, table, ["Inpris", "Värde differens"], Money);
        table.ShowTotalsRow = true;
        table.Field("Artikelnr").TotalsRowLabel = "Summa";
        foreach (var f in new[] { "Saldo vid start", "Räknat", "Differens" }.Concat(m.HasPrices ? ["Värde differens"] : Array.Empty<string>()))
            table.Field(f).TotalsRowFunction = XLTotalsRowFunction.Sum;
        var diffCol = table.Field("Differens").Column.ColumnNumber();
        var diffRange = wa.Range(2, diffCol, rows.Count + 1, diffCol);
        diffRange.AddConditionalFormat().WhenLessThan(0).Font.SetFontColor(XLColor.FromHtml("#c0362c"));
        diffRange.AddConditionalFormat().WhenGreaterThan(0).Font.SetFontColor(XLColor.FromHtml("#1a7f45"));

        // ---- Ej räknade ----
        var wu = wb.AddWorksheet("Ej räknade");
        var uh = new List<string> { "Artikelnr", "Benämning", "Enhet", "Saldo" };
        if (m.HasPrices) uh.AddRange(["Inpris", "Värde"]);
        var ut = Fill(wu, uh, m.Uncounted.Select(u =>
        {
            var r = new List<XLCellValue> { u.ArticleNo, u.Name, u.Unit ?? "", u.Stock };
            if (m.HasPrices) { r.Add(u.Price is { } p ? p : ""); r.Add(u.Value is { } v ? v : ""); }
            return r;
        }).ToList(), "EjRaknade");
        Format(wu, ut, ["Saldo"], Qty);
        if (m.HasPrices) Format(wu, ut, ["Inpris", "Värde"], Money);

        // ---- Inlämningar ----
        var wsb = wb.AddWorksheet("Inlämningar");
        var st = Fill(wsb, ["Inlämning", "Räknare", "Inlämnad", "Rader", "Totalt antal", "Signerad med", "Intakt", "Kontrollsumma (SHA-256)", "Kommentar"],
            m.Submissions.Select(x => new List<XLCellValue>
            {
                x.Id, x.User, x.SubmittedAt.LocalDateTime, x.Lines, x.Total, x.SignedWith, x.Intact ? "Ja" : "NEJ – ändrad", x.Hash, x.Comment ?? "",
            }).ToList(), "Inlamningar");
        Format(wsb, st, ["Inlämnad"], null, "yyyy-mm-dd hh:mm");
        Format(wsb, st, ["Totalt antal"], Qty);

        // ---- Alla rader (spårbarhet) ----
        var wl = wb.AddWorksheet("Alla rader");
        var lt = Fill(wl, ["Artikelnr", "Benämning", "Antal", "Plats", "Kommentar", "Skannad kod", "Tolkad via", "Räknad", "Räknare", "Enhet", "Inlämning", "Struken", "Struken av", "Rad-id"],
            m.Lines.Select(l => new List<XLCellValue>
            {
                l.ArticleNo, l.Name, l.Quantity, l.Location ?? "", l.Comment ?? "", l.ScannedCode ?? "", l.ScanVia ?? "",
                l.CountedAt.LocalDateTime, l.User, l.Device ?? "", l.SubmissionId is { } sid ? sid : "Utkast",
                l.Voided ? "Ja" : "", l.VoidedBy ?? "", l.Id.ToString(),
            }).ToList(), "Rader");
        Format(wl, lt, ["Antal"], Qty);
        Format(wl, lt, ["Räknad"], null, "yyyy-mm-dd hh:mm:ss");

        foreach (var sheet in wb.Worksheets)
        {
            sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            sheet.PageSetup.FitToPages(1, 0);
            sheet.PageSetup.Header.Center.AddText($"{m.CompanyName} – Inventering #{s.Id} {s.Name}");
            sheet.PageSetup.Footer.Right.AddText("Sida ");
            sheet.PageSetup.Footer.Right.AddText(XLHFPredefinedText.PageNumber);
            sheet.PageSetup.Footer.Right.AddText(" av ");
            sheet.PageSetup.Footer.Right.AddText(XLHFPredefinedText.NumberOfPages);
        }
        wa.SetTabActive();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static IXLTable Fill(IXLWorksheet ws, IReadOnlyList<string> headers, List<List<XLCellValue>> rows, string name)
    {
        for (var c = 0; c < headers.Count; c++) ws.Cell(1, c + 1).Value = headers[c];
        for (var r = 0; r < rows.Count; r++)
            for (var c = 0; c < rows[r].Count; c++) ws.Cell(r + 2, c + 1).Value = rows[r][c];
        var table = ws.Range(1, 1, Math.Max(rows.Count, 1) + 1, headers.Count).CreateTable(name);
        table.Theme = XLTableTheme.TableStyleLight9;
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, headers.Count).AdjustToContents(1, Math.Min(rows.Count + 1, 500), 8, 60);
        return table;
    }

    private static void Format(IXLWorksheet ws, IXLTable t, IEnumerable<string> fields, string? number, string? date = null)
    {
        foreach (var f in fields)
        {
            var col = ws.Column(t.Field(f).Column.ColumnNumber());
            if (number != null) col.Style.NumberFormat.Format = number;
            if (date != null) col.Style.DateFormat.Format = date;
        }
    }
}
