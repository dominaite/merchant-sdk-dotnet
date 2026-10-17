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
    /// The id the API gave the failed request: the <c>Dominaite-Request-Id</c> header, else the
    /// body's <c>metadata.requestId</c>. Null when nothing reached the API or it sent no id. Quote
    /// it to Dominaite support.
    /// </summary>
    public string? RequestId { get; internal set; }

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
/// The HTTP 401 codes. Each one arrives as a <see cref="DominaiteAuthException"/> and none is
/// retryable.
/// </summary>
public static class AuthErrorCodes
{
    /// <summary>The key id is unknown, malformed or revoked, or it belongs to another environment.</summary>
    public const string InvalidApiKey = "INVALID_API_KEY";

    /// <summary>The signature does not match: check the secret, the signed path and the body bytes.</summary>
    public const string InvalidSignature = "INVALID_SIGNATURE";

    /// <summary>The <c>X-Timestamp</c> is more than 300 seconds away from the server clock.</summary>
    public const string TimestampOutOfRange = "TIMESTAMP_OUT_OF_RANGE";

    /// <summary>The caller's IP address is not on the key's allowlist.</summary>
    public const string IpNotAllowed = "IP_NOT_ALLOWED";

    /// <summary>Every 401 code above, in the order the integration contract lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [InvalidApiKey, InvalidSignature, TimestampOutOfRange, IpNotAllowed];
}

/// <summary>
/// The API rejected your credentials or signature (HTTP 401). Not retryable: fix the key id,
/// the secret, the server clock, or the caller allowlist.
/// </summary>
/// <remarks>
/// A 403 is not this exception. A 403 means the credentials were good but the call is not
/// allowed: that is <see cref="DominaitePermissionException"/>.
/// </remarks>
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
/// The HTTP 403 codes. The key and the signature were good, but this call is not allowed.
/// </summary>
public static class PermissionErrorCodes
{
    /// <summary>
    /// HTTP 403: the API key does not have the scope this route needs. The message names the
    /// scope. Use a key that has it; retrying with the same key will not help.
    /// </summary>
    public const string InsufficientScope = "INSUFFICIENT_SCOPE";

    /// <summary>
    /// HTTP 403 on session create: verify-only sessions are not on for your account. Nothing was
    /// created and the idempotency key is not used up.
    /// </summary>
    public const string VerifyOnlyNotEnabled = "VERIFY_ONLY_NOT_ENABLED";

    /// <summary>Every 403 code above.</summary>
    public static IReadOnlyList<string> All { get; } = [InsufficientScope, VerifyOnlyNotEnabled];
}

/// <summary>
/// The key and the signature were good, but this call is not allowed (HTTP 403). Not retryable:
/// the same call with the same key gets the same answer.
/// </summary>
/// <remarks>
/// <see cref="DominaiteException.Code"/> is one of <see cref="PermissionErrorCodes"/>.
/// <c>INSUFFICIENT_SCOPE</c>: use an API key that has the scope the message names.
/// <c>VERIFY_ONLY_NOT_ENABLED</c>: verify-only sessions are off for your account. We switch it on
/// for your account; ask Dominaite support.
/// </remarks>
public sealed class DominaitePermissionException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaitePermissionException"/> class.</summary>
    /// <param name="code">The 403 code, usually one of <see cref="PermissionErrorCodes"/>.</param>
    /// <param name="message">What is not allowed and what to do.</param>
    public DominaitePermissionException(string code, string message)
        : base(message)
    {
        this.Code = code;
        this.HttpStatus = 403;
    }
}

/// <summary>
/// The HTTP 429 codes. Each one means a limit refused the call and nothing was done.
/// </summary>
public static class RateLimitErrorCodes
{
    /// <summary>Too many requests with this API key. Wait, then send the call again.</summary>
    public const string RateLimitExceeded = "RATE_LIMIT_EXCEEDED";

    /// <summary>
    /// A stored-card charge would go past a volume limit: the charges allowed on one payment
    /// method in 24 hours, or the amount allowed per hour in one currency. Nothing was charged.
    /// </summary>
    public const string ChargeVelocityExceeded = "CHARGE_VELOCITY_EXCEEDED";

    /// <summary>
    /// This API key collected too many declined charges in a short time, so its charge route is
    /// locked for a while. Nothing was charged.
    /// </summary>
    public const string ChargeDeclineRateExceeded = "CHARGE_DECLINE_RATE_EXCEEDED";

