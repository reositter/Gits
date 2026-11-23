using Inventering.Core.Visma;

namespace Inventering.Tests;

/// <summary>
/// Testar protokollet mot ADK-bryggan med ett skalskript som låtsas vara bryggan
/// (riktig ADK kan bara köras på Windows med Visma installerat).
/// </summary>
public class AdkBridgeGatewayTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bridge").FullName;

    private string FakeBridge(string script)
    {
        var path = Path.Combine(_dir, "bridge.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + script.Replace("\r", ""));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task Reads_articles_and_sends_credentials_via_stdin()
    {
        if (OperatingSystem.IsWindows()) return;
        var input = Path.Combine(_dir, "in.json");
        var bridge = FakeBridge($$"""
            cat > {{input}}
            echo '[{"articleNo":"1001","name":"Skruv","ean":"7310000000011","unit":"st","stockQuantity":40.5,"isStockItem":true,"inactive":false},
                   {"articleNo":"1002","name":"Gammal","ean":null,"unit":"st","stockQuantity":0,"isStockItem":true,"inactive":true}]'
            """);
        var gw = new AdkBridgeGateway(new AdkOptions { BridgePath = bridge }, @"C:\Ftg", "u", "hemligt");

        var articles = await gw.GetArticlesAsync();

        Assert.Equal(2, articles.Count);
        Assert.Equal(40.5m, articles[0].StockQuantity);
        Assert.True(articles[0].IsStockItem);
        Assert.False(articles[1].IsStockItem); // inaktiv → ej inventeringsbar
        var sent = File.ReadAllText(input);
        Assert.Contains("\"command\":\"articles\"", sent);
        Assert.Contains("\"password\":\"hemligt\"", sent);
    }

    [Fact]
    public async Task Bridge_error_is_reported()
    {
        if (OperatingSystem.IsWindows()) return;
        var bridge = FakeBridge("""
            cat > /dev/null
            echo '{"error":"ADK-fel 1234: Företaget är låst"}'
            exit 1
            """);
        var gw = new AdkBridgeGateway(new AdkOptions { BridgePath = bridge }, @"C:\Ftg");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => gw.GetStatusAsync());
        Assert.Contains("Företaget är låst", ex.Message);
    }

    [Fact]
    public async Task Writes_inventory_rows()
    {
        if (OperatingSystem.IsWindows()) return;
        var input = Path.Combine(_dir, "in.json");
        var bridge = FakeBridge($$"""
            cat > {{input}}
            echo '[{"articleNo":"1001","success":true,"stockBefore":40,"message":null},{"articleNo":"9","success":false,"stockBefore":null,"message":"Finns inte i underlaget"}]'
            """);
        var gw = new AdkBridgeGateway(new AdkOptions { BridgePath = bridge }, @"C:\Ftg");
        var res = await gw.WriteInventoryAsync([new("1001", 20), new("9", 1)], "test");
        Assert.True(res[0].Success);
        Assert.Equal(40m, res[0].StockBefore);
        Assert.False(res[1].Success);
        Assert.Contains("\"command\":\"inventory-write\"", File.ReadAllText(input));
        Assert.Contains("\"quantity\":20", File.ReadAllText(input));
    }

    [Fact]
    public async Task Discovers_companies()
    {
        if (OperatingSystem.IsWindows()) return;
        var bridge = FakeBridge("""
            cat > /dev/null
            echo '[{"name":"Bygg AB","directory":"FTG1","directoryFullPath":"C:\\\\F\\\\FTG1","databaseName":"SPCS_Adm_FTG1"}]'
            """);
        var f = new AdkGatewayFactory(new AdkBridgeRunner(new AdkOptions { BridgePath = bridge }), _ => null);
        var list = await f.DiscoverCompaniesAsync();
        Assert.Equal("Bygg AB", list.Single().Name);
        Assert.Equal("FTG1", list.Single().Directory);
    }

    [Fact]
    public async Task Missing_bridge_gives_clear_error()
    {
        var gw = new AdkBridgeGateway(new AdkOptions { BridgePath = Path.Combine(_dir, "finns-inte.exe") }, @"C:\Ftg");
        await Assert.ThrowsAsync<FileNotFoundException>(() => gw.GetArticlesAsync());
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}
