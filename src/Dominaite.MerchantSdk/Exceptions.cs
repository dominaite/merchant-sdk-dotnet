using System.Text.Json;

namespace Dominaite.MerchantSdk;

/// <summary>
/// The base of the SDK's error taxonomy: what happened, and whether retrying can help.
/// </summary>
/// <remarks>
/// Catch the derived type rather than matching on the message. Every subclass that carries a
/// machine-readable reason exposes it on <see cref="Code"/>.
/// </remarks>
public abstract class DominaiteException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteException"/> class.</summary>
    /// <param name="message">The human-readable message.</param>
    /// <param name="innerException">The underlying cause, when there was one.</param>
    protected DominaiteException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// The machine-readable reason, when the API sent one. Null for errors this SDK raises
    /// itself.
    /// </summary>
    public string? Code { get; protected init; }

    /// <summary>The HTTP status, for the errors that carry one.</summary>
    public int? HttpStatus { get; protected init; }

    /// <summary>
    /// The idempotency key the failed call used, set on session-create failures. Reuse this key
    /// when you retry: a fresh key on a retry is the double-charge bug.
    /// </summary>
    public string? IdempotencyKey { get; internal set; }

    /// <summary>
    /// True when retrying can help, and only with the SAME idempotency key: every
    /// <see cref="DominaiteTransportException"/>, and any answer coded
    /// <c>PAYMENT_PROCESSING_UNAVAILABLE</c> (a 200 refusal on session create, a 503 on a
    /// stored-card charge), since card payments come back on their own.
    /// </summary>
    public virtual bool IsRetryable
        => string.Equals(this.Code, ErrorCodes.PaymentProcessingUnavailable, StringComparison.Ordinal);
}

/// <summary>
/// The SDK rejected the call before sending anything. Nothing reached the network; fix the
/// arguments.
/// </summary>
public sealed class DominaiteValidationException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteValidationException"/> class.</summary>
    /// <param name="message">What was wrong with the call.</param>
    public DominaiteValidationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The API understood the request and refused it: HTTP 200 with <c>success: false</c>. The
/// replay codes arrive this way too (<c>DUPLICATE_REQUEST</c>, <c>ALREADY_PROCESSED</c>,
/// <c>PRIOR_ATTEMPT_FAILED</c>, <c>IDEMPOTENCY_KEY_REUSED</c>).
/// </summary>
/// <remarks>
/// Never blind-retry a refusal: it will not change on its own. The one exception is
/// <c>PAYMENT_PROCESSING_UNAVAILABLE</c>, which reports <see cref="DominaiteException.IsRetryable"/>
/// and which <see cref="DominaiteClient.CreateCheckoutSessionWithRetryAsync"/> retries with the
/// same key.
/// </remarks>
public sealed class DominaiteRefusalException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteRefusalException"/> class.</summary>
    /// <param name="code">The machine-readable refusal code.</param>
    /// <param name="message">The human-readable refusal message from the API.</param>
    /// <param name="transactionId">The payment the idempotency key collided with, when named.</param>
    /// <param name="rawResult">The unwrapped API payload, as received.</param>
    public DominaiteRefusalException(
        string code,
        string message,
        string? transactionId,
        JsonElement rawResult)
        : base(message)
    {
        this.Code = code;
        this.TransactionId = transactionId;
        this.RawResult = rawResult;
    }

    /// <summary>
    /// The payment this idempotency key collided with, when the API named one. That is the
    /// recovery path for a replay refusal: read it back with
    /// <see cref="DominaiteClient.GetStatusAsync"/> to find out what the earlier attempt did,
    /// instead of minting a second payment for the same order.
    /// </summary>
    /// <remarks>
    /// Null when the API did not name one - notably the concurrent-race
    /// <c>DUPLICATE_REQUEST</c>, which knows a key was taken but not yet by which row.
    /// </remarks>
    public string? TransactionId { get; }

    /// <summary>
    /// The unwrapped API payload exactly as received, for fields the typed surface does not
    /// model yet.
    /// </summary>
    public JsonElement RawResult { get; }
}