    /// <summary>Every 429 code above.</summary>
    public static IReadOnlyList<string> All { get; } = [RateLimitExceeded, ChargeVelocityExceeded, ChargeDeclineRateExceeded];
}

/// <summary>
/// A limit refused the call (HTTP 429). Nothing was done. Wait <see cref="RetryAfter"/>, then
/// send the call again with the SAME idempotency key.
/// </summary>
/// <remarks>
/// <see cref="DominaiteException.Code"/> says which limit (<see cref="RateLimitErrorCodes"/>), and
/// is null when the 429 did not come from the API itself. <see cref="DominaiteException.IsRetryable"/>
/// is false and <see cref="DominaiteClient.CreateCheckoutSessionWithRetryAsync"/> does not retry
/// it: a retry before the wait is over meets the same limit.
/// </remarks>
public sealed class DominaiteRateLimitException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteRateLimitException"/> class.</summary>
    /// <param name="code">The 429 code, when the API sent one.</param>
    /// <param name="message">Which limit, and what to do.</param>
    /// <param name="retryAfter">The wait the API asked for in its <c>Retry-After</c> header, when it sent one.</param>
    public DominaiteRateLimitException(string? code, string message, TimeSpan? retryAfter)
        : base(message)
    {
        this.Code = code;
        this.HttpStatus = 429;
        this.RetryAfter = retryAfter;
    }

    /// <summary>
    /// How long to wait before the next attempt, from the <c>Retry-After</c> header. Null when the
    /// API sent no such header: then wait at least a few seconds, and longer on each new 429.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <inheritdoc />
    public override bool IsRetryable => false;
}

