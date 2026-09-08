# Security

## Authentication

JWT access tokens (15 minutes) plus rotating refresh tokens.

### Sessions last until sign-out

`Jwt:RefreshTokenDays = 0` — the default — means a refresh token has no expiry of its own. A user
stays signed in until they explicitly sign out. Set a positive number for a fixed lifetime instead.

This is a deliberate trade. What still ends a session:

- signing out (`/auth/logout`, optionally on every device)
- changing or resetting the password
- an administrator disabling the account
- refresh-token reuse being detected

Rotation is unchanged: every refresh consumes the presented token and issues a replacement, so a
stolen token is still single-use and its reuse still burns the whole family. The window in which a
stolen token is useful is therefore governed by how quickly reuse is detected, not by a clock.

### Token handling

- Refresh tokens are stored as **SHA-256 hashes only**; the raw value never touches the database,
  so a database leak yields no usable tokens.
- Each rotation records `ReplacedByTokenId`, which is what makes reuse detectable.
- Presenting an already-consumed token is treated as theft: the entire token family for that user
  is revoked and the attempt is logged as a warning.
- Login returns the same error for an unknown email and a wrong password, so the endpoint cannot
  enumerate accounts. `forgot-password` always returns 200 for the same reason.

The mobile client keeps tokens in the platform keystore (Keychain / EncryptedSharedPreferences),
never in plain preferences, and shares a single in-flight refresh across concurrent 401s so a burst
of requests cannot consume the single-use token more than once.

### One-time codes

Codes are used for two things: signing in, and confirming a withdrawal.

| Property | How |
|---|---|
| Never stored in the clear | SHA-256, salted with the destination and purpose |
| Unguessable | 6 digits from a cryptographic RNG, no modulo bias |
| Compared safely | `CryptographicOperations.FixedTimeEquals` |
| Single use | Consumed on success; replay returns `OTP_ALREADY_USED` |
| Short-lived | 5 minutes by default |
| Purpose-scoped | A sign-in code cannot authorise a withdrawal |
| Operation-bound | A withdrawal code names the exact amount, currency and destination |
| Brute-force resistant | Dies after 5 wrong guesses, even for the correct code afterwards |
| Superseded on resend | Requesting a new code retires the previous one |
| Throttled | 60s between requests, 10 per destination per hour |
| Never enumerating | An unknown destination gets a plausible challenge with no code behind it |

Destinations are normalised before use, so `+966 50 123 4567` and `+966501234567` are one identity
and cannot be used to sidestep the rate limit. Everything shown back to the client is masked
(`us***@example.com`, `*******4567`), including in logs.

`Otp:ExposeCodeInDevelopment` echoes the code in the API response so the flow is testable without
an SMS or email provider. The service checks `IHostEnvironment` directly, so the code is withheld
in Production regardless of configuration — the decision is made once, not at each call site.

Delivery is behind `IOtpSender`. The shipped implementation logs instead of sending; replace it
with a Twilio/SNS/SendGrid client and nothing else changes.

### Withdrawal confirmation

`Auction:RequireOtpForWithdrawal` (on by default) requires a code before money leaves the platform,
so a stolen access token alone is not enough.

The code is bound to a SHA-256 fingerprint of the amount, currency and destination. Verification
recomputes it, so a code obtained for a 10 transfer to your own account cannot approve a 5,000
transfer to someone else's — both return `INVALID_OTP`.

The Shopping wallet remains non-withdrawable regardless: the OTP gate is an extra layer over that
rule, never a way around it.

### JWT secret

The API **refuses to start in Production** without a `Jwt:Secret` of at least 32 characters.
Development falls back to a clearly-marked throwaway key. Supply the real one through environment
variables or a secret manager; never commit it.

## Authorization

Roles: `USER`, `ADMIN`, `SUPER_ADMIN`, `AUCTION_MANAGER`, `FINANCE`, `SUPPORT`, `STORE_MANAGER`.

Fine-grained permissions travel as claims and are checked by a policy per permission:

```text
Auction.Read  Auction.Create  Auction.Update  Auction.Start  Auction.Cancel
Store.Read  Store.CreateProduct  Store.UpdateProduct  Store.DeleteProduct
Store.ManageInventory  Store.ManageOrders  Store.ManageSettings
Wallet.Read  Wallet.Adjust  Wallet.Refund
User.Read  User.Manage  Reports.Read  Audit.Read
```

`SUPER_ADMIN` satisfies every policy. Role changes are restricted to `SUPER_ADMIN` and require a
written reason.