/// <summary>
/// The API rejected your credentials or signature (HTTP 401/403). Not retryable: fix the key id,
/// the secret, the server clock, or the caller allowlist.
/// </summary>
public sealed class DominaiteAuthException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteAuthException"/> class.</summary>
    /// <param name="code">One of INVALID_API_KEY, INVALID_SIGNATURE, TIMESTAMP_OUT_OF_RANGE, IP_NOT_ALLOWED.</param>
    /// <param name="message">The human-readable reason.</param>
    /// <param name="httpStatus">The HTTP status that carried it.</param>
    public DominaiteAuthException(string code, string message, int httpStatus)
        : base(message)
    {
        this.Code = code;
        this.HttpStatus = httpStatus;
    }
}

/// <summary>
/// The API answered, but with a rejecting or unexpected response. A 400 carries a validation
/// code such as <c>IDEMPOTENCY_KEY_REQUIRED</c>; a 404 from <see cref="DominaiteClient.GetStatusAsync"/>
/// means an unknown transaction id, and from the payment method routes an id that is not yours
/// (<c>PAYMENT_METHOD_NOT_FOUND</c> on a charge, <c>VALIDATION_ERROR</c> on a revoke).
/// </summary>
public sealed class DominaiteApiException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteApiException"/> class.</summary>
    /// <param name="httpStatus">The HTTP status code.</param>
    /// <param name="code">The machine-readable reason when the API sent one, otherwise null.</param>
    /// <param name="message">The human-readable reason.</param>
    public DominaiteApiException(int httpStatus, string? code, string message)
        : base(message)
    {
        this.HttpStatus = httpStatus;
        this.Code = code;
    }
}

/// <summary>
/// The gateway codes a checkout integration branches on, as constants. The stored-card codes live
/// in <see cref="ChargeErrorCodes"/> and <see cref="RevokeErrorCodes"/>, the refund codes in
/// <see cref="RefundErrorCodes"/>.
/// </summary>
public static class ErrorCodes
{
    /// <summary>
    /// HTTP 409 on session create: the storefront's domain is not whitelisted with the payment
    /// provider yet. Nothing was created. Not retryable until the whitelisting is done; arrives as
    /// <see cref="DominaiteStorefrontException"/>.
    /// </summary>
    public const string StorefrontNotWhitelisted = "STOREFRONT_NOT_WHITELISTED";

    /// <summary>
    /// HTTP 409 on session create: the storefront was deactivated or deleted. Arrives as
    /// <see cref="DominaiteStorefrontException"/>.
    /// </summary>
    public const string StorefrontInactive = "STOREFRONT_INACTIVE";

    /// <summary>
    /// HTTP 400 on session create: the API key is bound to one storefront and the request named
    /// another. Arrives as <see cref="DominaiteStorefrontException"/>.
    /// </summary>
    public const string StorefrontMismatch = "STOREFRONT_MISMATCH";

    /// <summary>
    /// Session refusal: this idempotency key's payment already moved money (paid, refunded,
    /// disputed, or held awaiting capture). Read it back with the named transaction.
    /// </summary>
    public const string AlreadyProcessed = "ALREADY_PROCESSED";

    /// <summary>Session refusal: the earlier attempt with this key ended failed, cancelled or abandoned. Use a fresh key.</summary>
    public const string PriorAttemptFailed = "PRIOR_ATTEMPT_FAILED";

    /// <summary>
    /// An attempt with this key is still unfinished but cannot be handed back right now (still
    /// being written, or in flight on a charge). Re-send the SAME key shortly, never a fresh one.
    /// An open session that can be handed back is not refused: the replay returns it.
    /// </summary>
    public const string DuplicateRequest = "DUPLICATE_REQUEST";

    /// <summary>
    /// Card payments are off right now; nothing was created or charged. HTTP 200 on session
    /// create, 503 on a stored-card charge. Retryable with the SAME key.
    /// </summary>
    public const string PaymentProcessingUnavailable = "PAYMENT_PROCESSING_UNAVAILABLE";

    /// <summary>The key was already used with a different body (amount, currency, storefront). Use a fresh key.</summary>
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";

    /// <summary>The session refusal codes, in the order the canonical contract lists them.</summary>
    public static IReadOnlyList<string> SessionRefusals { get; } =
    [
        PaymentProcessingUnavailable,
        DuplicateRequest,
        AlreadyProcessed,
        IdempotencyKeyReused,
        PriorAttemptFailed,
    ];

