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
`.../api/merchant-api/checkout/sessions` but you sign
`/merchant-api/checkout/sessions`.

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

    // Required. Same order + same amount = same key, so a reload or a retry replays this session.
    IdempotencyKey = IdempotencyKeys.ForOrder("checkout", "order-1042", 2500, "EUR"),
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
    Path = "/merchant-api/checkout/sessions",       // path only, no host
    IdempotencyKey = "00000000-0000-4000-8000-000000000001",   // "" for GET
    Body = """{"amount":2500,"currency":"EUR","orderReference":"order-1042"}""", // "" for GET
});
// "8f5fba0b29a8eea81b76a0e6d7119e79ec68f586910f77713b045652e5ce9b74"
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

Every `CreateCheckoutSessionAsync` and `ChargePaymentMethodAsync` call needs an idempotency key,
and the SDK never makes one up: a null or blank `IdempotencyKey` throws
`DominaiteValidationException` before anything is sent. Derive the key from the order:

```csharp
var key = IdempotencyKeys.ForOrder("checkout", order.Id, amountMinor, "EUR");
// "checkout-order-1042-2500-EUR"
```

The key is `{scope}-{orderId}-{amountMinor}-{CURRENCY}`. The same order at the same amount always
gives the same key, so a page reload, a back button or a retried POST replays the session that
already exists instead of opening a second payment. A changed amount or currency gives a new key,
which is what the gateway wants: a reused key with a different amount is refused as
`IDEMPOTENCY_KEY_REUSED`. Use a different scope per kind of call (`checkout` for sessions,
`charge` for stored-card charges). The key is checked against the same rules as one you build
yourself: at most 100 characters, no control characters.

`CreateCheckoutSessionWithRetryAsync` sends the request's key on every attempt,
retrying transport failures (network errors, timeouts, and 5xx including
`MERCHANT_API_UNAVAILABLE`) and `PAYMENT_PROCESSING_UNAVAILABLE`, which means card payments are
off for a moment and nothing was created. Every other refusal, storefront refusals and
authentication failures are thrown immediately - they will not change. `IsRetryable` on any
`DominaiteException` gives you the same answer for your own retry loop.

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

One replay is not a refusal at all. A session that expired unpaid is superseded: from a few
minutes past its expiry, re-POSTing the same key returns an ordinary success with a fresh session
(new `TransactionId`, same key), so a customer who comes back late just pays. Keep the
order-derived key for the life of the order to keep that path open. The band is not endless - once
the platform has independently closed the attempt (about an hour past expiry), the replay answers
`PRIOR_ATTEMPT_FAILED` and the key is spent; reconcile with `GetStatusAsync` and use a fresh key.

**Cart changed = new session, new idempotency key.** There is no session-update call: if the
order's amount or contents change after a session exists, abandon it and mint a fresh session with
a fresh key. Reusing the old key with a new amount is rejected as `IDEMPOTENCY_KEY_REUSED` by
design.

## Sessions expire

A session is valid for about 2 hours. If the payer comes back later, create a new one. Before
re-rendering the widget for a stored session, read the status first: a completed session's widget
shows "session is closed or expired", which reads as an error to someone who just paid.

## Stored payment methods (recurring)

A session can ask the payer to save their card for later. Set `SaveCard = true` on the request;
nothing else about the session changes, and a request that never sets it sends the exact same
bytes as before (null is omitted, not sent as `false`).

```csharp
var session = await client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
{
    Amount = 2500,
    Currency = "EUR",
    OrderReference = "order-1042",
    SaveCard = true,
    IdempotencyKey = IdempotencyKeys.ForOrder("checkout", "order-1042", 2500, "EUR"),
});
```

Once that session is paid, `GetStatusAsync` carries a `StoredPaymentMethod`: an id, the brand,
the last four digits, the expiry and a status (`active`, `revoked` or `expired`). Persist the id
against your customer. The full card number never reaches the SDK, and the provider token behind
the id never leaves the gateway. `Brand`, `Last4` and the expiry are nullable: the gateway omits
them when the provider did not report them. This is not the gateway's `paymentMethod` field (the
string category of how the payer paid), which stays on `Raw`.

```csharp
var status = await client.GetStatusAsync(session.TransactionId);
if (status.StoredPaymentMethod is { IsChargeable: true } method)
{
    await StoreForCustomerAsync(customerId, method.Id); // pm_...
}
```

Charge the stored card later, off-session, with `ChargePaymentMethodAsync`. The call takes the
same amount, currency and order reference as a session, and an idempotency key that is required
and signed exactly like `CreateCheckoutSessionAsync`. Send the same key when you retry.

