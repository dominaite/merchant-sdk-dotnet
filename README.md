# Dominaite.MerchantSdk

Server-side .NET client for the Dominaite merchant API. One call from your backend opens a
hosted checkout session; a two-line script tag renders the payment widget on your page. Card
details go straight from your customer's browser into the payment widget - they never touch your
server, which keeps your PCI scope minimal (SAQ A).

Targets `net8.0` (so it runs on .NET 8, 9 and 10). Zero runtime NuGet dependencies:
`System.Text.Json` and `HMACSHA256` are both in-box.

## Install

Nothing is published to NuGet yet - the package id is still an owner decision, so the project
ships with `IsPackable=false` and there is no publish workflow. Until then, clone and reference
the project:

```sh
git clone https://github.com/dominaite/merchant-sdk-dotnet
dotnet add YourApp.csproj reference merchant-sdk-dotnet/src/Dominaite.MerchantSdk/Dominaite.MerchantSdk.csproj
```

To work on the SDK itself:

```sh
dotnet build
dotnet test          # includes the offline signing and webhook vectors
```

## Credentials

You get two values from the Dominaite dashboard, **Website integration** tab, when you generate
an API key (shown once - store them like passwords):

- `dmk_...` - your API key id. Identifies you; not secret by itself.
- `dms_...` - your API secret. Server-side only: environment variable, or a secret store. Never
  in a browser, never in git, never in logs.

Every request is signed with the secret (HMAC-SHA256) and timestamped. Keep your server clock on
NTP - signatures older than 5 minutes are rejected with `TIMESTAMP_OUT_OF_RANGE`.

If the key has an IP allowlist, calls from anywhere else fail with `IP_NOT_ALLOWED`. The allowlist
is managed on the same dashboard tab.

`DominaiteClient.ToString()` redacts the secret, and so does its debugger display, so a logged
client object cannot leak it. The same goes for `CheckoutSession.ToString()` and the session's
`CashierToken`.

## Environments

| Environment | Base URL |
|---|---|
| Production | `https://api.dominaite.com/payments` (the default) |
| Dev / staging | the raw payments function host, whose Azure Functions route prefix is `/api`, e.g. `https://func-dom-gw-payments-dev-gwc-01.azurewebsites.net/api` |

Confirm the host for your environment before the first call. **A dev key against production is a
guaranteed `INVALID_API_KEY`** - keys are issued per environment.

The base URL's own prefix is never part of the signed path: on dev you POST to
`.../api/merchant-api/bridgerpay/checkout/sessions` but you sign
`/merchant-api/bridgerpay/checkout/sessions`.

## Quickstart

```sh
export DOMINAITE_KEY_ID=dmk_...      # Website integration tab
export DOMINAITE_SECRET=dms_...      # shown once when you generated the key
export DOMINAITE_BASE_URL=https://func-dom-gw-payments-dev-gwc-01.azurewebsites.net/api
# Production needs no DOMINAITE_BASE_URL.
```

```csharp
using Dominaite.MerchantSdk;

using var client = new DominaiteClient(
    Environment.GetEnvironmentVariable("DOMINAITE_KEY_ID")!,
    Environment.GetEnvironmentVariable("DOMINAITE_SECRET")!,
    new DominaiteClientOptions
    {
        // An unset variable is null, and a null value keeps the production default.
        BaseUrl = Environment.GetEnvironmentVariable("DOMINAITE_BASE_URL"),
    });

// First live call: proves key, secret, signing and clock without creating anything.
var ping = await client.PingAsync();
Console.WriteLine($"merchant {ping.MerchantId}, clock skew {ping.ClockSkewSeconds}s");

var session = await client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
{
    Amount = 2500, // 2500 = 25.00 EUR, always MINOR units
    Currency = "EUR",
    OrderReference = "order-1042",
    Customer = new Customer { FirstName = "Ana", Email = "ana@example.com" },
});

// Store session.TransactionId against your order, then hand CashierKey and CashierToken to the
// page that renders the widget.
```

A runnable version is in `examples/CreateSession`, using the same three environment variables:

```sh
dotnet run --project examples/CreateSession
```

Render the widget with the two cashier values:

```html
<div id="checkout">
  <script src="https://bp-checkout.dominaite.com/v2/launcher"
          data-cashier-key="CASHIER_KEY_FROM_SESSION"
          data-cashier-token="CASHIER_TOKEN_FROM_SESSION"></script>
</div>
```

The launcher renders the form where the script tag sits, so keep the script inside your container.
`CashierKey` and `CashierToken` are per-payment session values, not credentials - but HTML-escape
them when you template them into the page, and **never log the token**.

### Then find out whether it got paid

Opening the session is half the integration. The widget runs in your customer's browser, so your
server does not learn the outcome from the call above - something has to tell it.