/// <summary>
/// The API answered, but with a rejecting or unexpected response. A 400 carries a validation
/// code such as <c>IDEMPOTENCY_KEY_REQUIRED</c>; a 404 from <see cref="DominaiteClient.GetStatusAsync"/>
/// means an unknown transaction id, and from the payment method routes an id that is not yours
/// (<c>PAYMENT_METHOD_NOT_FOUND</c> on a charge, <c>VALIDATION_ERROR</c> on a revoke).
/// </summary>
/// <remarks>
/// <see cref="RawResult"/> keeps the answer's envelope. Read the details there that
/// <see cref="DominaiteException.Code"/> does not carry, for example the field errors of a 400.
/// </remarks>
public sealed class DominaiteApiException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteApiException"/> class.</summary>
    /// <param name="httpStatus">The HTTP status code.</param>
    /// <param name="code">The machine-readable reason when the API sent one, otherwise null.</param>
    /// <param name="message">The human-readable reason.</param>
    public DominaiteApiException(int httpStatus, string? code, string message)
        : this(httpStatus, code, message, default)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DominaiteApiException"/> class.</summary>
    /// <param name="httpStatus">The HTTP status code.</param>
    /// <param name="code">The machine-readable reason when the API sent one, otherwise null.</param>
    /// <param name="message">The human-readable reason.</param>
    /// <param name="rawResult">The whole envelope, as received.</param>
    public DominaiteApiException(int httpStatus, string? code, string message, JsonElement rawResult)
        : base(message)
    {
        this.HttpStatus = httpStatus;
        this.Code = code;
        this.RawResult = rawResult;
    }

    /// <summary>
    /// The whole envelope exactly as received, for fields the typed surface does not model. Its
    /// <see cref="JsonElement.ValueKind"/> is <see cref="JsonValueKind.Undefined"/> when the answer
    /// had no envelope.
    /// </summary>
    public JsonElement RawResult { get; }
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

    /// <summary>
    /// HTTP 400 on every keyed write: the <c>Idempotency-Key</c> header is missing or longer than
    /// 100 characters. The SDK checks the key first, so you see this only from your own HTTP code.
    /// </summary>
    public const string IdempotencyKeyRequired = "IDEMPOTENCY_KEY_REQUIRED";

    /// <summary>
    /// HTTP 503: the API cannot serve the call right now. Nothing was done. Arrives as a
    /// retryable <see cref="DominaiteTransportException"/>, except on a revoke.
    /// </summary>
    public const string MerchantApiUnavailable = "MERCHANT_API_UNAVAILABLE";

    /// <summary>Session refusal: the <c>agreementId</c> names no agreement of yours.</summary>
    public const string AgreementNotFound = "AGREEMENT_NOT_FOUND";

    /// <summary>
    /// Session refusal: the agreement is already active, cancelled, completed or failed, so there is
    /// no first payment left to take.
    /// </summary>
    public const string AgreementNotPending = "AGREEMENT_NOT_PENDING";

    /// <summary>Session refusal: the amount or the currency differs from the agreement's own.</summary>
    public const string AgreementAmountMismatch = "AGREEMENT_AMOUNT_MISMATCH";

    /// <summary>
    /// Session refusal: neither the request nor the agreement carries platform consent. Send
    /// <see cref="CheckoutSessionRequest.Consent"/>.
    /// </summary>
    public const string AgreementConsentRequired = "AGREEMENT_CONSENT_REQUIRED";

    /// <summary>
    /// Session refusal: a session for this agreement is already open;
    /// <see cref="DominaiteRefusalException.TransactionId"/> names it.
    /// </summary>
    public const string AgreementSessionOpen = "AGREEMENT_SESSION_OPEN";

    /// <summary>
    /// Session refusal: the session bound to this agreement was already paid while the agreement is
    /// still pending. Read the status of the named transaction; do not mint again.
    /// </summary>
    public const string AgreementAlreadyPaid = "AGREEMENT_ALREADY_PAID";

    /// <summary>
    /// Session refusal: the agreement has a <c>maxAmountPerPeriod</c>, and a session can take the
    /// first payment of a fixed-amount agreement only.
    /// </summary>
    public const string AgreementVariableAmountUnsupported = "AGREEMENT_VARIABLE_AMOUNT_UNSUPPORTED";

    /// <summary>
    /// Session refusal: subscriptions are not switched on for your account yet. Ask Dominaite
    /// support; do not retry on a schedule.
    /// </summary>
    public const string SubscriptionsNotEnabled = "SUBSCRIPTIONS_NOT_ENABLED";

    /// <summary>
    /// The session refusal codes: an HTTP 200 whose <c>data.success</c> is false. The first five
    /// are in the order the canonical contract lists them; the subscription session codes follow.
    /// Each one arrives as a <see cref="DominaiteRefusalException"/>.
    /// </summary>
    public static IReadOnlyList<string> SessionRefusals { get; } =
    [
        PaymentProcessingUnavailable,
        DuplicateRequest,
        AlreadyProcessed,
        IdempotencyKeyReused,
        PriorAttemptFailed,
        AgreementNotFound,
        AgreementNotPending,
        AgreementAmountMismatch,
        AgreementConsentRequired,
        AgreementSessionOpen,
        AgreementAlreadyPaid,
        AgreementVariableAmountUnsupported,
        SubscriptionsNotEnabled,
    ];

    /// <summary>The idempotency codes, in the order the integration contract lists them.</summary>
    public static IReadOnlyList<string> Idempotency { get; } =
    [
        IdempotencyKeyRequired,
        DuplicateRequest,
        AlreadyProcessed,
        PriorAttemptFailed,
        IdempotencyKeyReused,
    ];

    /// <summary>
    /// The codes that mean "nothing was done, try again later with the SAME key", in the order the
    /// integration contract lists them.
    /// </summary>
    public static IReadOnlyList<string> Transient { get; } = [MerchantApiUnavailable, PaymentProcessingUnavailable];

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
    /// HTTP 402: the issuer declined. Not an exception and not in <see cref="All"/>: the call
    /// returns the charge with status <c>failed</c>.
    /// </summary>
    public const string ChargeDeclined = "CHARGE_DECLINED";

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

    /// <summary>
    /// HTTP 503: nothing was charged. We send this code before we switch stored-card charges on for
    /// your account, and when we pause charges for a short time. The code is the same in both
    /// cases. If we never switched stored-card charges on for your account, a retry does not help:
    /// ask Dominaite support. If they are on, retry later with the SAME key.
    /// </summary>
    public const string PaymentMethodChargesDisabled = "PAYMENT_METHOD_CHARGES_DISABLED";

    /// <summary>HTTP 503: card payments are off right now; nothing was charged. Retry later with the SAME key.</summary>
    public const string PaymentProcessingUnavailable = "PAYMENT_PROCESSING_UNAVAILABLE";

    /// <summary>HTTP 404: no stored payment method with this id under your account. Nothing was charged.</summary>
    public const string PaymentMethodNotFound = "PAYMENT_METHOD_NOT_FOUND";

    /// <summary>
    /// HTTP 409: another charge on this payment method, sent with a different key, still waits for
    /// the provider's verdict. This request charged nothing. Not retryable until that charge ends.
    /// </summary>
    public const string ChargeInProgress = "CHARGE_IN_PROGRESS";

    /// <summary>
    /// HTTP 422: a card is only charged in the currency of the payment it was saved from. Nothing
    /// was charged.
    /// </summary>
    public const string ChargeCurrencyMismatch = "CHARGE_CURRENCY_MISMATCH";

    /// <summary>HTTP 404: the <c>agreementId</c> is not one of your agreements. Nothing was charged and the key is not used up.</summary>
    public const string AgreementNotFound = "AGREEMENT_NOT_FOUND";

    /// <summary>HTTP 422: the agreement is linked to a different payment method than the one in the path.</summary>
    public const string AgreementMethodMismatch = "AGREEMENT_METHOD_MISMATCH";

    /// <summary>HTTP 422: the agreement has no consent on record, so it cannot back a charge.</summary>
    public const string AgreementConsentMissing = "AGREEMENT_CONSENT_MISSING";

    /// <summary>HTTP 422: the agreement is in a different currency than the charge.</summary>
    public const string AgreementCurrencyMismatch = "AGREEMENT_CURRENCY_MISMATCH";

    /// <summary>HTTP 422: the agreement has a fixed amount and the charge is below it.</summary>
    public const string AgreementAmountMismatch = "AGREEMENT_AMOUNT_MISMATCH";

    /// <summary>HTTP 422: the amount is above what the customer agreed to under this agreement.</summary>
    public const string ChargeAmountAboveAgreement = "CHARGE_AMOUNT_ABOVE_AGREEMENT";

    /// <summary>HTTP 422: the agreement's current billing period already has its charge.</summary>
    public const string AgreementPeriodAlreadyCharged = "AGREEMENT_PERIOD_ALREADY_CHARGED";

    /// <summary>HTTP 422: the agreement billed its last period, or was cancelled before this period began.</summary>
    public const string AgreementEnded = "AGREEMENT_ENDED";

    /// <summary>HTTP 422: the agreement is not active yet or is still in its trial.</summary>
    public const string AgreementNotStarted = "AGREEMENT_NOT_STARTED";

    /// <summary>HTTP 422: the amount is above the most one charge may be in this currency.</summary>
    public const string ChargeAmountAboveCap = "CHARGE_AMOUNT_ABOVE_CAP";

    /// <summary>
    /// The whole vocabulary. The first seven are in the order the canonical contract lists them;
    /// the codes the charge route added later follow. An unlisted 4xx code still arrives as a
    /// <see cref="DominaiteChargeException"/>.
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
        PaymentMethodNotFound,
        ChargeInProgress,
        ChargeCurrencyMismatch,
        AgreementNotFound,
        AgreementMethodMismatch,
        AgreementConsentMissing,
        AgreementCurrencyMismatch,
        AgreementAmountMismatch,
        ChargeAmountAboveAgreement,
        AgreementPeriodAlreadyCharged,
        AgreementEnded,
        AgreementNotStarted,
        ChargeAmountAboveCap,
    ];
}