    /// <summary>
    /// The codes <see cref="DominaiteClient.CreateCheckoutSessionAsync"/> throws as a
    /// <see cref="DominaiteStorefrontException"/>, in the order the canonical contract lists them.
    /// None is retryable, and none is in <see cref="SessionRefusals"/>.
    /// </summary>
    public static IReadOnlyList<string> Storefront { get; } =
    [
        StorefrontMismatch,
        StorefrontInactive,
        StorefrontNotWhitelisted,
    ];
}

/// <summary>
/// The gateway refused a session because of the storefront (the online location the session is
/// attributed to): <c>STOREFRONT_NOT_WHITELISTED</c> and <c>STOREFRONT_INACTIVE</c> (HTTP 409) or
/// <c>STOREFRONT_MISMATCH</c> (HTTP 400, or 200 on an idempotent replay). Nothing was created.
/// </summary>
/// <remarks>
/// A retry will not help: this is configuration. Whitelisting the domain with the provider,
/// reactivating the storefront or using the key bound to the right storefront is what fixes it.
/// Branch on <see cref="DominaiteException.Code"/>, see <see cref="ErrorCodes.Storefront"/>.
/// </remarks>
public sealed class DominaiteStorefrontException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteStorefrontException"/> class.</summary>
    /// <param name="httpStatus">The HTTP status that carried the code.</param>
    /// <param name="code">One of the <see cref="ErrorCodes.Storefront"/> codes.</param>
    /// <param name="message">The human-readable reason from the API.</param>
    /// <param name="transactionId">The payment an idempotent replay collided with, when named.</param>
    /// <param name="rawResult">The whole envelope, as received.</param>
    public DominaiteStorefrontException(
        int httpStatus,
        string code,
        string message,
        string? transactionId,
        JsonElement rawResult)
        : base(message)
    {
        this.HttpStatus = httpStatus;
        this.Code = code;
        this.TransactionId = transactionId;
        this.RawResult = rawResult;
    }

    /// <summary>The payment the key collided with, on a replay refusal; null on a fresh mint.</summary>
    public string? TransactionId { get; }

    /// <summary>The whole envelope exactly as received, for fields the typed surface does not model.</summary>
    public JsonElement RawResult { get; }
}

/// <summary>
/// The codes <see cref="DominaiteClient.ChargePaymentMethodAsync"/> throws as a
/// <see cref="DominaiteChargeException"/>, in the gateway's own order. <c>CHARGE_DECLINED</c>
/// (HTTP 402) is deliberately not one of them: a decline is a charge result with status
/// <c>failed</c>, not an exception.
/// </summary>
public static class ChargeErrorCodes
{
    /// <summary>
    /// HTTP 409: the method is revoked, expired or retired; ask the customer for another card via
    /// a hosted session with SaveCard.
    /// </summary>
    public const string PaymentMethodNotActive = "PAYMENT_METHOD_NOT_ACTIVE";

    /// <summary>HTTP 409: a request with this key is still in flight; retry with the SAME key in a moment.</summary>
    public const string DuplicateRequest = "DUPLICATE_REQUEST";

    /// <summary>HTTP 422: same key, different body or method; a bug on your side.</summary>
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";

    /// <summary>
    /// HTTP 502: the provider gave no verdict and the charge MAY have happened. The charge row is
    /// attached: poll <see cref="DominaiteClient.GetStatusAsync"/> with its transaction id or
    /// wait for the webhook. Never retry under a new key.
    /// </summary>
    public const string ChargeOutcomeUnknown = "CHARGE_OUTCOME_UNKNOWN";

    /// <summary>
    /// HTTP 502: nothing was charged. The charge row is attached when one exists, absent when the
    /// provider refused before one.
    /// </summary>
    public const string ChargeFailed = "CHARGE_FAILED";

    /// <summary>HTTP 503: charges of stored methods are switched off; nothing was charged. Retry later with the SAME key.</summary>
    public const string PaymentMethodChargesDisabled = "PAYMENT_METHOD_CHARGES_DISABLED";

    /// <summary>HTTP 503: card payments are off right now; nothing was charged. Retry later with the SAME key.</summary>
    public const string PaymentProcessingUnavailable = "PAYMENT_PROCESSING_UNAVAILABLE";