**Every check happens on the server.** The admin dashboard hides nav items the user cannot use,
but that is a convenience: the endpoint refuses regardless. Ownership is enforced too — a prize
claim, order or wallet is only accessible to the user it belongs to, verified from the token rather
than from any request parameter.

## What the server never accepts from a client

```text
currentPrice   targetPrice   bidAmount   rank      winner
refundAmount   walletBalance productPrice orderTotal serverTimestamp
```

A bid request contains an idempotency key and nothing else. A checkout request contains product ids
and quantities. Everything else is derived from committed database state.

## Rate limiting

| Policy | Applies to | Limit |
|---|---|---|
| `bidding` | `POST /auctions/{id}/bids` | Token bucket, 10 tokens, 5/second refill |
| `auth` | `/auth/*` | 20 per minute |
| global | everything | 300 per minute |

Partitioned by user id when authenticated, otherwise by IP. A second, Redis-backed limiter inside
`BidService` enforces per-user-per-auction caps that work across instances. Exceeding either
returns the standard envelope with `RATE_LIMITED` and HTTP 429.

## Payment webhooks

- The **raw** request body is read verbatim, because the signature is computed over exact bytes.
- HMAC-SHA256 with the configured secret, compared with `CryptographicOperations.FixedTimeEquals`.
- An unsigned or wrongly-signed request is rejected before anything is parsed.
- The event is persisted **before** processing; `UNIQUE(Provider, ProviderEventId)` turns
  redelivery into a unique-index violation, answered with 200 so the provider stops retrying.
- The credit itself is idempotency-keyed, so even a bypassed check cannot double-credit.
- Webhook deposits reach the Main wallet only; the Shopping wallet is never externally funded.

## Idempotency

Every mutating financial operation is idempotency-keyed and enforced by a unique index, not by
application checks:

| Operation | Enforced by |
|---|---|
| Bid | `UNIQUE(AuctionId, ParticipantId, IdempotencyKey)` |
| Wallet movement | `UNIQUE(UserId, IdempotencyKey)` |
| Entry fee refund | `UNIQUE(AuctionId, ParticipantId)` |
| Store checkout | `UNIQUE(UserId, IdempotencyKey)` |
| Payment | `UNIQUE(Provider, ProviderTransactionId)` |
| Webhook | `UNIQUE(Provider, ProviderEventId)` |

## Auditing

Two append-only trails:

- **`AuctionEvents`** — everything that happened to an auction, from `PARTICIPANT_JOINED` through
  `WINNERS_DECLARED` and `SHOPPING_CREDIT_ADDED`.
- **`AuditLogs`** — administrative and financial actions: the action, the actor and their email,
  the entity, before/after JSON, the reason, IP and user agent.

Audited by default: auction create / update / publish / start / schedule / pause / resume / cancel,
store product and category changes, inventory adjustments, order transitions and refunds, shopping
credit issuance, wallet adjustments, role changes and account enable/disable.

Financial adjustments, inventory adjustments, refunds, role changes and account status changes all
**require a written reason** — the API rejects the request without one.

Audit writes never abort the operation they describe: a lost log line is preferable to a
half-applied refund. Failures are logged as errors.

## Transport and headers

- HSTS and HTTPS redirection outside development
- `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`
- CORS restricted to a configured origin list, with credentials allowed for the SignalR handshake
- The SignalR access token is accepted from the query string **only** on the hub path, because a
  WebSocket handshake cannot carry custom headers

## Passwords

Minimum 8 characters, at least one digit and one lowercase letter, hashed by ASP.NET Core Identity
(PBKDF2). Lockout after 10 failed attempts for 15 minutes.

## Secrets

Never commit `Jwt:Secret`, database passwords, `Payments:WebhookSecret`, Firebase credentials or
API keys. `appsettings.json` ships with empty values; supply real ones through environment
variables, user secrets or a secret manager. `.gitignore` excludes `.env`, `*.user`, `secrets.json`
and log output.

## Data retention

Financial and audit records are append-only in practice — payments, wallet transactions, refunds,
orders, bids, winner records and audit logs have no delete path exposed to ordinary admins.
Products and categories referenced by history are archived rather than removed.

## Recommended for production

- MFA for `SUPER_ADMIN` and `FINANCE`, and for anyone performing financial adjustments
- A real payment provider behind `PaymentWebhookService`
- Firebase service-account credentials for push, with `Firebase:Enabled = true`
- OpenTelemetry exported to a collector, with alerting on bid rejections, payment failures and
  refund failures
- Regular restore tests of the PostgreSQL backups
