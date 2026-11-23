using System.Security.Cryptography.X509Certificates;
using Inventering.Web.Data;
using Inventering.Web.Services;
using Microsoft.Extensions.Options;

namespace Inventering.Web.Endpoints;

/// <summary>Admin → Server: https-läge, certifikatets status och byte av certifikat.</summary>
public static class ServerEndpoints
{
    public static void MapServerAdmin(this WebApplication app, string dataDir)
    {
        var g = app.MapGroup("/api/admin/server").RequireAuthorization(Policies.Admin);

        g.MapGet("", (IOptionsMonitor<HttpsOptions> opt, ServerCertificateProvider certs, IConfiguration config, HttpRequest req) =>
        {
            var o = opt.CurrentValue;
            var c = certs.Current;
            var httpsEndpoint = config["Kestrel:Endpoints:Https:Url"];
            return Results.Ok(new
            {
                mode = o.Mode, enabled = o.Enabled, publicHost = o.PublicHost, port = o.Port,
                httpsUrl = o.Enabled ? o.HttpsUrl(req) : null,
                certificatePage = $"http://{(string.IsNullOrWhiteSpace(o.PublicHost) ? req.Host.Host : o.PublicHost)}{HttpPortSuffix(config)}/certifikat.html",
                windows = OperatingSystem.IsWindows(),
                canUpload = OperatingSystem.IsWindows() && !string.IsNullOrEmpty(httpsEndpoint),
                rootCa = o.RootCaFile is { Length: > 0 } f && File.Exists(f),
                certificate = c is null ? null : new
                {
                    subject = c.GetNameInfo(X509NameType.SimpleName, false), issuer = c.GetNameInfo(X509NameType.SimpleName, true),
                    c.NotBefore, c.NotAfter, daysLeft = (int)Math.Floor((c.NotAfter - DateTime.Now).TotalDays), c.Thumbprint,
                    names = c.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(x => x.EnumerateDnsNames()).ToList(),
                },
            });
        });

        g.MapPost("/certificate", async (HttpContext http, ServerCertificateProvider certs, AppDb db, IConfiguration config) =>
        {
            if (!OperatingSystem.IsWindows()) return Results.BadRequest(new { error = "Certifikat kan bara bytas när tjänsten körs på Windows." });
            if (string.IsNullOrEmpty(config["Kestrel:Endpoints:Https:Url"]))
                return Results.BadRequest(new { error = "Tjänsten är installerad utan https. Kör installera.ps1 -BaraCertifikat och välj ett certifikatalternativ." });
            if (!http.Request.HasFormContentType) return Results.BadRequest(new { error = "Ingen fil." });
            var form = await http.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file is null || file.Length == 0 || file.Length > 1_000_000) return Results.BadRequest(new { error = "Välj en .pfx-fil (max 1 MB)." });
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            try
            {
                var cert = certs.Import(ms.ToArray(), form["password"].ToString(), dataDir);
                db.Log(http, "certificate.uploaded", $"{cert.Subject} giltigt till {cert.NotAfter:yyyy-MM-dd} ({cert.Thumbprint})");
                await db.SaveChangesAsync();
                return Results.Ok(new { subject = cert.Subject, notAfter = cert.NotAfter });
            }
            catch (InvalidDataException ex) { return Results.BadRequest(new { error = ex.Message }); }
        }).DisableAntiforgery();
    }

    private static string HttpPortSuffix(IConfiguration config)
    {
        var url = config["Kestrel:Endpoints:Http:Url"];
        if (url is null) return "";
        var port = new Uri(url.Replace("0.0.0.0", "localhost").Replace("*", "localhost").Replace("+", "localhost")).Port;
        return port == 80 ? "" : ":" + port;
    }
}