    /// <summary>
    /// The whole vocabulary, in the order the canonical contract lists it. An unlisted code still
    /// arrives as a <see cref="DominaiteChargeException"/>.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        PaymentMethodNotActive,
        DuplicateRequest,
        IdempotencyKeyReused,
        ChargeOutcomeUnknown,
        ChargeFailed,
        PaymentMethodChargesDisabled,
        PaymentProcessingUnavailable,
    ];
}

/// <summary>
/// The gateway answered a charge with an error code instead of a charge result: HTTP 409, 422,
/// 502 or 503. Branch on <see cref="DominaiteException.Code"/>; see <see cref="ChargeErrorCodes"/>.
/// </summary>
/// <remarks>
/// The one that matters most is <c>CHARGE_OUTCOME_UNKNOWN</c>: the provider gave no verdict and
/// the charge MAY have happened. <see cref="Charge"/> is present, so poll
/// <see cref="DominaiteClient.GetStatusAsync"/> with <see cref="TransactionId"/> or wait for the
/// webhook. Never retry under a new key. A decline is NOT this exception: HTTP 402 returns a
/// charge whose status is <c>failed</c>. A 404 for an id that is not yours is
/// <see cref="DominaiteApiException"/> with code <c>PAYMENT_METHOD_NOT_FOUND</c>, and a 5xx that
/// carries no gateway code (an HTML page from a proxy) is <see cref="DominaiteTransportException"/>.
/// </remarks>
public sealed class DominaiteChargeException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteChargeException"/> class.</summary>
    /// <param name="httpStatus">The HTTP status code: 409, 422, 502 or 503.</param>
    /// <param name="code">The machine-readable reason. See <see cref="ChargeErrorCodes"/>.</param>
    /// <param name="message">The human-readable reason from the API.</param>
    /// <param name="charge">The charge row the gateway attached to its answer, when it did.</param>
    /// <param name="rawResult">The whole envelope, as received.</param>
    public DominaiteChargeException(
        int httpStatus,
        string code,
        string message,
        PaymentMethodCharge? charge,
        JsonElement rawResult)
        : base(message)
    {
        this.HttpStatus = httpStatus;
        this.Code = code;
        this.Charge = charge;
        this.TransactionId = string.IsNullOrEmpty(charge?.TransactionId) ? null : charge.TransactionId;
        this.RawResult = rawResult;
    }

    /// <summary>The charge row the gateway attached to its answer, when it did.</summary>
    public PaymentMethodCharge? Charge { get; }

    /// <summary>Shortcut for <c>Charge.TransactionId</c>, for polling <see cref="DominaiteClient.GetStatusAsync"/>.</summary>
    public string? TransactionId { get; }

    /// <summary>The whole envelope exactly as received, for fields the typed surface does not model.</summary>
    public JsonElement RawResult { get; }
}

/// <summary>
/// The codes <see cref="DominaiteClient.RevokePaymentMethodAsync"/> throws as a
/// <see cref="DominaiteRevokeException"/>, in the gateway's own order.
/// </summary>
public static class RevokeErrorCodes
{
    /// <summary>
    /// HTTP 502: the provider refused the deletion for a reason a retry will not fix; contact
    /// support with the payment method id.
    /// </summary>
    public const string UpstreamContractError = "UPSTREAM_CONTRACT_ERROR";

    /// <summary>HTTP 503: the provider is unavailable or throttling; retry later.</summary>
    public const string MerchantApiUnavailable = "MERCHANT_API_UNAVAILABLE";

    /// <summary>
    /// The whole vocabulary, in the order the canonical contract lists it. An unlisted code still
    /// arrives as a <see cref="DominaiteRevokeException"/>.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [UpstreamContractError, MerchantApiUnavailable];
}

/// <summary>
/// The gateway refused to revoke a stored payment method: HTTP 502 or 503. Nothing changed either
/// way; branch on <see cref="DominaiteException.Code"/>, see <see cref="RevokeErrorCodes"/>. An id
/// that is not yours is still <see cref="DominaiteApiException"/> with HTTP 404.
/// </summary>
public sealed class DominaiteRevokeException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteRevokeException"/> class.</summary>
    /// <param name="httpStatus">The HTTP status code: 502 or 503.</param>
    /// <param name="code">The machine-readable reason. See <see cref="RevokeErrorCodes"/>.</param>
    /// <param name="message">The human-readable reason from the API.</param>
    /// <param name="rawResult">The whole envelope, as received.</param>
    public DominaiteRevokeException(int httpStatus, string code, string message, JsonElement rawResult)
        : base(message)
    {
        this.HttpStatus = httpStatus;
        this.Code = code;
        this.RawResult = rawResult;
    }

    /// <summary>The whole envelope exactly as received, for fields the typed surface does not model.</summary>
    public JsonElement RawResult { get; }
}

