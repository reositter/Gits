using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventering.Core.Visma;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Inventering.Tests;

public class ApiFlowTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "inv-test-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;

    public ApiFlowTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DataDirectory", _dataDir);
            b.UseSetting("Security:RequireTotpForAdmin", "false");
            b.UseSetting("Visma:Mode", "Fake");
        });
    }

    private HttpClient Client()
    {
        var c = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        c.DefaultRequestHeaders.Add("X-Requested-With", "inventering");
        return c;
    }

    private string AdminPassword()
    {
        _factory.Server.ToString(); // starta servern → första admin skapas
        var text = File.ReadAllText(Path.Combine(_dataDir, "forsta-admin-losenord.txt"));
        return text.Split('\n').First(l => l.StartsWith("Lösenord:"))["Lösenord:".Length..].Trim();
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        Assert.True(r.IsSuccessStatusCode, $"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        var body = await r.Content.ReadAsStringAsync();
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task Full_inventory_flow_with_two_counters()
    {
        var admin = Client();
        await Json(await admin.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AdminPassword() }));

        // Två räknare
        var pw = new Dictionary<string, string>();
        foreach (var u in new[] { "anna", "bo" })
        {
            var r = await Json(await admin.PostAsJsonAsync("/api/admin/users", new { userName = u, role = "Counter" }));
            pw[u] = r.GetProperty("password").GetString()!;
        }

        // Starta inventering (fake-Visma: 1001 har saldo 40)
        var sid = (await Json(await admin.PostAsJsonAsync("/api/admin/sessions", new { name = "Test" }))).GetProperty("id").GetInt32();

        var anna = Client();
        await Json(await anna.PostAsJsonAsync("/api/auth/login", new { userName = "anna", password = pw["anna"] }));
        var bo = Client();
        await Json(await bo.PostAsJsonAsync("/api/auth/login", new { userName = "bo", password = pw["bo"] }));

        // Räknare får inte nå admin-API
        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync("/api/admin/sessions")).StatusCode);

        // Uppslag via EAN och via artikelnr (artikel utan EAN)
        var art = await Json(await anna.GetAsync("/api/articles/lookup?code=7310000000011"));
        Assert.Equal("1001", art.GetProperty("articleNo").GetString());
        Assert.Equal(HttpStatusCode.OK, (await anna.GetAsync("/api/articles/lookup?code=1003")).StatusCode);

        var annaLine = Guid.NewGuid();
        var lines1 = new[] { new { id = annaLine, articleNo = "1001", quantity = 12m, location = "A1" } };
        await Json(await anna.PostAsJsonAsync($"/api/sessions/{sid}/lines", lines1));
        // Omsändning (offline-kö) ska inte dubblera
        await Json(await anna.PostAsJsonAsync($"/api/sessions/{sid}/lines", lines1));
        await Json(await bo.PostAsJsonAsync($"/api/sessions/{sid}/lines", new[] { new { id = Guid.NewGuid(), articleNo = "1001", quantity = 8m, location = "B4" } }));

        // Kan inte skicka öppen inventering
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/admin/sessions/{sid}/send", new { confirm = true })).StatusCode);

        await Json(await admin.PostAsync($"/api/admin/sessions/{sid}/lock", null));
        // Räknare kan inte lägga rader i låst inventering
        Assert.Equal(HttpStatusCode.Conflict, (await bo.PostAsJsonAsync($"/api/sessions/{sid}/lines", new[] { new { id = Guid.NewGuid(), articleNo = "1002", quantity = 1m } })).StatusCode);

        var summary = await Json(await admin.GetAsync($"/api/admin/sessions/{sid}/summary"));
        var a1001 = summary.GetProperty("articles").EnumerateArray().Single(a => a.GetProperty("articleNo").GetString() == "1001");
        Assert.Equal(20m, a1001.GetProperty("countedQuantity").GetDecimal());
        Assert.Equal(-20m, a1001.GetProperty("difference").GetDecimal());

        var sent = await Json(await admin.PostAsJsonAsync($"/api/admin/sessions/{sid}/send", new { confirm = true, zeroUncounted = false }));
        Assert.Equal(1, sent.GetProperty("ok").GetInt32());

        var visma = (FakeVismaGateway)_factory.Services.GetRequiredService<IVismaGateway>();
        Assert.Equal(20, visma.Current.Single(a => a.ArticleNo == "1001").StockQuantity);
        Assert.Equal(25, visma.Current.Single(a => a.ArticleNo == "1002").StockQuantity); // ej räknad → orörd
    }

    [Fact]
    public async Task Mutating_requests_without_csrf_header_are_rejected()
    {
        var c = _factory.CreateClient();
        var r = await c.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Account_is_locked_after_repeated_failures()
    {
        AdminPassword();
        var c = Client();
        for (var i = 0; i < 5; i++)
            await c.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = "fel" });
        var r = await c.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = AdminPassword() });
        Assert.Equal((HttpStatusCode)423, r.StatusCode);
    }

    [Fact]
    public async Task Admin_requires_totp_when_configured()
    {
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DataDirectory", _dataDir);
            b.UseSetting("Security:RequireTotpForAdmin", "true");
        });
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Requested-With", "inventering");
        f.Server.ToString();
        var pw = AdminPassword();
        var login = await Json(await c.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = pw }));
        Assert.True(login.GetProperty("mustSetupTotp").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/admin/sessions")).StatusCode);

        var setup = await Json(await c.PostAsync("/api/auth/totp/setup", null));
        var secret = setup.GetProperty("secret").GetString()!;
        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp();
        await Json(await c.PostAsJsonAsync("/api/auth/totp/confirm", new { code }));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/admin/sessions")).StatusCode);

        // Nästa inloggning kräver kod
        var c2 = f.CreateClient();
        c2.DefaultRequestHeaders.Add("X-Requested-With", "inventering");
        var again = await Json(await c2.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = pw }));
        Assert.True(again.GetProperty("needTotp").GetBoolean());
    }

    public void Dispose()
    {
        _factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
