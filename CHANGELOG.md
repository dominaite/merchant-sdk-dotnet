# Changelog

## 0.7.0 (unreleased)

### Breaking

- The client retries by itself. `DominaiteClientOptions.MaxRetries` defaults to 2, so a GET or a
  POST with an idempotency key that fails with a transport error, a 5xx with no typed meaning on
  the route or `PAYMENT_PROCESSING_UNAVAILABLE` is sent up to 3 times. Set `MaxRetries = 0` to
  keep the old one-send behavior. A 429, a PATCH, a DELETE and `CreateWebhookEndpointAsync` are
  never retried.
- `Timeout` is per attempt. With retries, one call can take up to (`MaxRetries` + 1) x `Timeout`,
  plus the waits between attempts (each up to 500ms x 2^n, capped at 8 seconds).
- A coded charge 404 (for example `PAYMENT_METHOD_NOT_FOUND`) is now a
  `DominaiteChargeException`, not a `DominaiteApiException`. Both still derive from
  `DominaiteException`, so a catch on the base type is unchanged.
- A charge or revoke 5xx whose code is not in `ChargeErrorCodes.All` or `RevokeErrorCodes.All`
  (for example `INTERNAL_ERROR`) is now a `DominaiteTransportException` that keeps the code. It was
  a `DominaiteChargeException` or a `DominaiteRevokeException`.
- `DominaiteChargeException.IsRetryable` is now true for `DUPLICATE_REQUEST`, as on refunds.
- These now fail before anything is sent, with `DominaiteValidationException`: a currency that is
  not three upper-case letters, a `ListTransactionsAsync` window with `From` or `To` unset, a
  `CreatePlanAsync` amount of 0 or less, and a session `Consent` without `AcceptedAt`.

### Added

- `RequestId` on every result and on `DominaiteException`: the `Dominaite-Request-Id` header, else
  the body's `metadata.requestId`. The debug line uses the same fallback.
- `IdempotencyKey` is now set on the errors of every call that sends one, not only session create.
- `ChargeEventData.PaymentMethod` (`Brand`, `Last4`) on `charge.*` webhook events. A value with an
  unexpected shape reads as null and never fails a signed event; `Raw` keeps it as sent.
- Error-code constants for every code the gateway names: `AuthErrorCodes`,
  `SubscriptionErrorCodes`, `WebhookEndpointErrorCodes`, `ValidationErrorCodes`,
  `ErrorCodes.Idempotency`, `ErrorCodes.Transient`, the subscription session refusals in
  `ErrorCodes.SessionRefusals`, and the agreement, cap and in-progress codes in
  `ChargeErrorCodes.All`. New codes are appended, so the existing order is unchanged.

### Changed

- A stored payment method id is lowercased before it is signed and sent.
- A correctly signed webhook event whose `data` does not map to the typed model (for example
  `"periodNumber":"two"`) now verifies. `Payment`, `Agreement` or `Charge` is null and `Data` keeps
  the data as sent. It threw `DominaiteWebhookException` with `MalformedPayload` before. A body
  that is not JSON, or has no `id` or `type`, still throws.

### Deprecated

- `CreateCheckoutSessionWithRetryAsync` and `RetryOptions`. Use `CreateCheckoutSessionAsync` and
  `MaxRetries`. The method still works as a thin wrapper over the client's policy: its
  `Attempts` and `BaseDelay` replace the client's values for that call, and each wait is now a
  random time up to the doubling ceiling.

## 0.6.0

### Breaking

- The base URL is required. The client no longer falls back to production when you leave it
  out, so an unset environment variable cannot send sandbox credentials to production.
  Construction fails with `DominaiteValidationException` when the base URL is missing, empty or
  whitespace. Pass `DominaiteClient.SandboxBaseUrl` (`https://api.staging.dominaite.com/payments`)
  or `DominaiteClient.ProductionBaseUrl` (`https://api.dominaite.com/payments`), or your own
  value.
- `DominaiteClient.DefaultBaseUrl` is removed. Use `DominaiteClient.ProductionBaseUrl`.
- The base URL must use `https://`. Plain `http://` is accepted only for localhost, 127.0.0.1
  and ::1, so a local mock server still works. Anything else fails construction with
  `DominaiteValidationException`.

### Added

