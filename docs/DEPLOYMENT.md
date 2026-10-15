# Deployment

## Docker Compose

```bash
docker compose up -d --build          # postgres, redis, api
docker compose --profile scale up -d  # adds a second API instance on 8091
docker compose logs -f api
docker compose down                   # add -v to drop the volumes
```

### The mobile app as a PWA

The same Flutter app also builds for the web and is served by the API at `/app/`, so a phone can
install it from the browser instead of a store:

```bash
pwsh scripts/build-pwa.ps1     # or ./scripts/build-pwa.sh
docker compose up -d --build api
```

The build lands in `src/AuctionPlatform.Api/wwwroot/app` (ignored by git, copied into the image)
and the API serves it, with any address inside the app falling back to its own `index.html`.

On the web the app talks to **whichever host served it** — `Uri.base.origin` — so one build works
on localhost, through a tunnel and in production. Pass `--dart-define=API_BASE_URL=…` only when the
API lives somewhere else.

Installing requires a secure origin: `https://…` or `localhost`. Once installed it opens without
browser chrome, in Arabic and right-to-left, with the brand's icon and splash.

The build also compresses every file it produces — Brotli at its highest setting, with gzip
alongside for anything that cannot take Brotli — and the API hands the small copy to browsers that
accept one (`UsePrecompressedStaticFiles`). It matters: the app's code is 3.7 MB uncompressed and
809 KB as Brotli, and the engine 7.2 MB against 2.2 MB, so a first load over a phone connection
drops from roughly 11 MB to 3 MB. Compressing per request instead would spend that CPU again on
every visitor for the same answer.

`web/sw.js` is the app's own service worker: it keeps the app's files (page, engine, fonts, icons)
for offline starts and never caches `/api/` or `/hubs/`, so prices, balances and auction state
always come from the server. Each build stamps it with a new cache name, so a release replaces the
old files. Flutter's own service worker no longer caches anything, so the build turns it off
(`--pwa-strategy=none`).

| Service | Container port | Host port (default) |
|---|---|---|
| api | 8080 | 8090 |
| api2 *(scale profile)* | 8080 | 8091 |
| postgres | 5432 | 5442 |
| redis | 6379 | 6389 |

Host ports are set in `.env` and deliberately avoid the usual 5432 / 6379 / 8080 so the stack can
run alongside other projects.

The API image is multi-stage: restore against project files alone (so the layer caches across
source edits), publish, then run on `aspnet:10.0` as a non-root user.

## Configuration

Everything is overridable by environment variable using `__` for nesting.

| Setting | Environment variable | Notes |
|---|---|---|
| `ConnectionStrings:Postgres` | `ConnectionStrings__Postgres` | |
| `Jwt:Secret` | `Jwt__Secret` | **Required in Production**, ≥ 32 chars |
| `Jwt:AccessTokenMinutes` | `Jwt__AccessTokenMinutes` | default 15 |
| `Jwt:RefreshTokenDays` | `Jwt__RefreshTokenDays` | **0 = session lasts until sign-out** (default) |
| `Otp:ValidForSeconds` | `Otp__ValidForSeconds` | default 300 |
| `Otp:MaxAttempts` | `Otp__MaxAttempts` | default 5 |
| `Otp:ResendCooldownSeconds` | `Otp__ResendCooldownSeconds` | default 60 |
| `Otp:MaxRequestsPerHour` | `Otp__MaxRequestsPerHour` | default 10 |
| `Otp:ExposeCodeInDevelopment` | `Otp__ExposeCodeInDevelopment` | ignored in Production |
| `Auction:RequireOtpForWithdrawal` | `Auction__RequireOtpForWithdrawal` | default true |
| `Redis:Enabled` | `Redis__Enabled` | `false` uses in-process fallbacks |
| `Redis:ConnectionString` | `Redis__ConnectionString` | |
| `Payments:WebhookSecret` | `Payments__WebhookSecret` | required for webhooks |
| `Firebase:Enabled` / `ProjectId` | `Firebase__*` | push is a no-op when disabled |
| `Database:AutoMigrate` | `Database__AutoMigrate` | |
| `Database:SeedDevelopmentData` | `Database__SeedDevelopmentData` | ignored in Production |
| `Cors:AllowedOrigins` | `Cors__AllowedOrigins__0`, `__1`, … | |
| `Auction:DefaultCurrency` | `Auction__DefaultCurrency` | **`SAR`** — the currency new rows are created in |
| `Auction:*` | `Auction__*` | entry fee, participants, duration, percentages, rate limits |

### Currency

`SAR` (الريال السعودي) is the platform default, set in two places that must agree:
`Money.DefaultCurrency` in the domain, and `Auction:DefaultCurrency` in configuration.

