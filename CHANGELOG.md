# Changelog

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