/// <summary>
/// The gateway answered a charge with an error code instead of a charge result: HTTP 404, 409,
/// 422, 502 or 503. Branch on <see cref="DominaiteException.Code"/>; see <see cref="ChargeErrorCodes"/>.
/// </summary>
/// <remarks>
/// The one that matters most is <c>CHARGE_OUTCOME_UNKNOWN</c>: the provider gave no verdict and
/// the charge MAY have happened. <see cref="Charge"/> is present, so poll
/// <see cref="DominaiteClient.GetStatusAsync"/> with <see cref="TransactionId"/> or wait for the
/// webhook. Never retry under a new key. A decline is NOT this exception: HTTP 402 returns a
/// charge whose status is <c>failed</c>. A 404 for an id that is not yours IS this exception, with
/// code <c>PAYMENT_METHOD_NOT_FOUND</c> (or <c>AGREEMENT_NOT_FOUND</c> for the agreement). A 400 is
/// <see cref="DominaiteApiException"/>. A 5xx whose code is not in <see cref="ChargeErrorCodes"/>
/// (an HTML page from a proxy, or a code such as <c>INTERNAL_ERROR</c>) is a
/// <see cref="DominaiteTransportException"/> that keeps the code.
/// </remarks>
public sealed class DominaiteChargeException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteChargeException"/> class.</summary>
    /// <param name="httpStatus">The HTTP status code: 404, 409, 422, 502 or 503.</param>
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

    /// <summary>
    /// True for <c>PAYMENT_PROCESSING_UNAVAILABLE</c> (card payments come back on their own) and
    /// <c>DUPLICATE_REQUEST</c> (the first request with this key is still running): retry both with
    /// the SAME key. False for the rest, <c>CHARGE_IN_PROGRESS</c> and <c>CHARGE_OUTCOME_UNKNOWN</c>
    /// included.
    /// </summary>
    public override bool IsRetryable
        => this.Code is ChargeErrorCodes.DuplicateRequest or ChargeErrorCodes.PaymentProcessingUnavailable;
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
    /// The whole vocabulary, in the order the canonical contract lists it. An unlisted 5xx code
    /// arrives as a <see cref="DominaiteTransportException"/> that keeps the code.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [UpstreamContractError, MerchantApiUnavailable];
}