/// <summary>
/// The codes <see cref="DominaiteClient.CreateRefundAsync"/> and
/// <see cref="DominaiteClient.GetRefundAsync"/> throw as a <see cref="DominaiteRefundException"/>,
/// in the order the canonical contract lists them. A 5xx is none of these: nothing was queued, and
/// it arrives as a retryable <see cref="DominaiteTransportException"/>.
/// </summary>
public static class RefundErrorCodes
{
    /// <summary>HTTP 404: no card-not-present payment with this id under your account.</summary>
    public const string PaymentNotFound = "PAYMENT_NOT_FOUND";

    /// <summary>
    /// HTTP 404, status read only: no refund with this id on this payment. Right after the create
    /// call the refund may not have been picked up yet: retryable, poll again for up to 60 seconds.
    /// After that the id is unknown.
    /// </summary>
    public const string RefundNotFound = "REFUND_NOT_FOUND";

    /// <summary>
    /// HTTP 422: the payment is not paid, is already fully refunded, or everything left on it is
    /// already being refunded. Nothing was queued and the key is not burnt.
    /// </summary>
    public const string PaymentNotRefundable = "PAYMENT_NOT_REFUNDABLE";

    /// <summary>
    /// HTTP 422: the amount is more than what is left to refund, counting refunds in progress; the
    /// message names the amount left. Nothing was queued and the key is not burnt.
    /// </summary>
    public const string RefundAmountExceeded = "REFUND_AMOUNT_EXCEEDED";

    /// <summary>HTTP 422: this key was first used for a different amount, reason or payment. Use a fresh key.</summary>
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";

    /// <summary>
    /// HTTP 409: a request with this key is being processed right now. Retryable: send the SAME key
    /// again after a second, for up to 120 seconds.
    /// </summary>
    public const string DuplicateRequest = "DUPLICATE_REQUEST";

    /// <summary>HTTP 400: the Idempotency-Key header is missing or longer than 100 characters.</summary>
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";

    /// <summary>
    /// The whole vocabulary, in the order the canonical contract lists it. An unlisted code still
    /// arrives as a <see cref="DominaiteRefundException"/>.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        PaymentNotFound,
        RefundNotFound,
        PaymentNotRefundable,
        RefundAmountExceeded,
        IdempotencyKeyReused,
        DuplicateRequest,
        IdempotencyKeyRequired,
    ];
}

/// <summary>
/// The values <see cref="Refund.FailureCode"/> carries on a refund whose status is
/// <c>failed</c>. These are not exceptions: a failed refund is a result. Treat a value not listed
/// here as <see cref="RefundFailed"/>.
/// </summary>
public static class RefundFailureCodes
{
    /// <summary>The amount was more than what was left to refund by the time the refund ran.</summary>
    public const string RefundAmountExceeded = "REFUND_AMOUNT_EXCEEDED";

    /// <summary>The payment could no longer be refunded by the time the refund ran.</summary>
    public const string PaymentNotRefundable = "PAYMENT_NOT_REFUNDABLE";

    /// <summary>The refund could not be completed. Retry with a new idempotency key if it is still wanted.</summary>
    public const string RefundFailed = "REFUND_FAILED";

    /// <summary>The whole vocabulary, in the order the canonical contract lists it.</summary>
    public static IReadOnlyList<string> All { get; } = [RefundAmountExceeded, PaymentNotRefundable, RefundFailed];
}

