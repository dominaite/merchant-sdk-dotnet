# Changelog

## 0.3.1 (unreleased)

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
