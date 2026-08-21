using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dominaite.MerchantSdk;

/// <summary>
/// Optional payer details. Prefilled fields are hidden from the payer in the widget, so the
/// checkout form stays short.
/// </summary>
public sealed class Customer
{
    /// <summary>Payer first name.</summary>
    public string? FirstName { get; set; }

    /// <summary>Payer last name.</summary>
    public string? LastName { get; set; }

    /// <summary>Payer email.</summary>
    public string? Email { get; set; }

    /// <summary>Payer phone.</summary>
    public string? Phone { get; set; }
}

/// <summary>
/// The parameters for <see cref="DominaiteClient.CreateCheckoutSessionAsync"/>.
/// </summary>
/// <remarks>
/// Property order here is the JSON order on the wire, and the body is serialized exactly once:
/// the bytes that are hashed for the signature are the bytes that are sent.
/// </remarks>
public sealed class CheckoutSessionRequest
{
    /// <summary>The amount in MINOR units: 2500 is 25.00 EUR. Integers only.</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 currency, e.g. "EUR".</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Your own order id, at most 100 characters. It shows up in your dashboard.</summary>
    public string OrderReference { get; set; } = string.Empty;

    /// <summary>Optional payer details.</summary>
    public Customer? Customer { get; set; }

    /// <summary>ISO 3166-1 alpha-2 payer country.</summary>
    public string? Country { get; set; }

    /// <summary>ISO 639-1 widget UI language.</summary>
    public string? Language { get; set; }

    /// <summary>"light", "dark" or "bright".</summary>
    public string? Theme { get; set; }

    /// <summary>Free-text description stored on the transaction.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The idempotency key. It travels in the header and in the signature, never in the body.
    /// </summary>
    /// <remarks>
    /// Leave it null and the client generates one per logical call and writes it back here, so
    /// you can log it and reuse it. Reusing a key never opens a second payment; it comes back as
    /// a replay refusal naming the transaction it collided with.
    /// </remarks>
    [JsonIgnore]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Extra body fields this class does not model yet. Merged into the JSON body at the top
    /// level; a key that collides with a modelled property is ignored.
    /// </summary>
    [JsonIgnore]
    public IDictionary<string, object?> Extra { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>
/// What <see cref="DominaiteClient.CreateCheckoutSessionAsync"/> returns.
/// </summary>
public sealed class CheckoutSession
{
    /// <summary>Dominaite's payment id. Store it against your order; poll status with it.</summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>The provider-facing correlation id (<c>dom_...</c>). You never need it.</summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Feeds the widget's <c>data-cashier-key</c>. A per-payment value, not a credential.</summary>
    public string CashierKey { get; set; } = string.Empty;

    /// <summary>
    /// Feeds the widget's <c>data-cashier-token</c>. A per-payment bearer value for the session:
    /// return it to the page that renders the widget, and never log it.
    /// </summary>
    public string CashierToken { get; set; } = string.Empty;

    /// <summary>The locked amount in MINOR units.</summary>
    public long Amount { get; set; }

    /// <summary>The locked ISO 4217 currency.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>When the session stops being usable. Sessions last about 2 hours.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The unparsed payload, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }

    /// <summary>
    /// A description with the session token redacted, so a logged session object cannot leak a
    /// bearer credential.
    /// </summary>
    /// <returns>The redacted description.</returns>
    public override string ToString()
        => $"CheckoutSession {{ TransactionId = {this.TransactionId}, OrderId = {this.OrderId}, "
            + $"Amount = {this.Amount}, Currency = {this.Currency}, CashierToken = [REDACTED] }}";
}

/// <summary>
/// The transaction status wire values, as constants plus the enumerable
/// <see cref="TransactionStatuses.All"/>.
/// </summary>
public static class TransactionStatuses
{
    /// <summary>The session exists and nobody has paid yet.</summary>
    public const string Pending = "pending";

    /// <summary>The payment is in flight.</summary>
    public const string Processing = "processing";

    /// <summary>The customer paid. The ONLY value that means paid.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>The payment failed.</summary>
    public const string Failed = "failed";

    /// <summary>Paid and then fully returned.</summary>
    public const string Refunded = "refunded";

    /// <summary>Paid and then partly returned.</summary>
    public const string PartiallyRefunded = "partially_refunded";

    /// <summary>The payment was cancelled before completion.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The payment is disputed.</summary>
    public const string Disputed = "disputed";

    /// <summary>Authorized, awaiting capture. The payer HAS paid.</summary>
    public const string RequiresCapture = "requires_capture";

    /// <summary>The payer never paid and the session aged out.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>
    /// The whole vocabulary, in the order the canonical contract lists it. The contract test
    /// pins this against the vendored <c>merchant-api-contract.json</c>, so a status the API
    /// adds cannot land in one SDK and be missed here.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Pending,
        Processing,
        Succeeded,
        Failed,
        Refunded,
        PartiallyRefunded,
        Cancelled,
        Disputed,
        RequiresCapture,
        Abandoned,
    ];
}

/// <summary>
/// What <see cref="DominaiteClient.GetStatusAsync"/> returns.
/// </summary>
public sealed class CheckoutStatus
{
    /// <summary>Dominaite's payment id.</summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>The provider-facing correlation id.</summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Your own order id, echoed back.</summary>
    public string? OrderReference { get; set; }

