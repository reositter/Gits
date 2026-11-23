namespace Inventering.Web.Services;

/// <summary>Sätts av installationsskriptet (appsettings.Production.json).</summary>
public class HttpsOptions
{
    /// <summary>None, SelfSigned, InternalCa eller Existing.</summary>
    public string Mode { get; set; } = "None";
    /// <summary>Namnet användarna når tjänsten på, t.ex. inventering.lager.local eller serverns namn.</summary>
    public string? PublicHost { get; set; }
    public int Port { get; set; } = 443;
    /// <summary>Skicka vidare http till https (utom certifikatsidan, som måste nås innan telefonen litar på certifikatet).</summary>
    public bool RedirectHttp { get; set; } = true;
    /// <summary>Rotcertifikatet (.cer) som telefonerna installerar vid Mode=InternalCa (eller servercertifikatet vid SelfSigned).</summary>
    public string? RootCaFile { get; set; }
    /// <summary>Servercertifikatets tumavtryck i LocalMachine\My (sätts av installationen).</summary>
    public string? Thumbprint { get; set; }
    /// <summary>Reserv om tumavtrycket inte finns (t.ex. förnyat certifikat): exakt CN, nyaste giltiga väljs.</summary>
    public string? Subject { get; set; }

    public bool Enabled => !Mode.Equals("None", StringComparison.OrdinalIgnoreCase);
    public string HttpsUrl(HttpRequest req) =>
        $"https://{(string.IsNullOrWhiteSpace(PublicHost) ? req.Host.Host : PublicHost)}{(Port == 443 ? "" : ":" + Port)}";
}

public static class HttpsSetup
{
    /// <summary>
    /// Väljer servercertifikat ur Windows certifikatarkiv (LocalMachine\My): först exakt tumavtryck,
    /// annars nyaste giltiga med exakt CN och serverautentisering. Rotcertifikat väljs aldrig.
    /// </summary>
    public static System.Security.Cryptography.X509Certificates.X509Certificate2? FindServerCertificate(HttpsOptions o, ILogger log)
    {
        using var store = new System.Security.Cryptography.X509Certificates.X509Store(
            System.Security.Cryptography.X509Certificates.StoreName.My, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
        store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
        var cert = SelectServerCertificate(store.Certificates.Cast<System.Security.Cryptography.X509Certificates.X509Certificate2>(), o, DateTime.Now, out var thumbMissing);
        if (thumbMissing) log.LogWarning("Certifikat med tumavtryck {Thumbprint} hittades inte eller har gått ut – söker på namn.", o.Thumbprint);
        if (cert is null) log.LogError("Hittade inget giltigt servercertifikat (tumavtryck {Thumbprint}, namn {Subject}). Kör installera.ps1 -BaraCertifikat.", o.Thumbprint, o.Subject);
        return cert;
    }

    public static System.Security.Cryptography.X509Certificates.X509Certificate2? SelectServerCertificate(
        IEnumerable<System.Security.Cryptography.X509Certificates.X509Certificate2> certs, HttpsOptions o, DateTime now, out bool thumbprintMissing)
    {
        thumbprintMissing = false;
        var usable = certs.Where(c => c.HasPrivateKey && c.NotBefore <= now && c.NotAfter > now && IsServerCert(c)).ToList();

        if (!string.IsNullOrWhiteSpace(o.Thumbprint))
        {
            var byThumb = usable.FirstOrDefault(c => string.Equals(c.Thumbprint, o.Thumbprint.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (byThumb != null) return byThumb;
            thumbprintMissing = true;
        }
        if (!string.IsNullOrWhiteSpace(o.Subject))
        {
            var bySubject = usable.Where(c => string.Equals(c.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false), o.Subject, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.NotAfter).FirstOrDefault();
            if (bySubject != null) return bySubject;
        }
        return null;
    }

    private static bool IsServerCert(System.Security.Cryptography.X509Certificates.X509Certificate2 c)
    {
        var bc = c.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension>().FirstOrDefault();
        if (bc is { CertificateAuthority: true }) return false;
        var eku = c.Extensions.OfType<System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return eku is null || eku.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.1");
    }

    // Nås över http även när https är påslaget – telefonen måste kunna hämta certifikatet först.
    private static readonly string[] OpenOverHttp = ["/certifikat", "/ca.cer", "/api/cert-info", "/css/", "/js/common.js", "/js/icons.js", "/js/certifikat.js", "/lib/qrcode.js", "/icon.svg", "/api/health", "/favicon"];

    public static void UseHttpsRedirectForApp(this WebApplication app)
    {
        var o = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HttpsOptions>>().Value;
        if (!o.Enabled || !o.RedirectHttp) return;
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? "/";
            if (ctx.Request.IsHttps || OpenOverHttp.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                await next();
                return;
            }
            ctx.Response.Redirect(o.HttpsUrl(ctx.Request) + path + ctx.Request.QueryString, permanent: false, preserveMethod: true);
        });
    }

    public static void MapCertificateEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cert-info", (Microsoft.Extensions.Options.IOptions<HttpsOptions> opt, HttpRequest req) =>
        {
            var o = opt.Value;
            return Results.Ok(new
            {
                mode = o.Mode, enabled = o.Enabled, httpsUrl = o.Enabled ? o.HttpsUrl(req) : null,
                hasRootCa = o.RootCaFile is { Length: > 0 } f && File.Exists(f), isHttps = req.IsHttps,
            });
        });

        app.MapGet("/ca.cer", (Microsoft.Extensions.Options.IOptions<HttpsOptions> opt) =>
        {
            var f = opt.Value.RootCaFile;
            if (string.IsNullOrEmpty(f) || !File.Exists(f)) return Results.NotFound();
            // application/x-x509-ca-cert gör att iPhone och Android erbjuder installation direkt.
            return Results.File(File.ReadAllBytes(f), "application/x-x509-ca-cert", "inventering-rotcertifikat.cer");
        });
    }
}