- The User-Agent is now `dominaite-dotnet/<version> (dotnet <runtime version>)`, then
  `UserAgentSuffix` after one space. No operating system. A suffix with a control character
  fails construction with `DominaiteValidationException`.
- `DominaiteClientOptions.OnWarning`: an `Action<string>` for the SDK's warnings. It defaults to
  `Trace.TraceWarning`. It gets the API's `Dominaite-Sdk-Notice` header (once per distinct
  value), each retry in `CreateCheckoutSessionWithRetryAsync`, and a ping clock skew over 60
  seconds.
- `DominaiteClientOptions.OnDebug`: turns on a debug log, one line per request with the method,
  the signed path, the status, the time, the `Dominaite-Request-Id` response header and the
  idempotency key. Off by default.
- No warning or debug line holds the secret, a signature, a cashier token, a query string or a
  body. An exception thrown by a callback is dropped.

## 0.5.2

### Added

- Billing address on the payer: `Customer.Address`, `City`, `PostalCode` and `State`, all
  optional. Address is one line, at most 200 characters; city at most 100; postal code at most
  20. State is the state or province code, at most 50 characters, and only used for the United
  States, Canada and Australia. The widget does not ask the payer for a field that you send. A
  value that is too long fails with 400 `VALIDATION_ERROR`, code `MAX_LENGTH`.

## 0.5.1

### Added

- `TransactionItem` gains `Fee`, `NetAmount`, `SettledAt` and `PayoutId`. They are null until the
  acquirer settles the payment. `Fee` and `NetAmount` are in minor units, like `Amount`.
  `NetAmount` is the amount minus the fee of this payment only, not the deductions on the payout.
- `ListTransactionsRequest.SettledFrom` and `SettledTo` (`DateOnly`): a settlement window, sent
  as `yyyy-MM-dd` query parameters. Set both or neither. `From` and `To` still apply.

## 0.5.0

Breaking: a 403 now throws `DominaitePermissionException` (it was `DominaiteAuthException`), and
a 429 now throws `DominaiteRateLimitException` (it was `DominaiteApiException`). Catch the new
types where you caught the old ones for these statuses.

### Added

- Verify-only sessions: `CheckoutSessionRequest.VerifyOnly` saves the payer's card with no
  payment. Leave `Amount` at 0; the SDK refuses any other amount before sending. Verify-only is
  off by default. We switch it on for your account. Until then the create call throws
  `DominaitePermissionException` with `VERIFY_ONLY_NOT_ENABLED`.
- Webhook events `verification.succeeded` and `verification.failed`
  (`WebhookEventTypes.VerificationSucceeded` and `VerificationFailed`). A verify-only session
  sends them instead of `payment.*` events. Their data is typed as `WebhookEvent.Payment`.
  `PaymentEventData` gains `VerifyOnly`, and `DeclineClass` for `payment.failed` and
  `verification.failed`.
- Subscriptions: `CreatePlanAsync`, `ListPlansAsync`, `GetPlanAsync`, `UpdatePlanAsync` and
  `DeletePlanAsync`, and `CreateAgreementAsync`, `GetAgreementAsync` and `CancelAgreementAsync`.
  New `Plan`, `Agreement`, `AgreementConsentRecord`, `CreatePlanRequest`, `UpdatePlanRequest`,
  `CreateAgreementRequest`, `IntervalUnits`, `PlanStatuses` and `PastDueReasons`. The create
  calls need an idempotency key. `UpdatePlanAsync` sends none, because the API does not replay a
  plan update.
- Subscription sessions: `CheckoutSessionRequest.AgreementId` and `Consent` (`AgreementConsent`),
  and also `LocationId`.
- `ListRefundsAsync(transactionId)`: the refunds of one payment that you made through the API,
  with the `RefundedAmount` and `RefundableAmount` totals (`RefundList`).
- Webhook endpoints from your server, with a `webhooks:manage` key: `CreateWebhookEndpointAsync`,
  `ListWebhookEndpointsAsync` and `DeleteWebhookEndpointAsync`. Only the first create answer has
  the `whsec_` secret; a repeat with the same key has none. New `WebhookEndpoint`,
  `CreateWebhookEndpointRequest` and `WebhookEndpointStatuses`.
