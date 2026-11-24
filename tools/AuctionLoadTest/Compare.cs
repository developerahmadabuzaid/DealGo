using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AuctionLoadTest;

/// <summary>
/// Puts two runs side by side: a baseline and a candidate, each a folder with results.json.
///
///   compare --baseline artifacts/loadtest/A --candidate artifacts/loadtest/B [--title "..."] [--out file.md]
///
/// Figures a run does not have (an older build without the queue, say) show as "—".
/// </summary>
static class Compare
{
    private sealed record Row(string Label, Func<JsonElement, double?> Pick, string Unit, bool LowerIsBetter = true);

    private static readonly Row[] Rows =
    {
        new("Bid latency p50", r => Num(r, "bidLatency", "p50"), "ms"),
        new("Bid latency p95", r => Num(r, "bidLatency", "p95"), "ms"),
        new("Bid latency p99", r => Num(r, "bidLatency", "p99"), "ms"),
        new("Bid latency, closing burst p99", r => Num(r, "burstBidLatency", "p99"), "ms"),
        new("Bids accepted per second", r => Num(r, "acceptedPerSecond"), "/s", LowerIsBetter: false),
        new("Failed bids", r => Num(r, "failed"), ""),
        new("Bids turned away as busy", r => Num(r, "busy") ?? 0, ""),
        new("Joins turned away as busy", r => Num(r, "joinsBusy") ?? 0, ""),
        new("DB connections in use (busiest reading)", r => Num(r, "server", "poolUsedMax"), ""),
        new("Requests waiting for a DB connection (peak)", r => Num(r, "server", "poolWaitingMax"), ""),
        new("Getting a DB connection p99", r => Num(r, "server", "connectionOpen", "p99"), "ms"),
        new("Bids waiting in the queue (peak)", r => Num(r, "server", "bidQueueLengthMax"), ""),
        new("Queue wait p99", r => Count(r, "bidQueueWait") ? Num(r, "server", "bidQueueWait", "p99") : null, "ms"),
        new("Lock wait p99", r => Num(r, "server", "bidLockWait", "p99"), "ms"),
        new("Lock held, average", r => Num(r, "server", "bidLockHeld", "meanMs"), "ms"),
        new("Leaderboard lag p95", r => Num(r, "leaderboardLag", "p95"), "ms"),
        new("Leaderboard lag p99", r => Num(r, "leaderboardLag", "p99"), "ms"),
        new("Price reaches screens p99", r => Num(r, "propagation", "p99"), "ms"),
        new("Times a screen would show an older price", r => Num(r, "priceRegressions"), ""),
        new("Winners shown after the close", r => Num(r, "deadlineToWinnersEvent", "p50"), "ms"),
        new("Auction fully closed after the close", r => Num(r, "deadlineToFinalStatusMs"), "ms"),
        new("Joins all at once, slowest", r => Num(r, "join", "max"), "ms"),
        new("Server CPU peak", r => Num(r, "server", "cpuMaxPercent"), "%"),
        new("Server memory peak", r => Num(r, "server", "workingSetMaxMb"), "MB"),
    };

    public static async Task<int> RunAsync(Args a)
    {
        var baseline = Load(a.Require("baseline"));
        var candidate = Load(a.Require("candidate"));
        var md = new StringBuilder();

        md.AppendLine($"## {a.Get("title", "Baseline vs candidate")}");
        md.AppendLine();
        md.AppendLine($"Baseline: `{a.Require("baseline")}` · Candidate: `{a.Require("candidate")}`");
        md.AppendLine();

        foreach (var (n, before) in baseline.OrderBy(b => b.Key))
        {
            if (!candidate.TryGetValue(n, out var after)) continue;

            md.AppendLine($"### {n} participants");
            md.AppendLine();
            md.AppendLine("| | Baseline | Now | Change |");
            md.AppendLine("|---|---|---|---|");
            foreach (var row in Rows)
            {
                var b = row.Pick(before);
                var c = row.Pick(after);
                if (b is null && c is null) continue;
                md.AppendLine($"| {row.Label} | {Format(b, row.Unit)} | {Format(c, row.Unit)} | {Change(b, c, row.LowerIsBetter)} |");
            }
            md.AppendLine($"| Closing checks | {Checks(before)} | {Checks(after)} | |");
            md.AppendLine();
        }

        var text = md.ToString();
        Console.WriteLine(text);
        if (a.Get("out") is { } path) await File.WriteAllTextAsync(path, text);
        return 0;
    }

    private static Dictionary<int, JsonElement> Load(string folder)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "results.json")));
        return doc.RootElement.EnumerateArray().ToDictionary(
            r => r.GetProperty("participants").GetInt32(),
            r => r.Clone());
    }

    private static double? Num(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.Number ? e.GetDouble() : null;
    }

    private static bool Count(JsonElement r, string window) => Num(r, "server", window, "count") is > 0;

    private static string Checks(JsonElement r)
    {
        if (!r.TryGetProperty("checks", out var checks)) return "—";
        var all = checks.EnumerateArray().ToList();
        var failed = all.Count(c => !c.GetProperty("passed").GetBoolean());
        return failed == 0 ? $"✅ {all.Count} passed" : $"❌ {failed} of {all.Count} failed";
    }

    private static string Format(double? v, string unit) => v switch
    {
        null => "—",
        _ when unit == "ms" => Summary.F(v.Value),
        _ when unit == "/s" => v.Value.ToString("0.0", CultureInfo.InvariantCulture) + "/s",
        _ when unit == "%" => v.Value.ToString("0", CultureInfo.InvariantCulture) + "%",
        _ when unit == "MB" => v.Value.ToString("0", CultureInfo.InvariantCulture) + " MB",
        _ => v.Value.ToString("0", CultureInfo.InvariantCulture)
    };

    private static string Change(double? before, double? after, bool lowerIsBetter)
    {
        if (before is null || after is null) return "";
        if (Math.Abs(before.Value - after.Value) < 1e-9) return "same";
        if (before.Value == 0) return after.Value > 0 ? (lowerIsBetter ? "worse" : "better") : "";

        var ratio = after.Value / before.Value;
        var better = lowerIsBetter ? ratio < 1 : ratio > 1;
        var text = ratio >= 1
            ? $"{ratio:0.0}× {(lowerIsBetter ? "higher" : "more")}"
            : $"{(1 - ratio) * 100:0}% {(lowerIsBetter ? "lower" : "less")}";
        return better ? $"**{text}**" : text;
    }
}