```csharp
try
{
    var charge = await client.ChargePaymentMethodAsync(methodId, new ChargeRequest
    {
        Amount = 2500,
        Currency = "EUR",
        OrderReference = "order-1043",
        Description = "Monthly plan",
        IdempotencyKey = IdempotencyKeys.ForOrder("charge", "order-1043", 2500, "EUR"),
    });

    switch (charge.Status)
    {
        case ChargeStatuses.Succeeded:
            await MarkPaidAsync(charge.TransactionId);
            break;
        case ChargeStatuses.Pending:
            await PollLaterAsync(charge.TransactionId); // not terminal
            break;
        case ChargeStatuses.Cancelled:
            break; // nothing moved
        default:
            switch (charge.DeclineClass)
            {
                case DeclineClasses.Hard: await StopChargingAsync(methodId); break;   // never retry
                case DeclineClasses.SoftFunds: await RetryInAFewDaysAsync(); break;
                case DeclineClasses.SoftScaRequired: await BringThePayerBackAsync(); break; // needs a session
                default: await RetryLaterAsync(); break;                              // soft_other
            }

            break;
    }
}
catch (DominaiteChargeException error)
{
    switch (error.Code)
    {
        // The provider gave no verdict: the charge MAY have happened. Poll the transaction
        // (or wait for the webhook); never retry under a new key.
        case ChargeErrorCodes.ChargeOutcomeUnknown:
            await PollLaterAsync(error.TransactionId!);
            break;
        case ChargeErrorCodes.PaymentMethodNotActive:
            await AskForAnotherCardAsync();
            break;
        // Nothing was charged; retry later with the SAME key (error.IdempotencyKey).
        // error.IsRetryable is true for PAYMENT_PROCESSING_UNAVAILABLE.
        case ChargeErrorCodes.DuplicateRequest:
        case ChargeErrorCodes.PaymentMethodChargesDisabled:
        case ChargeErrorCodes.PaymentProcessingUnavailable:
            await RetryLaterAsync();
            break;
        default: // CHARGE_FAILED, IDEMPOTENCY_KEY_REUSED
            throw;
    }
}
```

`ChargePaymentMethodAsync` returns for HTTP 201 and for HTTP 402 alike. A decline is a result,
not an exception: the 402 charge has `Status == "failed"` and a `DeclineClass` (`hard`,
`soft_funds`, `soft_sca_required` or `soft_other`) plus the provider's `DeclineCode`. `IsPaid` and
`IsTerminal` read the same way they do on a session status; `pending` is the one status that is
not terminal.

`DominaiteChargeException` is the gateway answering with an error code instead of a charge: HTTP
409 (`PAYMENT_METHOD_NOT_ACTIVE`, `DUPLICATE_REQUEST`), 422 (`IDEMPOTENCY_KEY_REUSED`), 502
(`CHARGE_OUTCOME_UNKNOWN`, `CHARGE_FAILED`) or 503 (`PAYMENT_METHOD_CHARGES_DISABLED`,
`PAYMENT_PROCESSING_UNAVAILABLE`). The exception keeps `HttpStatus`, `Code`, the gateway's
message, the `IdempotencyKey` to reuse, and the charge row on `Charge` when the gateway attached
one (always for `CHARGE_OUTCOME_UNKNOWN`, whose `TransactionId` is what you poll). A 404 is a
method id that is not yours and stays the plain `DominaiteApiException` with code
`PAYMENT_METHOD_NOT_FOUND`; a 5xx without a gateway code (an HTML page from a proxy) stays
`DominaiteTransportException`.

`RevokePaymentMethodAsync` drops the card: the gateway deletes the saved credential at the
provider and marks the method `revoked`, and any later charge on it is refused with
`PAYMENT_METHOD_NOT_ACTIVE`. It is a signed `DELETE` with an empty key and an empty body (the same
recipe as GET) and resolves on HTTP 204, again on an already revoked method. When the gateway
refuses, nothing changed and you get `DominaiteRevokeException` with the code:
`MERCHANT_API_UNAVAILABLE` (503, retry later) or `UPSTREAM_CONTRACT_ERROR` (502, contact support
with the id). A 404 is the plain `DominaiteApiException` with code `VALIDATION_ERROR`.

```csharp
try
{
    await client.RevokePaymentMethodAsync(methodId);
}
catch (DominaiteRevokeException error) when (error.Code == RevokeErrorCodes.MerchantApiUnavailable)
{
    await RetryLaterAsync();
}
```

