# Security

## Authentication

JWT access tokens (15 minutes) plus rotating refresh tokens (30 days).

- Refresh tokens are stored as **SHA-256 hashes only**; the raw value never touches the database,
  so a database leak yields no usable tokens.
- Every refresh consumes the presented token and issues a replacement, recording
  `ReplacedByTokenId`.
- Presenting an already-consumed token is treated as theft: the entire token family for that user
  is revoked and the attempt is logged as a warning.
- Changing a password, resetting one, or disabling an account revokes every outstanding session.
- Login returns the same error for an unknown email and a wrong password, so the endpoint cannot
  enumerate accounts. `forgot-password` always returns 200 for the same reason.

The mobile client keeps tokens in the platform keystore (Keychain / EncryptedSharedPreferences),
never in plain preferences, and shares a single in-flight refresh across concurrent 401s so a burst
of requests cannot consume the single-use token more than once.

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
