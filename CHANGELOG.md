# Changelog

## 1.0.0

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
  `RevokePaymentMethodAsync`), which were versioned 0.3.0 but never published.

## 0.2.0

First NuGet release.