- `ListTransactionsAsync(ListTransactionsRequest)`, with a `transactions:read` key: one page of
  your payments in a window of at most 31 days, oldest first, and the `NextCursor` of the next
  page. The SDK signs the path without the query. New `TransactionPage`, `TransactionItem` and
  `TransactionTypes`.
- `GetIntegrationContractAsync()`: the contract that the API publishes about itself
  (`IntegrationContract`). It models the version, the API base, the status and event values and
  the subscription values. `Raw` has the whole contract.
- Response fields: `DeclineClass`, `IsRecurring`, `VerifyOnly`, `Agreement`
  (`CheckoutStatusAgreement`) and `Sequence` on `CheckoutStatus`; `VerifyOnly` and `AgreementId`
  on `CheckoutSession`; `AgreementId` and `Sequence` on `PaymentMethodCharge`. Every status,
  class and reason stays a plain string, so a value the SDK does not know never throws.
- `ChargeRequest.AgreementId`, and `Extra` on `ChargeRequest` and `RefundRequest`. Every request
  with a body now takes fields that the SDK does not model yet.
- `DominaiteApiException.RawResult`: the whole JSON answer, for example the field errors of a 400
  or `error.params.webhookId` on 409 `WEBHOOK_ALREADY_REGISTERED`.
- `DominaitePermissionException` and `PermissionErrorCodes` (`INSUFFICIENT_SCOPE`,
  `VERIFY_ONLY_NOT_ENABLED`). `DominaiteRateLimitException` and `RateLimitErrorCodes`
  (`RATE_LIMIT_EXCEEDED`, `CHARGE_VELOCITY_EXCEEDED`, `CHARGE_DECLINE_RATE_EXCEEDED`), with
  `RetryAfter` from the `Retry-After` header.
- Wire contract fixture regenerated from the live contract, with the authorization error group.
- Contract fixture: the shared 0.5.0 file. The session, status read and charge fields include the
  new fields, and every example carries them.
- Wallet reporting: `CheckoutStatus.PaymentMethod` (`card`, `wallet`, `bank_transfer` or `sepa`)
  and `CheckoutStatus.WalletType` (`apple_pay`, `google_pay`, `samsung_pay`, or a lower-cased
  identifier for a wallet not listed yet). Both are nullable: null while the payment is open and
  on older transactions, and `WalletType` is null for non-wallet payments. Reporting data only, a
  wallet payment refunds, captures and disputes like a card.
- `PaymentMethodCategories` and `WalletTypes` constants, each enumerable as `All`.
- Contract fixtures: `paymentMethod` and `walletType` in the status read fields and examples, and
  the `wallets` section in the wire contract.

### Changed

- A 403 no longer tells you to check your key id, secret and server clock. That hint is for 401
  only. `INSUFFICIENT_SCOPE` names the missing scope, and `VERIFY_ONLY_NOT_ENABLED` says that we
  switch it on for your account.
- `DeclineClass` can be null, for example when nobody declined the payment. The docs no longer
  promise a class on every failure.
- Dates in a request body are written in UTC with a `Z`.
- The README documents key scopes, verify-only sessions, subscriptions, the refund list, webhook
  endpoints, transactions and the integration contract.

## 0.4.0

- Processor reference: `CheckoutStatus.PspReference` and `PaymentEventData.PspReference`. The
  payment processor's reference for the transaction, null until known and on a sale settled by
  reconciliation without a processor webhook. Refund and cancel events carry the original sale's
  reference. Not on `charge.*` events.
- Contract fixture: `pspReference` in the status read fields and examples.
- Card fields: `CheckoutSessionRequest.Integration` (`CheckoutIntegrations.Widget` or
  `CheckoutIntegrations.Fields`, omitted when null), and `CheckoutSession.Integration` and
  `CheckoutSession.ClientSecret` on the response. `ClientSecret` is set only for fields sessions
  and is redacted from `ToString()`. Card fields are enabled per merchant on request; see the
  README.
- Contract fixture: `integrationVocabulary`, the two new checkout fields and a fields session
  example.

## 0.3.1

- New `Webhooks.VerifyAndParse(payload, signatureHeader, secret, ...)` verifies a delivery and
  then parses it into a `WebhookEvent` (`Id`, `Type`, `ApiVersion`, `CreatedAt`, `Data`), with
  `agreement.*` and `charge.*` data typed as `AgreementEventData` and `ChargeEventData`. A body
  that verifies but is not a readable envelope throws with the new
  `WebhookFailureReason.MalformedPayload`. Event type constants on `WebhookEventTypes`.
