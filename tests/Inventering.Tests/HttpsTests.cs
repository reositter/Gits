using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Inventering.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Inventering.Tests;

public class HttpsTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "inv-https-" + Guid.NewGuid().ToString("N"));

    private static X509Certificate2 Cert(string cn, bool ca = false, bool serverEku = true, int daysValid = 365, int daysOld = 1)
    {
        using var key = RSA.Create(2048);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (ca) req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        if (serverEku) req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        return req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-daysOld), DateTimeOffset.Now.AddDays(daysValid - daysOld));
    }

    [Fact]
    public void Root_ca_is_never_chosen_even_if_name_matches()
    {
        var root = Cert("visma01", ca: true, serverEku: false, daysValid: 3650);
        var server = Cert("visma01");
        var chosen = HttpsSetup.SelectServerCertificate([root, server], new HttpsOptions { Mode = "InternalCa", Subject = "visma01" }, DateTime.Now, out _);
        Assert.Equal(server.Thumbprint, chosen?.Thumbprint);
    }

    [Fact]
    public void Thumbprint_wins_and_falls_back_to_newest_with_exact_name()
    {
        var old = Cert("lager.local", daysValid: 100);
        var newer = Cert("lager.local", daysValid: 800);
        var other = Cert("lager.local.example");
        var o = new HttpsOptions { Mode = "Existing", Subject = "lager.local", Thumbprint = old.Thumbprint };
        Assert.Equal(old.Thumbprint, HttpsSetup.SelectServerCertificate([old, newer, other], o, DateTime.Now, out var missing)?.Thumbprint);
        Assert.False(missing);

        o.Thumbprint = "FINNS-INTE";
        Assert.Equal(newer.Thumbprint, HttpsSetup.SelectServerCertificate([old, newer, other], o, DateTime.Now, out missing)?.Thumbprint);
        Assert.True(missing);
    }

    [Fact]
    public void Expired_certificate_is_skipped()
    {
        var expired = Cert("lager.local", daysValid: 10, daysOld: 20);
        Assert.Null(HttpsSetup.SelectServerCertificate([expired], new HttpsOptions { Mode = "Existing", Subject = "lager.local" }, DateTime.Now, out _));
    }

    [Fact]
    public async Task Http_is_redirected_except_certificate_page()
    {
        Directory.CreateDirectory(_dataDir);
        var cer = Path.Combine(_dataDir, "rot.cer");
        File.WriteAllBytes(cer, Cert("Inventering rot", ca: true, serverEku: false).Export(X509ContentType.Cert));
        using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DataDirectory", _dataDir);
            b.UseSetting("Https:Mode", "InternalCa");
            b.UseSetting("Https:PublicHost", "lager.local");
            b.UseSetting("Https:Port", "8443");
            b.UseSetting("Https:RootCaFile", cer);
        });
        var c = f.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var root = await c.GetAsync("/admin.html");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, root.StatusCode);
        Assert.Equal("https://lager.local:8443/admin.html", root.Headers.Location?.ToString());

        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/certifikat.html")).StatusCode);
        var ca = await c.GetAsync("/ca.cer");
        Assert.Equal(HttpStatusCode.OK, ca.StatusCode);
        Assert.Equal("application/x-x509-ca-cert", ca.Content.Headers.ContentType?.MediaType);
        var info = await c.GetStringAsync("/api/cert-info");
        Assert.Contains("\"hasRootCa\":true", info);
        Assert.Contains("https://lager.local:8443", info);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch { }
    }
}
