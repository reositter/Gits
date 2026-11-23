using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Inventering.Tests;

public class TotpPerUserTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "inv-2fa-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _f;

    public TotpPerUserTests()
    {
        _f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DataDirectory", _dataDir);
            b.UseSetting("Security:RequireTotpForAdmin", "false");
        });
    }

    private HttpClient Client()
    {
        var c = _f.CreateClient();
        c.DefaultRequestHeaders.Add("X-Requested-With", "inventering");
        return c;
    }

    private static string Code(string secret, long offsetSteps = 0) =>
        new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow.AddSeconds(30 * offsetSteps));

    private async Task<HttpClient> Admin()
    {
        _f.Server.ToString();
        var pw = File.ReadAllText(Path.Combine(_dataDir, "forsta-admin-losenord.txt")).Split('\n').First(l => l.StartsWith("Lösenord:"))[9..].Trim();
        var c = Client();
        (await c.PostAsJsonAsync("/api/auth/login", new { userName = "admin", password = pw })).EnsureSuccessStatusCode();
        return c;
    }

    [Fact]
    public async Task Required_per_user_forces_setup_before_counting()
    {
        var admin = await Admin();
        (await admin.PostAsJsonAsync("/api/admin/users", new { userName = "kalle", role = "Counter", password = "Lager2026!!", requireTotp = true })).EnsureSuccessStatusCode();

        var c = Client();
        var login = await (await c.PostAsJsonAsync("/api/auth/login", new { userName = "kalle", password = "Lager2026!!" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(login.GetProperty("mustSetupTotp").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/sessions/open")).StatusCode);

        var setup = await (await c.PostAsync("/api/auth/totp/setup", null)).Content.ReadFromJsonAsync<JsonElement>();
        var secret = setup.GetProperty("secret").GetString()!;
        (await c.PostAsJsonAsync("/api/auth/totp/confirm", new { code = Code(secret) })).EnsureSuccessStatusCode();
        // Efter aktivering: behörighet (409 = välj bolag, inte 403)
        Assert.NotEqual(HttpStatusCode.Forbidden, (await c.GetAsync("/api/sessions/open")).StatusCode);

        // Kan inte stänga av när det krävs
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync("/api/auth/totp/disable", new { password = "Lager2026!!", code = Code(secret, 1) })).StatusCode);
    }

    [Fact]
    public async Task Optional_totp_can_be_enabled_and_disabled_by_user()
    {
        var admin = await Admin();
        (await admin.PostAsJsonAsync("/api/admin/users", new { userName = "lisa", role = "Counter", password = "Lager2026!!" })).EnsureSuccessStatusCode();

        var c = Client();
        var login = await (await c.PostAsJsonAsync("/api/auth/login", new { userName = "lisa", password = "Lager2026!!" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(login.GetProperty("mustSetupTotp").GetBoolean());

        var secret = (await (await c.PostAsync("/api/auth/totp/setup", null)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("secret").GetString()!;
        (await c.PostAsJsonAsync("/api/auth/totp/confirm", new { code = Code(secret) })).EnsureSuccessStatusCode();

        // Ny inloggning kräver kod
        var c2 = Client();
        var again = await (await c2.PostAsJsonAsync("/api/auth/login", new { userName = "lisa", password = "Lager2026!!" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(again.GetProperty("needTotp").GetBoolean());

        // Stäng av med lösenord + ny kod (samma kod får inte återanvändas)
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/totp/disable", new { password = "fel", code = Code(secret, 1) })).StatusCode);
        (await c.PostAsJsonAsync("/api/auth/totp/disable", new { password = "Lager2026!!", code = Code(secret, 1) })).EnsureSuccessStatusCode();
        var c3 = Client();
        var third = await (await c3.PostAsJsonAsync("/api/auth/login", new { userName = "lisa", password = "Lager2026!!" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(third.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Admin_turning_on_requirement_logs_out_existing_sessions()
    {
        var admin = await Admin();
        var created = await (await admin.PostAsJsonAsync("/api/admin/users", new { userName = "per", role = "Counter", password = "Lager2026!!" })).Content.ReadFromJsonAsync<JsonElement>();
        var c = Client();
        (await c.PostAsJsonAsync("/api/auth/login", new { userName = "per", password = "Lager2026!!" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/auth/me")).StatusCode);

        (await admin.PutAsJsonAsync($"/api/admin/users/{created.GetProperty("id").GetInt32()}", new { userName = "", role = "", requireTotp = true })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);
    }

    public void Dispose()
    {
        _f.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