/// <summary>
/// The gateway answered a refund call with an error code instead of a refund: HTTP 400, 404, 409
/// or 422. Branch on <see cref="DominaiteException.Code"/>; see <see cref="RefundErrorCodes"/>.
/// </summary>
/// <remarks>
/// <see cref="DominaiteException.IsRetryable"/> is true for <c>DUPLICATE_REQUEST</c> (retry the
/// SAME key for up to 120 seconds) and <c>REFUND_NOT_FOUND</c> (poll again for up to 60 seconds
/// right after the create call), false for the rest. A failed refund is NOT this exception: it is a
/// <see cref="Refund"/> with status <c>failed</c> and a <see cref="Refund.FailureCode"/>.
/// Credentials failures stay <see cref="DominaiteAuthException"/>, and a 5xx is a
/// <see cref="DominaiteTransportException"/> (nothing was queued; retry with the same key).
/// </remarks>
public sealed class DominaiteRefundException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteRefundException"/> class.</summary>
    /// <param name="httpStatus">The HTTP status code.</param>
    /// <param name="code">The machine-readable reason. See <see cref="RefundErrorCodes"/>.</param>
    /// <param name="message">The human-readable reason from the API.</param>
    /// <param name="rawResult">The whole envelope, as received.</param>
    public DominaiteRefundException(int httpStatus, string code, string message, JsonElement rawResult)
        : base(message)
    {
        this.HttpStatus = httpStatus;
        this.Code = code;
        this.RawResult = rawResult;
    }

    /// <summary>The whole envelope exactly as received, for fields the typed surface does not model.</summary>
    public JsonElement RawResult { get; }

    /// <inheritdoc />
    public override bool IsRetryable
        => this.Code is RefundErrorCodes.DuplicateRequest or RefundErrorCodes.RefundNotFound;
}

/// <summary>
/// A network-level failure, a timeout, or a 5xx on a route with no typed error of its own. The
/// request may or may not have reached the API, so retry WITH THE SAME idempotency key.
/// </summary>
/// <remarks>
/// When the 5xx was the gateway talking (a JSON envelope rather than a proxy's HTML page), its
/// code is kept on <see cref="DominaiteException.Code"/>, e.g. <c>MERCHANT_API_UNAVAILABLE</c>.
/// </remarks>
public sealed class DominaiteTransportException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteTransportException"/> class.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="httpStatus">The 5xx status, when the failure was one.</param>
    /// <param name="innerException">The underlying cause, when there was one.</param>
    /// <param name="code">The gateway's code, when the 5xx carried one.</param>
    public DominaiteTransportException(
        string message,
        int? httpStatus = null,
        Exception? innerException = null,
        string? code = null)
        : base(message, innerException)
    {
        this.HttpStatus = httpStatus;
        this.Code = code;
    }

    /// <inheritdoc />
    public override bool IsRetryable => true;
}

/// <summary>Why a webhook delivery failed verification.</summary>
public enum WebhookFailureReason
{
    /// <summary>
    /// The signature header was not <c>t={unix_seconds},v1={lowercase_hex}</c>. The header never
    /// reached the crypto. In production this usually means the wrong header was read off the
    /// request, not an attack.
    /// </summary>
    MalformedSignature,

    /// <summary>
    /// The header parsed, but the MAC did not match. Either the body was modified in flight, or
    /// it was verified against the wrong endpoint's secret. Body mismatches are far more often a
    /// framework that re-serialized the JSON than an actual attacker.
    /// </summary>
    SignatureMismatch,

    /// <summary>
    /// The MAC was valid but the timestamp is too far from your clock, so this is a replay of a
    /// genuine delivery, or your server clock has drifted off NTP.
    /// </summary>
    TimestampOutOfTolerance,

    /// <summary>
    /// The signature verified but the body is not a webhook envelope this SDK can read: not JSON,
    /// or missing <c>id</c> / <c>type</c>, or a field of the wrong type. Only
    /// <see cref="Webhooks.VerifyAndParse(string, string, string, int, long?)"/> raises it.
    /// </summary>
    MalformedPayload,
}

/// <summary>
/// A webhook delivery did not verify. Every reason means the same thing operationally: do not
/// process the delivery. They are split apart so a misconfigured secret is distinguishable from
/// a clock problem in your logs.
/// </summary>
public sealed class DominaiteWebhookException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteWebhookException"/> class.</summary>
    /// <param name="reason">Which check failed.</param>
    /// <param name="message">The human-readable detail.</param>
    public DominaiteWebhookException(WebhookFailureReason reason, string message)
        : base(message)
    {
        this.Reason = reason;
    }

    /// <summary>Which check failed. Branch on this rather than on the message.</summary>
    public WebhookFailureReason Reason { get; }
}