- Webhook envelopes carry `apiVersion` (currently `2026-09-25`), and `agreement.*` and `charge.*`
  data carry `sequence`. Both are optional: payloads from servers without them still parse, as
  null. Order those events by `Sequence` per `OrderingKey`, never by `createdAt`; see Ordering in
  the README. Signature verification is unchanged.
- Refunds: new `CreateRefundAsync(transactionId, RefundRequest)` and
  `GetRefundAsync(transactionId, refundId)`. The idempotency key is required and signed like a
  charge; leave `Amount` null to refund everything still refundable (no `amount` is sent). The
  create call answers once the refund is queued; poll `GetRefundAsync` or wait for
  `payment.refunded`. New `Refund`, `RefundRequest`, `RefundStatuses`, `RefundErrorCodes`,
  `RefundFailureCodes` and `DominaiteRefundException` (`DUPLICATE_REQUEST` and
  `REFUND_NOT_FOUND` are retryable). A failed refund is a result with a `FailureCode`, and fires
  no webhook.
- `payment.*` webhook data is typed as `PaymentEventData` on `WebhookEvent.Payment`, including
  `StoredPaymentMethod`: the same object as on the status read, null or absent when no card was
  saved. It can be null even when a card was saved; the status read is the source of truth.

## 0.3.0

Breaking. To migrate: set `IdempotencyKey` on every `CheckoutSessionRequest` and `ChargeRequest`,
built from the order with `IdempotencyKeys.ForOrder("checkout", orderId, amountMinor, currency)`
(scope `"charge"` for charges), and stop calling `DominaiteClient.NewIdempotencyKey`.

- The idempotency key is required on `CreateCheckoutSessionAsync`,
  `CreateCheckoutSessionWithRetryAsync` and `ChargePaymentMethodAsync`. A missing or blank key
  throws `DominaiteValidationException` before anything is sent; the SDK no longer makes up a
  random one. `NewIdempotencyKey` is removed.
- New `IdempotencyKeys.ForOrder(scope, orderId, amountMinor, currency)` builds
  `{scope}-{orderId}-{amountMinor}-{CURRENCY}`: the same order and amount replays the same session,
  a changed amount gets a new key.
- Keys must be 1 to 100 characters of visible ASCII (0x21 to 0x7E).
- New `ErrorCodes` constants, including `STOREFRONT_NOT_WHITELISTED`, `STOREFRONT_INACTIVE` and
  `STOREFRONT_MISMATCH`. Storefront refusals on session create throw the new
  `DominaiteStorefrontException` instead of `DominaiteApiException` / `DominaiteRefusalException`.
- `PAYMENT_PROCESSING_UNAVAILABLE` is retryable (`IsRetryable` is true) and
  `CreateCheckoutSessionWithRetryAsync` retries it with the same key, as a 200 refusal and as a
  503. A coded 5xx keeps its code on `DominaiteTransportException.Code`.
- `CheckoutStatus.IsTerminal` is false for `disputed`: a dispute resolves later.
- New `MinorUnits.From(decimal, currency)` and `MinorUnits.Exponent(currency)`, using the
  gateway's exponents: HUF is 0 (whole forints, not ISO 4217's 2); ISK, KRW, OMR, JOD and TND are
  refused as not supported.
- Also first released here: stored payment methods (`SaveCard`, `ChargePaymentMethodAsync`,
  `RevokePaymentMethodAsync`), merged earlier but never published.
- Saved cards can be `retired`: the platform stopped the card on its own and it never becomes
  active again. New `StoredPaymentMethodStatuses.Retired`, `StoredPaymentMethod.RetiredReason`
  (null unless retired) and `StoredPaymentMethodRetiredReasons` (`hard_decline`, `chargeback`,
  `source_sale_reversed`).
- `ErrorCodes.Storefront` is now in the contract's order: `STOREFRONT_MISMATCH`,
  `STOREFRONT_INACTIVE`, `STOREFRONT_NOT_WHITELISTED`.
- Contract fixtures refreshed from the gateway (contract version 2026-09-16).

## 0.2.0

First NuGet release.
