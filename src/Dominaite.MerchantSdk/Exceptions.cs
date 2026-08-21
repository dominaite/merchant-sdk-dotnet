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
    /// True only for <see cref="DominaiteTransportException"/>, the one kind that is safe to
    /// retry, and only with the SAME idempotency key.
    /// </summary>
    public virtual bool IsRetryable => false;
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
/// Never blind-retry a refusal. It will not change on its own.
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
/// code such as <c>IDEMPOTENCY_KEY_REQUIRED</c>; a 422 means an idempotency key was replayed
/// with a different body; a 404 from <see cref="DominaiteClient.GetStatusAsync"/> means an
/// unknown transaction id.
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
/// A network-level failure, a timeout, or a 5xx. The request may or may not have reached the
/// API, so retry WITH THE SAME idempotency key.
/// </summary>
public sealed class DominaiteTransportException : DominaiteException
{
    /// <summary>Initializes a new instance of the <see cref="DominaiteTransportException"/> class.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="httpStatus">The 5xx status, when the failure was one.</param>
    /// <param name="innerException">The underlying cause, when there was one.</param>
    public DominaiteTransportException(string message, int? httpStatus = null, Exception? innerException = null)
        : base(message, innerException)
    {
        this.HttpStatus = httpStatus;
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
