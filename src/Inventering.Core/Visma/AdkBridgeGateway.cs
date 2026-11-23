using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Inventering.Core.Domain;

namespace Inventering.Core.Visma;

public class AdkOptions
{
    /// <summary>Företagsmapp i Visma Administration, t.ex. C:\ProgramData\SPCS\SPCS Administration\Företag\FTG3.</summary>
    public string CompanyPath { get; set; } = "";
    /// <summary>Gemensamma filer (tom = ADK:s standard).</summary>
    public string CommonPath { get; set; } = "";
    /// <summary>Visma-användare om inloggning är aktiverad i Visma (annars tomt).</summary>
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    /// <summary>Sökväg till Inventering.AdkBridge.exe. Relativ sökväg utgår från programmets katalog.</summary>
    public string BridgePath { get; set; } = @"AdkBridge\Inventering.AdkBridge.exe";
    public int TimeoutSeconds { get; set; } = 600;
}

/// <summary>
/// Visma Administration via ADK. ADK-wrappern (AdkNet4Wrapper/AvSynkLib) är .NET Framework x86
/// och kan inte laddas i .NET 8, så varje anrop körs i en kort 32-bitars process
/// (Inventering.AdkBridge). Det isolerar också ADK:s minnesläckor och krascher från webbtjänsten.
/// Anropen körs ett i taget.
/// </summary>
public sealed class AdkBridgeGateway(AdkOptions options) : IVismaGateway
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string Name => "Visma Administration (ADK)";

    public async Task<VismaStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var s = await RunAsync<StatusDto>(new { command = "status" }, ct);
        return new VismaStatus(Name, $"{s.Product}, ADK {s.AdkVersion}, {s.CompanyName}", true,
            s.HasInventory ? s.InventoryListRows : 0, s.InventoryListDate);
    }

    public async Task<IReadOnlyList<Article>> GetArticlesAsync(CancellationToken ct = default)
    {
        var list = await RunAsync<List<ArticleDto>>(new { command = "articles" }, ct);
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
        var res = await RunAsync<List<WriteResultDto>>(new
        {
            command = "inventory-write",
            date = DateTime.Today,
            rows = rows.Select(r => new { articleNo = r.ArticleNo, quantity = (double)r.CountedQuantity }),
        }, ct);
        return res.Select(r => new InventoryWriteResult(r.ArticleNo, r.Success, r.StockBefore is { } d ? (decimal)d : null, r.Message)).ToList();
    }

    private async Task<T> RunAsync<T>(object payload, CancellationToken ct)
    {
        var exe = Path.IsPathRooted(options.BridgePath) ? options.BridgePath : Path.Combine(AppContext.BaseDirectory, options.BridgePath);
        if (!File.Exists(exe)) throw new FileNotFoundException($"ADK-bryggan hittades inte: {exe}");

        // Inloggningsuppgifter läggs till här och skickas via stdin, aldrig som argument.
        var doc = JsonSerializer.SerializeToNode(payload, Json)!.AsObject();
        doc["companyPath"] = options.CompanyPath;
        doc["commonPath"] = options.CommonPath;
        doc["user"] = options.User;
        doc["password"] = options.Password;

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
            await using (var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
                await stdin.WriteAsync(doc.ToJsonString());

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

    private sealed record ArticleDto(string ArticleNo, string? Name, string? Ean, string? Unit, double StockQuantity, bool IsStockItem, bool Inactive);
    private sealed record WriteResultDto(string ArticleNo, bool Success, double? StockBefore, string? Message);
    private sealed record StatusDto(string? CompanyName, string? AdkVersion, string? Product, bool HasInventory, int InventoryListRows, DateTime? InventoryListDate);
}