    /// <summary>
    /// One of the <see cref="TransactionStatuses"/> values. Ask <see cref="IsPaid"/> rather than
    /// comparing by hand.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The amount in MINOR units, as locked at session mint.</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 currency.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// How much of the amount has been returned, in MINOR units. Null when nothing was refunded,
    /// which is not the same as zero.
    /// </summary>
    public long? RefundedAmount { get; set; }

    /// <summary>When the session was created (UTC).</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>When the status last changed (UTC). Null when it never changed.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// The payer-checkout deadline: present while the payer still has an action to take, null
    /// once the payer's part is over. Null is NOT the same as terminal - branch on
    /// <see cref="Status"/> for liveness.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The unparsed payload, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }

    /// <summary>
    /// True only for <c>succeeded</c>. <c>refunded</c> and <c>partially_refunded</c> mean the
    /// customer paid and was then (partly) returned, which is a different question.
    /// </summary>
    /// <remarks>
    /// <c>requires_capture</c> is false here too, but it is NOT "unpaid": the payer has paid and
    /// the funds are held awaiting capture. Keep polling it rather than treating it as an
    /// abandoned order.
    /// </remarks>
    [JsonIgnore]
    public bool IsPaid => string.Equals(this.Status, TransactionStatuses.Succeeded, StringComparison.Ordinal);

    /// <summary>
    /// False while the payment can still change, true once it cannot.
    /// </summary>
    /// <remarks>
    /// An unrecognised status is reported as NOT terminal, so a status the API adds later makes
    /// you keep polling rather than silently close an order that is still open.
    /// </remarks>
    [JsonIgnore]
    public bool IsTerminal => this.Status switch
    {
        TransactionStatuses.Succeeded => true,
        TransactionStatuses.Failed => true,
        TransactionStatuses.Refunded => true,
        TransactionStatuses.PartiallyRefunded => true,
        TransactionStatuses.Cancelled => true,
        TransactionStatuses.Disputed => true,
        TransactionStatuses.Abandoned => true,
        _ => false,
    };
}

/// <summary>
/// What <see cref="DominaiteClient.PingAsync"/> returns: proof that your key, secret, signing and
/// clock are all good, without creating anything.
/// </summary>
public sealed class PingResponse
{
    /// <summary>Always true on a 200.</summary>
    public bool Pong { get; set; }

    /// <summary>The merchant id your key authenticated as.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Server time (UTC).</summary>
    public DateTimeOffset? ServerTime { get; set; }

    /// <summary>Server time in unix seconds.</summary>
    public long? ServerUnixTime { get; set; }

    /// <summary>
    /// Server time minus your <c>X-Timestamp</c>. If its absolute value creeps toward 300, fix
    /// NTP now - requests start failing at 300.
    /// </summary>
    public long ClockSkewSeconds { get; set; }

    /// <summary>The unparsed payload, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }
}
