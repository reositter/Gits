using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inventering.Core.Domain;

namespace Inventering.Core.Visma;

public class AdkOptions
{
    /// <summary>Gemensamma filer (tom = ADK:s standard).</summary>
    public string CommonPath { get; set; } = "";
    /// <summary>Sökväg till Inventering.AdkBridge.exe. Relativ sökväg utgår från programmets katalog.</summary>
    public string BridgePath { get; set; } = @"AdkBridge\Inventering.AdkBridge.exe";
    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// Startar ADK-bryggan (.NET Framework 4.8 x86) och utbyter JSON via stdin/stdout.
/// ADK-wrappern kan inte laddas i .NET 8, och en egen process per anrop isolerar också
/// ADK:s minnesläckor och krascher. Ett anrop i taget, oavsett bolag.
/// </summary>
public sealed class AdkBridgeRunner(AdkOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AdkOptions Options => options;

    public async Task<T> RunAsync<T>(JsonObject request, CancellationToken ct)
    {
        var exe = Path.IsPathRooted(options.BridgePath) ? options.BridgePath : Path.Combine(AppContext.BaseDirectory, options.BridgePath);
        if (!File.Exists(exe)) throw new FileNotFoundException($"ADK-bryggan hittades inte: {exe}");
        request["commonPath"] ??= options.CommonPath;

        await _lock.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Kunde inte starta ADK-bryggan.");
            // Lösenord skickas via stdin, aldrig som argument (syns annars i processlistan).
            await using (var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
                await stdin.WriteAsync(request.ToJsonString());

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
            var stdoutTask = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(timeout.Token);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw new TimeoutException($"ADK-bryggan svarade inte inom {options.TimeoutSeconds} s.");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (p.ExitCode != 0)
            {
                string? err = null;
                try { err = JsonDocument.Parse(stdout).RootElement.GetProperty("error").GetString(); } catch { }
                throw new InvalidOperationException(err ?? $"ADK-bryggan avslutades med kod {p.ExitCode}. {stderr}".Trim());
            }
            return JsonSerializer.Deserialize<T>(stdout, Json) ?? throw new InvalidOperationException("Tomt svar från ADK-bryggan.");
        }
        finally { _lock.Release(); }
    }
}

/// <summary>Visma Administration via ADK för ett bolag.</summary>
public sealed class AdkBridgeGateway(AdkBridgeRunner runner, string companyPath, string? user, string? password) : IVismaGateway
{
    public AdkBridgeGateway(AdkOptions options, string companyPath, string? user = null, string? password = null)
        : this(new AdkBridgeRunner(options), companyPath, user, password) { }

    public string Name => "Visma Administration (ADK)";

    private JsonObject Request(string command) => new()
    {
        ["command"] = command, ["companyPath"] = companyPath, ["user"] = user ?? "", ["password"] = password ?? "",
    };

    public async Task<VismaStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var s = await runner.RunAsync<StatusDto>(Request("status"), ct);
        return new VismaStatus(Name, $"{s.Product}, ADK {s.AdkVersion}, {s.CompanyName}", true,
            s.HasInventory ? s.InventoryListRows : 0, s.InventoryListDate);
    }

    public async Task<IReadOnlyList<Article>> GetArticlesAsync(CancellationToken ct = default)
    {
        var list = await runner.RunAsync<List<ArticleDto>>(Request("articles"), ct);
        return list.Where(a => !string.IsNullOrWhiteSpace(a.ArticleNo)).Select(a => new Article
        {
            ArticleNo = a.ArticleNo, Name = a.Name ?? "", Ean = a.Ean, Unit = a.Unit,
            StockQuantity = (decimal)a.StockQuantity,
            // Inaktiva artiklar behandlas som ej inventeringsbara.
            IsStockItem = a.IsStockItem && !a.Inactive,
        }).ToList();
    }

    public async Task<IReadOnlyList<InventoryWriteResult>> WriteInventoryAsync(IReadOnlyList<InventoryWrite> rows, string reference, CancellationToken ct = default)
    {
        var req = Request("inventory-write");
        req["date"] = DateTime.Today;
        req["rows"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject { ["articleNo"] = r.ArticleNo, ["quantity"] = (double)r.CountedQuantity }).ToArray());
        var res = await runner.RunAsync<List<WriteResultDto>>(req, ct);
        return res.Select(r => new InventoryWriteResult(r.ArticleNo, r.Success, r.StockBefore is { } d ? (decimal)d : null, r.Message)).ToList();
    }

    private sealed record ArticleDto(string ArticleNo, string? Name, string? Ean, string? Unit, double StockQuantity, bool IsStockItem, bool Inactive);
    private sealed record WriteResultDto(string ArticleNo, bool Success, double? StockBefore, string? Message);
    private sealed record StatusDto(string? CompanyName, string? AdkVersion, string? Product, bool HasInventory, int InventoryListRows, DateTime? InventoryListDate);
}

public sealed class AdkGatewayFactory(AdkBridgeRunner runner, Func<Company, string?> passwordResolver) : IVismaGatewayFactory
{
    public string Mode => "Adk";

    public IVismaGateway For(Company company) =>
        new AdkBridgeGateway(runner, company.CompanyPath, company.VismaUser, passwordResolver(company));

    public async Task<IReadOnlyList<VismaCompanyInfo>> DiscoverCompaniesAsync(CancellationToken ct = default)
    {
        var list = await runner.RunAsync<List<CompanyDto>>(new JsonObject { ["command"] = "companies" }, ct);
        return list.Select(c => new VismaCompanyInfo(c.Name ?? c.Directory ?? "", c.Directory ?? "", c.DirectoryFullPath ?? "", c.DatabaseName)).ToList();
    }

    private sealed record CompanyDto(string? Name, string? Directory, string? DirectoryFullPath, string? DatabaseName);
}
