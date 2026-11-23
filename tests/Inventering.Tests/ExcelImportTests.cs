using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Inventering.Tests;

public class ExcelImportTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "inv-xl-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public ExcelImportTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DataDirectory", _dataDir);
            b.UseSetting("Security:RequireTotpForAdmin", "false");
        });
    }

    private async Task<(HttpClient Client, int SessionId)> AdminWithSession()
    {
        _factory.Server.ToString();
        var pw = File.ReadAllText(Path.Combine(_dataDir, "forsta-admin-losenord.txt")).Split('\n').First(l => l.StartsWith("Lösenord:"))[9..].Trim();
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-Requested-With", "inventering");
        (await c.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = pw })).EnsureSuccessStatusCode();
        var me = await c.GetFromJsonAsync<JsonElement>("/api/auth/me");
        (await c.PostAsJsonAsync("/api/auth/company", new { companyId = me.GetProperty("companies")[0].GetProperty("id").GetInt32() })).EnsureSuccessStatusCode();
        var s = await (await c.PostAsJsonAsync("/api/admin/sessions", new { name = "Excel" })).Content.ReadFromJsonAsync<JsonElement>();
        return (c, s.GetProperty("id").GetInt32());
    }

    private static MultipartFormDataContent Form(byte[] file, string name = "lista.xlsx")
    {
        var content = new ByteArrayContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        return new MultipartFormDataContent { { content, "file", name } };
    }

    [Fact]
    public async Task Countsheet_roundtrip_creates_submission()
    {
        var (c, sid) = await AdminWithSession();
        var sheet = await c.GetByteArrayAsync($"/api/admin/sessions/{sid}/countsheet.xlsx?includeStock=true");

        // Fyll i listan som en räknare skulle göra
        byte[] filled;
        using (var wb = new XLWorkbook(new MemoryStream(sheet)))
        {
            var ws = wb.Worksheet("Inventering");
            Assert.Equal("Artikelnr", ws.Cell(7, 1).GetString());
            Assert.Equal("Räknat antal", ws.Cell(7, 7).GetString());
            ws.Cell(4, 2).Value = "Kalle Kartong";
            ws.Cell(8, 7).Value = 38;                // 1001
            ws.Cell(9, 7).Value = "24,5";            // 1002, text med komma
            ws.Cell(9, 6).Value = "B2";
            ws.Cell(10, 7).Value = "många";          // 1003 – fel
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            filled = ms.ToArray();
        }

        var preview = await (await c.PostAsync($"/api/admin/sessions/{sid}/import", Form(filled))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, preview.GetProperty("valid").GetInt32());
        Assert.Equal(1, preview.GetProperty("invalid").GetInt32());
        Assert.Equal("Kalle Kartong", preview.GetProperty("countedBy").GetString());

        var commit = await c.PostAsync($"/api/admin/sessions/{sid}/import?commit=true", Form(filled));
        Assert.Equal(HttpStatusCode.OK, commit.StatusCode);

        var summary = await c.GetFromJsonAsync<JsonElement>($"/api/admin/sessions/{sid}/summary");
        var arts = summary.GetProperty("articles").EnumerateArray().ToDictionary(a => a.GetProperty("articleNo").GetString()!, a => a.GetProperty("countedQuantity").GetDecimal());
        Assert.Equal(38m, arts["1001"]);
        Assert.Equal(24.5m, arts["1002"]);
        Assert.False(arts.ContainsKey("1003"));

        var subs = await c.GetFromJsonAsync<JsonElement>($"/api/admin/sessions/{sid}/submissions");
        var sub = subs.EnumerateArray().Single();
        Assert.Equal("excel-import", sub.GetProperty("signedWith").GetString());
        Assert.True(sub.GetProperty("intact").GetBoolean());

        // Samma fil igen nekas
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/api/admin/sessions/{sid}/import?commit=true", Form(filled))).StatusCode);
    }

    [Fact]
    public async Task Own_excel_with_matching_columns_is_accepted()
    {
        var (c, sid) = await AdminWithSession();
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Blad1");
        ws.Cell(1, 1).Value = "Artikelnummer"; ws.Cell(1, 2).Value = "Antal";
        ws.Cell(2, 1).Value = "3001"; ws.Cell(2, 2).Value = 55;
        ws.Cell(3, 1).Value = "9999"; ws.Cell(3, 2).Value = 1;
        using var ms = new MemoryStream(); wb.SaveAs(ms);
        var preview = await (await c.PostAsync($"/api/admin/sessions/{sid}/import", Form(ms.ToArray(), "egen.xlsx"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, preview.GetProperty("valid").GetInt32());
        Assert.Contains(preview.GetProperty("rows").EnumerateArray(), r => r.GetProperty("error").GetString()?.Contains("9999") == true);
    }

    [Fact]
    public async Task Non_excel_file_is_rejected()
    {
        var (c, sid) = await AdminWithSession();
        var r = await c.PostAsync($"/api/admin/sessions/{sid}/import", Form("hej"u8.ToArray(), "x.xlsx"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