Both routes are pinned by known-answer vectors in `SigningVectorTests` next to the session ones,
shared byte-for-byte with the gateway: the charge vector signs
`POST /merchant-api/payment-methods/pm_0123456789abcdef0123456789abcdef/charges` with key
`00000000-0000-4000-8000-000000000003` and body
`{"amount":2500,"currency":"EUR","orderReference":"order-1043"}`, the revoke vector signs the
`DELETE` with nothing else.

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
| `DominaiteStorefrontException` | Session create refused because of the storefront: `STOREFRONT_NOT_WHITELISTED` or `STOREFRONT_INACTIVE` (409), `STOREFRONT_MISMATCH` (400, or 200 on a replay). Carries `TransactionId` on a replay and `RawResult`. | Configuration, not a retry. See [Storefront errors](#storefront-errors). |
| `DominaiteAuthException` | 401/403. `Code` is `INVALID_API_KEY`, `INVALID_SIGNATURE`, `TIMESTAMP_OUT_OF_RANGE`, or `IP_NOT_ALLOWED`. | Fix the key id, secret, server clock, or allowlist. Never retry-loop. |
| `DominaiteTransportException` | Network failure, timeout, or a 5xx on session create, status or ping, including one whose body is an HTML error page from a proxy. `Code` keeps the gateway's code when the 5xx carried one. | Retry with the **same** idempotency key. `IsRetryable` is always true here, and elsewhere only for `PAYMENT_PROCESSING_UNAVAILABLE`. |
| `DominaiteChargeException` | `ChargePaymentMethodAsync` got an error code instead of a charge: 409, 422, 502 or 503. Carries `Charge`, `TransactionId` and `RawResult`. | Branch on `Code` (see [Stored payment methods](#stored-payment-methods-recurring)). `CHARGE_OUTCOME_UNKNOWN` carries the `TransactionId` to poll; never retry it under a new key. |
| `DominaiteRevokeException` | `RevokePaymentMethodAsync` was refused: 502 `UPSTREAM_CONTRACT_ERROR` or 503 `MERCHANT_API_UNAVAILABLE`. Nothing changed. | Retry later on 503; contact support on 502. |
| `DominaiteApiException` | Any other rejecting or unexpected response, including a 3xx. `Code` carries the API's reason when it sent one, e.g. `IDEMPOTENCY_KEY_REQUIRED` on a 400, `PAYMENT_METHOD_NOT_FOUND` on a charge 404. | Inspect `HttpStatus` and `Code`. A 404 from `GetStatusAsync` is an unknown transaction id. |
| `DominaiteValidationException` | Bad arguments (non-positive amount, missing field, missing idempotency key, malformed key id). | Fix the call; nothing was sent. |

Failures from a session create also carry `IdempotencyKey`, so a log line tells you which key to
reuse.

Refusal codes on `DominaiteRefusalException`:

- `PAYMENT_PROCESSING_UNAVAILABLE` - card payments are off right now; nothing was created. Retry
  with the same key (`IsRetryable` is true, and the retry helper does it for you).
- `DUPLICATE_REQUEST` - a session for this idempotency key is already open, or expired within the
  last few minutes. Re-POST the same key shortly, never a fresh one.
- `ALREADY_PROCESSED` - this idempotency key's payment already completed.
- `PRIOR_ATTEMPT_FAILED` - the earlier attempt with this key failed; use a fresh key.
- `IDEMPOTENCY_KEY_REUSED` - same key sent with a different body; use a fresh key.

All five arrive as HTTP 200 with `success: false`, not as an HTTP error status. Every code has a
constant on `ErrorCodes` (`ErrorCodes.AlreadyProcessed`, `ErrorCodes.DuplicateRequest`, ...), so
branch on those rather than on string literals.

### Storefront errors

A storefront is one website under your merchant account. When a session is attributed to one,
the gateway can refuse it before anything is created:

- `STOREFRONT_NOT_WHITELISTED` (HTTP 409) - the site's domain is not whitelisted with the payment
  provider yet. Ask Dominaite support to finish the whitelisting; retrying will not help.
- `STOREFRONT_INACTIVE` (HTTP 409) - the storefront was deactivated or deleted.
- `STOREFRONT_MISMATCH` (HTTP 400) - the API key is bound to a different storefront than the one
  the request names. An idempotent replay says the same thing as HTTP 200 with `success: false`.

All three arrive as `DominaiteStorefrontException`, whatever the status, and the retry helper
never retries them:

```csharp
catch (DominaiteStorefrontException error) when (error.Code == ErrorCodes.StorefrontNotWhitelisted)
{
    // Show "payments are not available on this site yet" and alert your ops channel.
}
```

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