/// <summary>
/// The codes the plan and agreement routes answer with, in the order the integration contract
/// lists them. A 4xx arrives as a <see cref="DominaiteApiException"/> with one of these on
/// <see cref="DominaiteException.Code"/>; the 503 arrives as a retryable
/// <see cref="DominaiteTransportException"/>.
/// </summary>
public static class SubscriptionErrorCodes
{
    /// <summary>HTTP 404: no plan with this id under your account. The <c>plan_</c> handle is case-sensitive.</summary>
    public const string PlanNotFound = "PLAN_NOT_FOUND";

    /// <summary>HTTP 409: the plan is archived; create the agreement from an active plan.</summary>
    public const string PlanNotActive = "PLAN_NOT_ACTIVE";

    /// <summary>HTTP 404: no agreement with this id under your account.</summary>
    public const string AgreementNotFound = "AGREEMENT_NOT_FOUND";

    /// <summary>HTTP 409 on cancel: the agreement already completed or failed. Cancelling a cancelled agreement succeeds.</summary>
    public const string AgreementEnded = "AGREEMENT_ENDED";

    /// <summary>HTTP 404: no stored payment method with the <c>storedPaymentMethodId</c> under your account.</summary>
    public const string PaymentMethodNotFound = "PAYMENT_METHOD_NOT_FOUND";

    /// <summary>HTTP 409: the stored payment method is revoked, expired or retired and cannot back an agreement.</summary>
    public const string PaymentMethodNotActive = "PAYMENT_METHOD_NOT_ACTIVE";

    /// <summary>HTTP 422: the stored payment method was saved from a payment in another currency than the plan's.</summary>
    public const string PaymentMethodCurrencyMismatch = "PAYMENT_METHOD_CURRENCY_MISMATCH";

    /// <summary>HTTP 422: <c>maxAmountPerPeriod</c> is below the plan's amount. Nothing was created.</summary>
    public const string MaxAmountBelowPlanAmount = "MAX_AMOUNT_BELOW_PLAN_AMOUNT";

    /// <summary>HTTP 422: the agreement names a stored payment method or a maximum amount but carries no consent.</summary>
    public const string AgreementConsentRequired = "AGREEMENT_CONSENT_REQUIRED";

    /// <summary>HTTP 422: the currency the stored payment method is pinned to cannot be established.</summary>
    public const string PaymentMethodCurrencyUnknown = "PAYMENT_METHOD_CURRENCY_UNKNOWN";

    /// <summary>HTTP 422: this key was first used for another plan or agreement. Use a fresh key.</summary>
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";

    /// <summary>HTTP 503: the platform could not validate the request. Nothing was created; retry with the SAME key.</summary>
    public const string MerchantApiUnavailable = "MERCHANT_API_UNAVAILABLE";

    /// <summary>The whole vocabulary above.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        PlanNotFound,
        PlanNotActive,
        AgreementNotFound,
        AgreementEnded,
        PaymentMethodNotFound,
        PaymentMethodNotActive,
        PaymentMethodCurrencyMismatch,
        MaxAmountBelowPlanAmount,
        AgreementConsentRequired,
        PaymentMethodCurrencyUnknown,
        IdempotencyKeyReused,
        MerchantApiUnavailable,
    ];
}

/// <summary>
/// The codes the webhook endpoint routes answer with. A 4xx arrives as a
/// <see cref="DominaiteApiException"/> with one of these on <see cref="DominaiteException.Code"/>;
/// the 503 arrives as a retryable <see cref="DominaiteTransportException"/>.
/// </summary>
public static class WebhookEndpointErrorCodes
{
    /// <summary>
    /// HTTP 404 on delete: the id is unknown, belongs to another account or was already deleted. A
    /// delete retry after a lost answer gets this; treat it as done.
    /// </summary>
    public const string WebhookNotFound = "WEBHOOK_NOT_FOUND";

