using System.Globalization;
using System.Text.Json;
using AuctionLoadTest;

/*
 * DealGo bidding load test and live-auction monitor. See README.md.
 *
 *   run      simulated participants join and bid in fresh auctions on a LOCAL server
 *   monitor  watch one real auction without bidding: live updates, closing, server health
 *   proxy    a TCP relay that delays traffic, to put a "remote" database behind a local one
 */
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(File.Exists(Path.Combine(AppContext.BaseDirectory, "README.md"))
        ? "See tools/AuctionLoadTest/README.md"
        : "Commands: run | monitor | proxy. See tools/AuctionLoadTest/README.md");
    return 0;
}

var command = args[0];
var options = new Args(args.Skip(1));

try
{
    return command switch
    {
        "run" => await RunAsync(options),
        "monitor" => await AuctionMonitor.RunAsync(options),
        "proxy" => await LatencyProxy.RunAsync(options),
        "token" => await TokenAsync(options),
        "compare" => await Compare.RunAsync(options),
        "play" => await Play.RunAsync(options),
        _ => throw new ArgumentException($"Unknown command '{command}'. Use run, monitor or proxy.")
    };
}
catch (ArgumentException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

// Local only: a super administrator token signed with the local server's key, for `monitor --token`.
static async Task<int> TokenAsync(Args a)
{
    var local = new LocalSetup(a.Require("db"), a.Require("jwt-secret"),
        a.Get("jwt-issuer", "auction-platform"), a.Get("jwt-audience", "auction-platform-clients"));
    Console.WriteLine((await local.EnsureAdminAsync()).Token);
    return 0;
}

static async Task<int> RunAsync(Args a)
{
    var baseUrl = new Uri(a.Get("base-url", "http://127.0.0.1:5199/"));
    if (!baseUrl.IsLoopback && !a.Flag("allow-remote-server"))
    {
        // Creating hundreds of accounts and hammering the bid endpoint is for a server on this
        // machine. Against anything else it has to be asked for in so many words.
        throw new ArgumentException(
            $"{baseUrl} is not on this machine. 'run' creates test users and bids at full load; " +
            "point it at a local server, or add --allow-remote-server if that is really intended.");
    }

    var settings = new RunSettings(
        baseUrl,
        a.Int("duration", 90),
        a.Double("rate", 0.5),
        a.Double("burst-rate", 2.0),
        a.Int("burst-seconds", 15),
        a.Double("min-gap-ms", 250),
        a.Get("mode", "paid"),
        a.Get("close", "time"),
        a.Int("target-bids", 3000),
        (decimal)a.Double("entry-fee", 20),
        a.Int("connect-concurrency", 64),
        a.Int("seed", 42),
        a.Int("neighbors", 0));

    if (settings.Mode is not ("paid" or "demo")) throw new ArgumentException("--mode is paid or demo.");
    if (settings.Close is not ("time" or "target")) throw new ArgumentException("--close is time or target.");

    var sizes = a.Get("participants", "13,50,100,400").Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => int.Parse(s.Trim(), CultureInfo.InvariantCulture)).ToList();
    var outDir = a.Get("out", Path.Combine("artifacts", "loadtest", DateTime.Now.ToString("yyyyMMdd-HHmmss")));
    Directory.CreateDirectory(outDir);

    var local = new LocalSetup(
        a.Require("db"), a.Require("jwt-secret"),
        a.Get("jwt-issuer", "auction-platform"), a.Get("jwt-audience", "auction-platform-clients"));

    using var api = new Api(baseUrl, TimeSpan.FromSeconds(a.Int("timeout-seconds", 30)));
    var admin = await local.EnsureAdminAsync();
    var pool = await local.EnsureBiddersAsync(sizes.Max() + settings.Neighbors);
    Console.WriteLine($"{pool.Count} test bidders ready; writing to {Path.GetFullPath(outDir)}");

    var runner = new ScenarioRunner(settings, api, local, admin, pool);
    var results = new List<ScenarioResult>();
    var environment = a.Get("environment", "local server");

    foreach (var n in sizes)
    {
        var result = await runner.RunAsync(n);
        results.Add(result);
        await File.WriteAllTextAsync(Path.Combine(outDir, $"attempts-{n}.csv"), runner.AttemptsCsv);

        // Written after every scenario, so a run stopped halfway still leaves its numbers.
        await File.WriteAllTextAsync(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(results, Json.Pretty));
        await File.WriteAllTextAsync(Path.Combine(outDir, "report.md"), Report.Markdown(settings, results, environment));
        await Task.Delay(TimeSpan.FromSeconds(a.Int("pause", 5)));
    }

    Console.WriteLine();
    Console.WriteLine($"Report: {Path.GetFullPath(Path.Combine(outDir, "report.md"))}");
    return results.All(r => r.AllChecksPassed && r.Failed == 0) ? 0 : 1;
}