Changing it affects **new rows only**. Every wallet, auction, product, order and ledger entry
stores its own currency, and a movement whose currency does not match the wallet is rejected with
`CURRENCY_MISMATCH` — deliberately, so a change of default can never silently re-denominate money
that already exists. To move an existing database, update the stored rows yourself and decide
whether the amounts are being *relabelled* or *converted*; the two are not the same.

### Language

The mobile app and the admin dashboard are **Arabic only**, laid out right-to-left. The English
strings remain in `AppStrings` and the API still answers with English `message` text, so a second
language can be restored by making `localeProvider` read the user's choice again.

The app never shows the server's `message`: it maps the error `code` to Arabic and falls back to
the server text only for a code it does not recognise.

### One-time codes

Signing in is by one-time code, and so is confirming a withdrawal. **Tawked**
(<https://tawked.com>, docs at <https://docs.tawked.com>) issues, delivers and checks those codes
for Saudi mobile numbers.

```text
Tawked__ApiKey            tk_live_… in production, tk_test_… for the sandbox
Tawked__BaseUrl           https://tawked.com  (the default; only change it for a proxy)
Tawked__Application       partner keys (tk_partner_) only: the provisioned application's id
Tawked__Autofill          web | android | none — or empty to use the console's own setting
Tawked__TimeoutSeconds    15
```

**Production refuses to start without `Tawked__ApiKey`.** That is deliberate. Without a provider
the API answers a code request with a normal `200` and sends nothing, so the failure is invisible:
the customer waits for an SMS that never arrives and no log line says the platform cannot sign
anybody in. Refusing to start says it once, at the only moment somebody is watching.

Setting it up, once:

1. Create the account at <https://tawked.com> with a Saudi mobile number. It comes with SAR 10 of
   trial credit, no card.
2. Submit the application for review — its name, its website and a logo. **The message names the
   reviewed application, never anything from our request**, so the review is what puts "DealGo" in
   front of the customer. Until it is approved, every send answers `403 account_not_active`.
3. Take a key from the application's **Keys** tab and set `Tawked__ApiKey`.
4. Top up under **Wallet**. SAR 0.09 per delivered SMS code; a failed send is not charged.

Worth knowing:

* **Tawked generates the code.** There is no way to hand it one of ours, so for a mobile number
  Tawked holds the code and our `OtpCodes` row holds what that code may authorise — its purpose,
  the account, and the operation binding that ties a withdrawal code to one exact transfer.
* **Email codes are not delivered.** Tawked serves Saudi mobile numbers only. An email destination
  still takes the old local path, whose only sender writes to the log. Withdrawals fall back to
  email when an account has no valid Saudi mobile, so those accounts cannot withdraw.
* **Code length, lifetime and attempts are Tawked's settings**, changed in their console
  (6 digits, 5 minutes, 3 attempts, 5 sends per hour per number by default). Our `Otp:CodeLength`
  and `Otp:ValidForSeconds` only govern the local path now. `Otp:ResendCooldownSeconds` and
  `Otp:MaxRequestsPerHour` are still ours and still apply, on top of Tawked's own caps.
* **Anything that is our account's fault** — no balance, a dead key, an application still in
  review or auto-paused — is logged as **critical** with what to do about it, and the caller is
  told only `SERVICE_UNAVAILABLE`. Nobody signing in ever reads our billing state, so watch for
  those log lines: they are the only warning.
* The startup log says which path the deployment is on, and whether the key is sandbox or live.

### Uploaded pictures

An administrator uploads auction covers, prize photographs and product images through the console.
Where those bytes are kept is one setting:

```text
Storage__Provider=Local        a folder under wwwroot — development, or a single long-lived server
Storage__Provider=AzureBlob    a storage account — what a container needs
Storage__AccountUrl            https://<account>.blob.core.windows.net  (preferred: no secret)
Storage__ConnectionString      only for a host with no managed identity
Storage__Container=uploads     created on first upload, readable by anyone with the address
Storage__PublicBaseUrl         optional: a CDN or custom domain in front of the container
```

**A container's disk goes away with the container.** With `Local`, every picture ever uploaded
disappears on the next deployment, restart or scale event, and the app shows broken images. Any
deployment that replaces containers must use `AzureBlob`.

With `AccountUrl`, the API signs in as the identity the platform gave it — nothing to store or
rotate. Grant that identity **Storage Blob Data Contributor** on the account:

```bash
az containerapp identity assign -n <app> -g <group> --system-assigned
az role assignment create --assignee-object-id <principalId> --assignee-principal-type ServicePrincipal \
  --role "Storage Blob Data Contributor" \
  --scope $(az storage account show -n <account> -g <group> --query id -o tsv)
```

Where that is not possible, set `Storage__ConnectionString` from a secret instead, and leave
`Storage__AccountUrl` empty.

Pictures uploaded while the setting was `Local` are **not** moved by changing it. Copy the old
`wwwroot/uploads` folder into the container first, or upload them again.

### Production checklist

```text
ASPNETCORE_ENVIRONMENT=Production
Jwt__Secret                 from a secret manager, ≥ 32 chars
Payments__WebhookSecret     from the provider
ConnectionStrings__Postgres least-privilege database user
Redis__Enabled=true         required for multi-instance
Database__SeedDevelopmentData=false   (also ignored, but be explicit)
Database__AutoMigrate=false           apply the migration script in your release step
Cors__AllowedOrigins        real origins only
Storage__Provider=AzureBlob uploaded pictures survive a deployment (see above)
Tawked__ApiKey              required: without it nobody can sign in (see above)
HTTPS terminated at the proxy, with HSTS
```

**Before going live, replace `IOtpSender`.** The shipped `LoggingOtpSender` writes the message to
the log instead of sending it; in Production it logs an error and delivers nothing, so sign-in by
code and withdrawal confirmation will not work until a real SMS or email provider is wired in.

The API refuses to start in Production without a proper JWT secret — a deliberate hard failure
rather than silently signing tokens with a guessable key.

## Migrations in production

```bash
dotnet ef migrations script --idempotent --output migrate.sql \
  --project src/AuctionPlatform.Infrastructure \
  --startup-project src/AuctionPlatform.Api
```

Review and apply through your release process with `Database__AutoMigrate=false`. If you do let the
application migrate, enable it on exactly one instance.

## Scaling

The API is stateless: no business logic depends on process-local memory. To scale, run more
instances behind a load balancer.

Requirements:

1. **Redis is mandatory** for more than one instance — it carries the SignalR backplane so a bid
   accepted on one instance reaches clients connected to another.
2. **Sticky sessions are not required.** SignalR reconnects rejoin groups and resync from the
   snapshot endpoint.
3. **Workers run everywhere.** Duplicate work is prevented by row locks, conditional updates and
   unique constraints — not by electing a leader.

Verify with the `scale` profile and confirm that a bid on 8090 appears immediately for a client
connected to 8091.

## Health checks

```text
/health        overall
/health/live   liveness  — no dependencies, safe for a restart probe
/health/ready  readiness — PostgreSQL and Redis, safe for a traffic probe
```

Point the container's restart probe at `/health/live` and the load balancer at `/health/ready`.

## Observability

**Serilog** writes structured JSON to the console and to `logs/api-.log` (daily, 14 days retained).
Request logging is on by default.

**OpenTelemetry** instruments ASP.NET Core and HttpClient for traces and metrics. Add an OTLP
exporter endpoint to ship them to a collector.

Worth alerting on: bid rejection rate, refund failures, payment failures, out-of-stock events at
checkout, SignalR connection count, and any `AuditService` write failure.

## Backups

PostgreSQL holds every financial record; Redis holds nothing that cannot be rebuilt.

```bash
docker exec auction-postgres pg_dump -U postgres -Fc auctions > auctions-$(date +%F).dump
docker exec -i auction-postgres pg_restore -U postgres -d auctions --clean < auctions.dump
```

For production: automated daily backups, WAL archiving for point-in-time recovery, off-site
retention, and a **restore test on a schedule** — an untested backup is a guess.

## Disaster recovery

| Failure | Recovery |
|---|---|
| Redis lost | Restart. Locks and caches rebuild; correctness was never dependent on them |
| API instance lost | Others carry on. The completion worker finishes any interrupted auction |
| Database lost | Restore from backup, then point-in-time recovery to the last consistent moment |
| Refund failed | The completion worker retries; the unique constraint prevents a double payout |
| Webhook missed | Ask the provider to redeliver — reprocessing is idempotent |
| Auction stuck `Finished` | The completion worker picks it up within 30 seconds |

## Admin dashboard

Served from `wwwroot/admin` by the API itself, so there is no second deployment. It is a
dependency-free SPA calling the same `/api/v1` surface with the same authorization.

Reachable at `/admin/`; `MapFallbackToFile` handles client-side routes.

## Mobile release

```bash
cd mobile
flutter build apk    --release --dart-define=API_BASE_URL=https://api.example.com
flutter build appbundle --release --dart-define=API_BASE_URL=https://api.example.com
flutter build ipa    --release --dart-define=API_BASE_URL=https://api.example.com
flutter build web    --release --dart-define=API_BASE_URL=https://api.example.com
```

For push notifications, add the Firebase configuration files (`google-services.json`,
`GoogleService-Info.plist`), apply the Firebase Gradle plugin, and set `Firebase__Enabled=true` on
the API with service-account credentials. Without them the app builds and runs normally; only push
delivery is inactive.

> On Windows, device builds need Developer Mode enabled for plugin symlinks
> (`start ms-settings:developers`).

## CI

`.github/workflows/ci.yml` runs restore → build → unit tests → integration and concurrency tests →
migration validation → Docker build, plus Flutter analyze, test and build. The integration tests
start their own PostgreSQL container, so no service definition is needed.