    /// <summary>HTTP 409: your account already has the most active endpoints it may have.</summary>
    public const string WebhookLimitReached = "WEBHOOK_LIMIT_REACHED";

    /// <summary>
    /// HTTP 409: this key registered an endpoint that still exists. Its id is in
    /// <c>error.params.webhookId</c> of <see cref="DominaiteApiException.RawResult"/>.
    /// </summary>
    public const string WebhookAlreadyRegistered = "WEBHOOK_ALREADY_REGISTERED";

    /// <summary>HTTP 400: the URL is not HTTPS on a public host.</summary>
    public const string WebhookUrlRejected = "WEBHOOK_URL_REJECTED";

    /// <summary>HTTP 422: this key was first used with a different body. Use a fresh key.</summary>
    public const string IdempotencyKeyReused = "IDEMPOTENCY_KEY_REUSED";

    /// <summary>HTTP 409: the first call with this key still runs. Retry shortly with the SAME key.</summary>
    public const string DuplicateRequest = "DUPLICATE_REQUEST";

    /// <summary>HTTP 503: the platform could not serve the call. Nothing was registered.</summary>
    public const string MerchantApiUnavailable = "MERCHANT_API_UNAVAILABLE";

    /// <summary>The whole vocabulary above.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        WebhookNotFound,
        WebhookLimitReached,
        WebhookAlreadyRegistered,
        WebhookUrlRejected,
        IdempotencyKeyReused,
        DuplicateRequest,
        MerchantApiUnavailable,
    ];
}

/// <summary>
/// The validation codes. A rejected request answers HTTP 400 with <see cref="ValidationError"/>
/// on <see cref="DominaiteException.Code"/>, and one entry per field in
/// <c>error.validationErrors</c> of <see cref="DominaiteApiException.RawResult"/>, each with one of
/// the <see cref="Fields"/> codes.
/// </summary>
public static class ValidationErrorCodes
{
    /// <summary>HTTP 400: one or more fields were refused; the field errors are in RawResult.</summary>
    public const string ValidationError = "VALIDATION_ERROR";

    /// <summary>Field code: the field is required.</summary>
    public const string Required = "REQUIRED";

    /// <summary>Field code: the value is below the minimum.</summary>
    public const string MinValue = "MIN_VALUE";

    /// <summary>Field code: the value is too long.</summary>
    public const string MaxLength = "MAX_LENGTH";

    /// <summary>Field code: the value does not have the expected format.</summary>
    public const string InvalidFormat = "INVALID_FORMAT";

    /// <summary>Field code: the value is not one of the allowed values.</summary>
    public const string InvalidSelection = "INVALID_SELECTION";

    /// <summary>Field code: the date is in the future.</summary>
    public const string DateInFuture = "DATE_IN_FUTURE";

    /// <summary>Field code: the value is not a valid email address.</summary>
    public const string InvalidEmail = "INVALID_EMAIL";

    /// <summary>Field code: the value is not a known currency.</summary>
    public const string InvalidCurrency = "INVALID_CURRENCY";

    /// <summary>The field codes, in the order the integration contract lists them.</summary>
    public static IReadOnlyList<string> Fields { get; } =
    [
        Required,
        MinValue,
        MaxLength,
        InvalidFormat,
        InvalidSelection,
        DateInFuture,
        InvalidEmail,
        InvalidCurrency,
    ];
}

/// <summary>
/// The gateway refused to revoke a stored payment method: HTTP 502 or 503 with one of the
/// <see cref="RevokeErrorCodes"/>. Nothing changed either way; branch on
/// <see cref="DominaiteException.Code"/>. An id that is not yours is still
/// <see cref="DominaiteApiException"/> with HTTP 404, and a 5xx with any other code is a
/// <see cref="DominaiteTransportException"/> that keeps the code.
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
/// The codes <see cref="DominaiteClient.CreateRefundAsync"/>,
/// <see cref="DominaiteClient.GetRefundAsync"/> and <see cref="DominaiteClient.ListRefundsAsync"/>
/// throw as a <see cref="DominaiteRefundException"/>, in the order the canonical contract lists
/// them. A 5xx is none of these: nothing was queued, and it arrives as a retryable
/// <see cref="DominaiteTransportException"/>.
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