Register a webhook endpoint, verify every delivery with `Webhooks.Verify`, and fulfil the order
when `payment.succeeded` arrives. See [Webhooks](#webhooks). If you cannot receive inbound
requests yet, poll `GetStatusAsync` instead and move to webhooks when you can.

Either way, keep a reconciliation sweep. Webhooks are the fast path, not the guarantee.

## Verify your signing before your first live call

Run `dotnet test` before you touch the live API. The SDK signs for you, but the recipe is pinned by
offline known-answer vectors shared with the gateway and the dashboard, and the suite reproduces
them byte-for-byte. If any fails, nothing else matters - every live call would come back
`INVALID_SIGNATURE`.

`RequestSigner.Sign` is public so you can pin the recipe in your own suite, or debug an
`INVALID_SIGNATURE` without reading this SDK's source:

```csharp
var signature = RequestSigner.Sign(new SignatureInput
{
    Secret = "dms_...",
    Timestamp = "1755302400",                                 // unix SECONDS
    Method = "POST",
    Path = "/merchant-api/bridgerpay/checkout/sessions",       // path only, no host
    IdempotencyKey = "00000000-0000-4000-8000-000000000001",   // "" for GET
    Body = """{"amount":2500,"currency":"EUR","orderReference":"order-1042"}""", // "" for GET
});
// "95759958a0a0a9bd3e6e37101c01e8e7fee1166406e4ac2ff488764f5f742cbf"
```

The signed payload is five lines:
`"{timestamp}\n{METHOD}\n{path}\n{idempotencyKey}\n{sha256hex(body)}"`, signed as lowercase hex
HMAC-SHA256 with your secret, UTF-8 throughout. Two things to get right:

- GET signs an EMPTY idempotency key and an EMPTY body, and sends no `Idempotency-Key` header.
  The payload is still five lines.
- The signed path never includes the base URL's own prefix.

The body is serialized exactly once: the bytes that are hashed are the bytes that are sent, which
is what keeps a non-ASCII payer name from producing a signature over one encoding and a request
over another.

## Ping before your first mint

```csharp
var ping = await client.PingAsync();
```

`PingAsync` is a GET that creates nothing and reads nothing. It returns `Pong`, your `MerchantId`,
`ServerTime`, `ServerUnixTime` and `ClockSkewSeconds` (server time minus your `X-Timestamp`). If
the absolute skew creeps toward 300, fix NTP now - requests start failing at 300.

Only after ping returns should you mint your first session. A 401 there means key id, secret, or
signing; a 503 means retry later. Never both at once.

## Client options

`new DominaiteClient(keyId, secret)` gives you production with a 45s timeout.
`DominaiteClientOptions` takes more:

| Option | What |
|---|---|
| `BaseUrl` | Point at a non-production environment. Empty and whitespace-only values are ignored, so an unset env var still gives you production. |
| `Timeout` | Per-request timeout. Defaults to 45s (serverless cold starts can take 10+s). |
| `UserAgentSuffix` | Appends your identifier to the SDK's User-Agent, which helps when support reads the access logs. |
| `HttpClient` | Your own client, for a proxy-aware or factory-managed transport. The SDK will not dispose it, and it overrides `Timeout`. Configure its handler with `AllowAutoRedirect = false`. |

One client per process is the normal shape - it owns an `HttpClient` and its connection pool.

The client the SDK builds for itself never follows redirects, and any 3xx is treated as a hard,
non-retryable error. The Dominaite API never redirects, so a 3xx means something in front of it is
answering, and following it would replay your signed request at whatever host the redirect names.

## Amounts are minor units

`Amount` is always an integer in the currency's minor unit: `2500` is 25.00 EUR. The property is a
`long`, so a decimal will not compile; non-positive values are rejected before anything reaches the
network. The amount is locked server-side - what you pass here is what gets charged, and nothing in
the browser can change it. Compute it from your own catalog, never from the request body your page
sent you.

The minor-unit exponent follows ISO 4217 per currency: EUR has 2 decimals, JPY has 0 (so `2500` is
JPY 2,500), KWD has 3. Never hardcode a x100 conversion.

## Retries and double-charges

Every `CreateCheckoutSessionAsync` call carries an idempotency key. Leave
`CheckoutSessionRequest.IdempotencyKey` null and the client generates one per logical call and
writes it back onto the request, so you can log it and reuse it.

`CreateCheckoutSessionWithRetryAsync` pins one key up front and reuses it across every attempt,
retrying only transport failures (network errors, timeouts, and 5xx including
`MERCHANT_API_UNAVAILABLE`). Refusals and authentication failures are thrown immediately - they
will not change.

```csharp
var session = await client.CreateCheckoutSessionWithRetryAsync(
    request,
    new RetryOptions { Attempts = 3, BaseDelay = TimeSpan.FromMilliseconds(500) });
```

Reusing the key is what makes the retry safe: a retried key never opens a second payment. What it
does today is come back as a replay refusal - `DUPLICATE_REQUEST` if the earlier attempt's session
is still open, `ALREADY_PROCESSED` if its payment completed - naming the transaction it collided
with. So a retry after a timeout either succeeds (the first attempt never landed) or hands you the
transaction id of the attempt that did, which you read back with `GetStatusAsync`:

```csharp
try
{
    var session = await client.CreateCheckoutSessionWithRetryAsync(request);
}
catch (DominaiteRefusalException error) when (error.TransactionId is { } collided)
{
    var status = await client.GetStatusAsync(collided);
    // Now you know what the earlier attempt actually did.
}
```

`TransactionId` is null when the API did not name one (a concurrent-race `DUPLICATE_REQUEST` knows
the key is taken but not yet by which row), so check it rather than assuming.

**Cart changed = new session, new idempotency key.** There is no session-update call: if the
order's amount or contents change after a session exists, abandon it and mint a fresh session with
a fresh key. Reusing the old key with a new amount is rejected as `IDEMPOTENCY_KEY_REUSED` by
design.

## Sessions expire

A session is valid for about 2 hours. If the payer comes back later, create a new one. Before
re-rendering the widget for a stored session, read the status first: a completed session's widget
shows "session is closed or expired", which reads as an error to someone who just paid.

## Webhooks

Webhooks are how you find out a payment succeeded without asking. Point an endpoint at your server
on the dashboard's **Webhooks** tab, pick the events you care about, and store the `whsec_...`
secret it shows you - it is shown exactly once, and regenerating it kills the old one.

**Verify the signature before you parse the body.** An unverified webhook is an unauthenticated
stranger POSTing JSON at your server.

```csharp
// body must be the RAW request bytes, exactly as received.
if (Webhooks.TryVerify(body, signatureHeader, secret, out var failure))
{
    var evt = JsonDocument.Parse(body);
    // Dedupe on evt.RootElement.GetProperty("id"), enqueue the work, then answer 2xx.
}
else if (failure!.Reason == WebhookFailureReason.TimestampOutOfTolerance)
{
    // A replay, or your clock drifted.
}
```

`Webhooks.Verify` throws `DominaiteWebhookException` instead, if you prefer to catch. Both take
`(payload, signatureHeader, secret, toleranceSeconds, nowUnixSeconds)`; `nowUnixSeconds` is there
for tests and pinned vectors, and null reads the system clock. There is a `byte[]` overload for
handlers that hold the raw request bytes.

The MAC comparison is constant-time, and it runs before the timestamp check so an unsigned request
learns nothing about your tolerance window.

The signature arrives in `X-Webhook-Signature` as `t={unix_seconds},v1={lowercase_hex}`: an
HMAC-SHA256 over `"{t}.{raw_body}"` keyed with the UTF-8 bytes of your `whsec_` secret. The default
tolerance is 300 seconds, which matches the server.

Getting the raw body is the part frameworks get wrong. If your handler hands you a deserialized
model and you re-serialize it to verify, property order or whitespace will differ and every
delivery will fail as `SignatureMismatch`. In ASP.NET Core, enable buffering and read the body
before any JSON layer touches it.

### The envelope

Flat JSON, no `success` wrapper - do not branch on a `success` field, there isn't one.

```json
{
  "id": "<delivery id - your dedupe key>",
  "type": "payment.succeeded",
  "createdAt": "<ISO 8601 UTC instant of the transition>",
  "data": {
    "transactionId": "...",
    "status": "succeeded",
    "previousStatus": "pending",
    "kind": "sale",
    "amount": 8440,
    "grossAmount": 8701,
    "surchargeAmount": 261,
    "currency": "EUR",
    "originalTransactionId": null,
    "idempotencyKey": "order-123"
  }
}
```

Amounts are minor units. On `payment.*` events `amount` is what you are PAID (base), while
`grossAmount` is the card movement; on `payment.refunded` the `amount` is what went back to the
customer. `surchargeAmount`, `previousStatus`, `kind` and `originalTransactionId` are nullable.

### Events

`payment.succeeded`, `payment.failed`, `payment.requires_capture`, `payment.cancelled`,
`payment.abandoned`, `payment.refunded`, `payment.disputed`. That is the whole set, exact case;
registering anything else is rejected.

`payment.succeeded` is the only signal that means money is in hand. `requires_capture` includes
approved pre-auth holds, `cancelled` is a pre-completion void only, `abandoned` is the sweep's
verdict on a checkout that was never paid, and `refunded` fires once per refund from the refund
ledger row rather than from the parent flipping status. `pending` and `processing` are not
webhooked at all - poll session status if you want in-flight UX.

### Delivery

Delivery is **at-least-once**, so the same event can arrive twice and you must dedupe on `id`.
Respond 2xx quickly and queue the work; doing it inline is how you end up timing out and collecting
retries you did not want.

Failed deliveries are retried up to your endpoint's `RetryCount` (default 3, max 10, 0 disables)
spaced 1m / 5m / 30m / 2h / 12h. An endpoint whose initial attempt and every configured retry fail
consecutively is auto-disabled; a later successful delivery re-enables it. Disabling an endpoint
yourself in the dashboard is never overridden. You get at most 25 active endpoints.

### Reconciliation is still mandatory

Webhooks complement your reconciliation sweep, they do not replace it. There are real loss windows -
there is no publish outbox, and chains parked on a disabled endpoint stay parked - so keep a
periodic sweep that reads status for orders you believe are unpaid and settles the difference.

## Status polling

Use this when you cannot receive webhooks - local development with no public URL, or a network that
will not accept inbound requests - and as the read side of the reconciliation sweep above.

```csharp
var status = await client.GetStatusAsync(session.TransactionId);
if (status.IsPaid) { /* fulfil the order */ }
```

`status.Status` is one of `pending`, `processing`, `succeeded`, `failed`, `refunded`,
`partially_refunded`, `cancelled`, `disputed`, `requires_capture`, `abandoned` - the
`TransactionStatuses` constants, enumerable as `TransactionStatuses.All`. **`succeeded` is the only
value that means the customer paid**, which is what `IsPaid` answers. `IsTerminal` tells you whether
to stop polling, and reports a status it does not recognise as NOT terminal, so a value the API adds
later makes you keep polling instead of closing an open order.

`requires_capture` is **not** "unpaid": the payer has already paid and the funds are held awaiting
capture, which is why `IsPaid` (settled) and `IsTerminal` (finished) both answer false for it. Never
treat it as an abandoned order.

Call this from your server, never from the browser, and poll after the payer returns to you or on
your order timeout - not in a tight loop, the endpoint is rate limited per key.

Every response type also carries `Raw` (a `JsonElement`) with the unparsed payload, for fields the
types do not model yet.

## Errors

Every call throws a subclass of `DominaiteException`. Catch the subclass, and read `Code` for the
machine-readable string where there is one.

| Exception | When | What to do |
|---|---|---|
| `DominaiteRefusalException` | HTTP 200 with `success: false`. Carries `TransactionId` and `RawResult`. | Branch on `Code`. Do not blind-retry. |
| `DominaiteAuthException` | 401/403. `Code` is `INVALID_API_KEY`, `INVALID_SIGNATURE`, `TIMESTAMP_OUT_OF_RANGE`, or `IP_NOT_ALLOWED`. | Fix the key id, secret, server clock, or allowlist. Never retry-loop. |
| `DominaiteTransportException` | Network failure, timeout, or 5xx (`MERCHANT_API_UNAVAILABLE`). | Retry with the **same** idempotency key. `IsRetryable` is true only here. |
| `DominaiteApiException` | Any other rejecting or unexpected response, including a 3xx. `Code` carries the API's reason when it sent one, e.g. `IDEMPOTENCY_KEY_REQUIRED` on a 400. | Inspect `HttpStatus` and `Code`. A 422 means an idempotency key was replayed with a different body - use a fresh key. A 404 from `GetStatusAsync` is an unknown transaction id. |
| `DominaiteValidationException` | Bad arguments (non-positive amount, missing field, malformed key id). | Fix the call; nothing was sent. |

Failures from a session create also carry `IdempotencyKey`, so a log line tells you which key to
reuse.

Refusal codes on `DominaiteRefusalException`:

- `PAYMENT_PROCESSING_UNAVAILABLE` - card payments are off right now; retry later.
- `DUPLICATE_REQUEST` - a session for this idempotency key is already open.
- `ALREADY_PROCESSED` - this idempotency key's payment already completed.
- `PRIOR_ATTEMPT_FAILED` - the earlier attempt with this key failed; use a fresh key.
- `IDEMPOTENCY_KEY_REUSED` - same key sent with a different body; use a fresh key.

All five arrive as HTTP 200 with `success: false`, not as an HTTP error status.

## Refunds, captures and voids

They are issued from the Dominaite dashboard by design - one audited, human-confirmed path for
money moving back. The merchant API is deliberately create-and-read; do not build refund automation
against it. If your order flow cancels an order, record it on your side and issue the refund from
the dashboard.

## The three identifiers

- `TransactionId` - Dominaite's payment id. Store it, poll status with it.
- `OrderReference` - your own id, echoed back. This is what you search for in your dashboard, so
  put your order or cart id there.
- `OrderId` (`dom_...`) - the provider-facing correlation id. You never need it.

## License

MIT. See [LICENSE](LICENSE).
