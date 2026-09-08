# Deployment

## Docker Compose

```bash
docker compose up -d --build          # postgres, redis, api
docker compose --profile scale up -d  # adds a second API instance on 8091
docker compose logs -f api
docker compose down                   # add -v to drop the volumes
```

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
| `Auction:*` | `Auction__*` | entry fee, participants, duration, percentages, rate limits |

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
