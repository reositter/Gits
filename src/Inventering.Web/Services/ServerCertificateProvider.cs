using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Inventering.Web.Services;

/// <summary>
/// Håller tjänstens servercertifikat. Kestrel frågar efter certifikatet vid varje anslutning,
/// så ett nytt certifikat (uppladdat i admin eller förnyat av t.ex. win-acme) börjar gälla utan omstart.
/// </summary>
public sealed class ServerCertificateProvider : IDisposable
{
    public const string OverrideFile = "https-installningar.json";
    private IOptionsMonitor<HttpsOptions>? _options;
    private ILogger? _log;
    private Timer? _timer;
    private volatile X509Certificate2? _current;

    public X509Certificate2? Current => _current;

    public void Start(IServiceProvider services)
    {
        _options = services.GetRequiredService<IOptionsMonitor<HttpsOptions>>();
        _log = services.GetRequiredService<ILoggerFactory>().CreateLogger("Https");
        Reload();
        _options.OnChange(_ => Reload());
        // Plocka upp förnyade certifikat med samma namn (win-acme m.fl.) utan omstart.
        _timer = new Timer(_ => Reload(), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
    }

    public void Reload()
    {
        if (_options is null || !OperatingSystem.IsWindows()) return;
        var o = _options.CurrentValue;
        if (!o.Enabled) { _current = null; return; }
        var cert = HttpsSetup.FindServerCertificate(o, _log!);
        if (cert != null && cert.Thumbprint != _current?.Thumbprint)
            _log!.LogInformation("Servercertifikat: {Subject}, giltigt till {NotAfter:yyyy-MM-dd} ({Thumbprint})", cert.Subject, cert.NotAfter, cert.Thumbprint);
        _current = cert ?? _current;
    }

    /// <summary>
    /// Importerar ett .pfx i LocalMachine\My och sparar valet i datakatalogen (läses in som konfiguration).
    /// </summary>
    public X509Certificate2 Import(byte[] pfx, string? password, string dataDir)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Certifikat kan bara bytas när tjänsten körs på Windows.");
        X509Certificate2 cert;
        try
        {
            cert = new X509Certificate2(pfx, password, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new InvalidDataException("Filen kunde inte öppnas. Kontrollera att det är en .pfx-fil och att lösenordet är rätt.");
        }
        if (!cert.HasPrivateKey) throw new InvalidDataException("Certifikatet saknar privat nyckel. Exportera det som .pfx med privat nyckel.");
        if (HttpsSetup.SelectServerCertificate([cert], new HttpsOptions { Mode = "Existing", Thumbprint = cert.Thumbprint }, DateTime.Now, out _) is null)
            throw new InvalidDataException($"Certifikatet går inte att använda för en webbserver (giltigt {cert.NotBefore:yyyy-MM-dd}–{cert.NotAfter:yyyy-MM-dd}, eller fel användningsområde).");

        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadWrite);
            cert.FriendlyName = "Inventering server (uppladdat)";
            store.Add(cert);
        }

        var cn = cert.GetNameInfo(X509NameType.SimpleName, false);
        var o = _options!.CurrentValue;
        var file = Path.Combine(dataDir, OverrideFile);
        var json = new JsonObject
        {
            ["Https"] = new JsonObject
            {
                ["Mode"] = "Existing", ["Thumbprint"] = cert.Thumbprint, ["Subject"] = cn,
                ["PublicHost"] = string.IsNullOrWhiteSpace(o.PublicHost) ? cn : o.PublicHost,
                ["Port"] = o.Port, ["RedirectHttp"] = true, ["RootCaFile"] = o.Mode == "InternalCa" ? o.RootCaFile : null,
            },
        };
        File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _current = cert; // gäller direkt, även innan konfigurationen laddats om
        _log!.LogInformation("Nytt servercertifikat uppladdat: {Subject} ({Thumbprint})", cert.Subject, cert.Thumbprint);
        return cert;
    }

    public void Dispose() => _timer?.Dispose();
}
