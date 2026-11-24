# Bidding load test and live-auction monitor

Two jobs, one tool:

- **`run`** — simulated participants join and bid in fresh auctions on a server **on this machine**,
  then the result is checked against the database. For 13, 50, 100 and 400 participants by default.
- **`monitor`** — watches one real auction on dealgosa.com from start to finish **without bidding**:
  how fast updates reach a phone, whether the close was right, and the server's CPU, memory and
  database figures while it ran.

Nothing in `run` ever talks to dealgosa.com: it refuses any address that is not on this machine
unless `--allow-remote-server` is given on purpose.

## What it measures

| Question | How |
|---|---|
| Bid acceptance latency p50 / p95 / p99 | Every bid timed from sending to the answer, per participant count, steady part and closing burst apart |
| Failed vs. rejected bids | Each answer sorted into *accepted*, *rejected* (a rule said no: last bidder, too fast, auction over) or *failed* (server error, timeout, network, anything unexpected) |
| Price on every screen | Each simulated phone keeps a live connection; for every bid: did it arrive, how long after the server accepted it, did it arrive out of order, and would the app have shown an older price |
| Leaderboard latency | From a bid being accepted to each phone receiving a top-ten list that includes it |
| Database pool | Connections in use and the pool size, requests waiting for a connection (true peak), time to get a connection, time to create one, command times, failures |
| The bid queue | How long bids and joins waited for their auction's turn in the server's queue (holding no database connection), the longest line, how long each turn took, and requests refused as busy, timed out or shut out |
| Lock contention | Time each bid and join waited for the auction's row lock, and how long a bid then held it — which sets the most bids a second one auction can take |
| Other auctions | With `-Neighbors N`, a small second auction bids alongside the busy one; its bid times show whether a busy auction slows the rest |
| CPU and memory | Server process CPU (average and peak per second), working set, GC heap, allocation rate, collections, thread pool |
| Closing accuracy | Every confirmed bid stored and nothing else; sequence 1…N with no gaps; each bid exactly one increment; nobody twice in a row; no bid at or after the closing time (or exactly one reaching the target); final price; winners = top four by price then time; prize amounts; one refund per loser; wallets add up |

Server-side figures come from `GET /api/v1/admin/diagnostics/runtime` (super administrators only),
which the API now serves from its own process — shared hosting allows no profiler.

## How participants behave

Like people with the app open: each bids at random moments, about `--rate` bids a second
(default 0.5), rising to `--burst-rate` (default 2) in the last `--burst-seconds` (default 15).
At most one bid in flight, and never straight after their own bid — the app's button is disabled
in both cases. The last auction in the video had 13 people placing about 5 bids a second in
total; the default rate gives 6.5 a second for 13 and scales with the count.

## Running locally

Needs Docker Desktop running. From the repository root:

```powershell
.\tools\AuctionLoadTest\run-local.ps1
```

It starts a throwaway PostgreSQL in Docker (`dealgo-loadtest-pg` on port 5462), publishes the API
in Release and runs it as SmarterASP does (Redis off, so locks, rate limits and SignalR are
in-process; no demo data), creates the test bidders, runs each participant count in its own
auction, and writes `artifacts\loadtest\<time>\report.md`, `results.json` and one
`attempts-<n>.csv` per count. Everything is stopped and removed at the end (`-KeepDatabase` keeps
PostgreSQL).

Useful variations:

```powershell
.\tools\AuctionLoadTest\run-local.ps1 -Participants 400 -DbDelayMs 2     # database 2 ms away each way
.\tools\AuctionLoadTest\run-local.ps1 -Close target -Participants 400    # race to the target price
.\tools\AuctionLoadTest\run-local.ps1 -Mode demo                         # demo auctions (no money)
```

`-DbDelayMs` puts a relay between the API and PostgreSQL that holds every packet for that long,
to imitate a database on another machine, which is how SmarterASP is set up.

To measure another build with the same tool — an older commit, say — check it out beside this one
and point the runner at its API:

```powershell
git worktree add ..\dealgo-before perf-baseline-2026-11-23
.	ools\AuctionLoadTestun-local.ps1 -ApiProject ..\dealgo-before\src\AuctionPlatform.Api\AuctionPlatform.Api.csproj
```

## Comparing two runs

```powershell
dotnet run --project tools\AuctionLoadTest -c Release -- compare --baseline artifacts\loadtest\<before> --candidate artifacts\loadtest\<after> --out compare.md
```

Side by side for each participant count present in both: bid latency, throughput, failures,
database connections and waits, queue and lock times, leaderboard and price delivery, how soon the
winners appear, CPU, memory and the closing checks.

## Watching the next live auction

After the diagnostics endpoint is deployed, run this on any machine while the auction runs. It signs
in with a super administrator's phone (a code is sent by SMS) and picks the live or next auction:

```powershell
dotnet run --project tools\AuctionLoadTest -c Release -- monitor --phone 05XXXXXXXX
```

Or name the auction with `--auction <id>`. It writes `artifacts\monitor\<time>\monitor-report.md`,
every event it heard (`events.jsonl`) and the server readings (`diagnostics.json`). Press Ctrl+C
to stop early; it stops by itself ten seconds after the auction reaches its final state. It places
no bids and joins nothing; it adds one live connection and a few requests a second.

## Reading the results

- *Rejected* answers are the rules working. *Failed* answers are the thing to chase.
- Latencies from a run on this machine do not include the internet or the shared host; compare the
  shape (what grows with participants, where the time goes) more than the absolute numbers.
- The first second of each fresh server is slower while .NET compiles the bid path; IIS does the
  same after an app pool recycle.
