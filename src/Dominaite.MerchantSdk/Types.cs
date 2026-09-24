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
    /// Ask the payer to save their card for later off-session charges. Once the session is
    /// paid, <see cref="CheckoutStatus.StoredPaymentMethod"/> carries the stored method to
    /// charge with <see cref="DominaiteClient.ChargePaymentMethodAsync"/>.
    /// </summary>
    /// <remarks>
    /// Null is omitted from the body, so a request that never sets it sends the exact bytes it
    /// always did.
    /// </remarks>
    public bool? SaveCard { get; set; }

    /// <summary>
    /// The idempotency key. Required. It travels in the header and in the signature, never in the
    /// body.
    /// </summary>
    /// <remarks>
    /// Derive it from the order with <see cref="IdempotencyKeys.ForOrder"/>: the same order at the
    /// same amount then replays the same session on a reload or a retry, and a changed amount gets
    /// a new key. Null or blank is rejected before anything is sent. Reusing a key never opens a
    /// second payment.
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

    /// <summary>
    /// The payment is disputed (a chargeback is open). Not terminal: a dispute resolves later.
    /// </summary>
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

    /// <summary>
    /// The card kept on file for this payment. Present once a session created with
    /// <see cref="CheckoutSessionRequest.SaveCard"/> has been approved, and it stays present
    /// after a revoke with status <c>revoked</c>; null (absent on the wire) until then, for
    /// sessions without SaveCard, and for declined or abandoned ones. Persist
    /// <see cref="StoredPaymentMethod.Id"/> against your customer; it is what
    /// <see cref="DominaiteClient.ChargePaymentMethodAsync"/> takes.
    /// </summary>
    /// <remarks>
    /// Not to be confused with the gateway's <c>paymentMethod</c> field, which is the string
    /// category of how the payer paid (<c>card</c>, <c>wallet</c>, ...) and stays on
    /// <see cref="Raw"/> untyped.
    /// </remarks>
    public StoredPaymentMethod? StoredPaymentMethod { get; set; }

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
    /// <c>pending</c>, <c>processing</c>, <c>requires_capture</c> and <c>disputed</c> are not
    /// terminal. A dispute resolves later, so keep watching it; <see cref="IsPaid"/> is false for
    /// it too, although the money did move. An unrecognised status is also reported as NOT
    /// terminal, so a status the API adds later makes you keep polling rather than silently close
    /// an order that is still open.
    /// </remarks>
    [JsonIgnore]
    public bool IsTerminal => this.Status switch
    {
        TransactionStatuses.Succeeded => true,
        TransactionStatuses.Failed => true,
        TransactionStatuses.Refunded => true,
        TransactionStatuses.PartiallyRefunded => true,
        TransactionStatuses.Cancelled => true,
        TransactionStatuses.Abandoned => true,
        _ => false,
    };
}

/// <summary>
/// The stored payment method status wire values, as constants plus the enumerable
/// <see cref="StoredPaymentMethodStatuses.All"/>.
/// </summary>
public static class StoredPaymentMethodStatuses
{
    /// <summary>The card can be charged. The ONLY value that means chargeable.</summary>
    public const string Active = "active";

    /// <summary>
    /// What <see cref="DominaiteClient.RevokePaymentMethodAsync"/> leaves behind; a charge on it
    /// is refused with <c>PAYMENT_METHOD_NOT_ACTIVE</c>.
    /// </summary>
    public const string Revoked = "revoked";

    /// <summary>The card's expiry date has passed; a charge on it is refused.</summary>
    public const string Expired = "expired";

    /// <summary>The whole vocabulary, in the order the canonical contract lists it.</summary>
    public static IReadOnlyList<string> All { get; } = [Active, Revoked, Expired];
}

/// <summary>
/// A card kept on file by a session that asked for <see cref="CheckoutSessionRequest.SaveCard"/>.
/// Display fields only: the card number never reaches this SDK, and the provider token behind
/// the id never leaves the gateway. <see cref="Brand"/>, <see cref="Last4"/> and the expiry are
/// null when the provider did not report them (the gateway omits null fields on the wire; the
/// SDK reads absent as null).
/// </summary>
public sealed class StoredPaymentMethod
{
    /// <summary>
    /// Opaque id: <c>pm_</c> followed by 32 hex characters, case-sensitive. The handle you charge
    /// and revoke with; persist it against your customer.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The card brand as the gateway reports it, e.g. "visa", "mastercard".</summary>
    public string? Brand { get; set; }

    /// <summary>The last four digits of the card number, for display only.</summary>
    public string? Last4 { get; set; }

    /// <summary>Expiry month, 1 to 12.</summary>
    public int? ExpiryMonth { get; set; }

    /// <summary>Expiry year, four digits, e.g. 2029.</summary>
    public int? ExpiryYear { get; set; }

    /// <summary>One of the <see cref="StoredPaymentMethodStatuses"/> values.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// True only for <c>active</c>. A status this SDK has never heard of reads as not chargeable,
    /// so a value the API adds later cannot make you charge a card you should not.
    /// </summary>
    [JsonIgnore]
    public bool IsChargeable => string.Equals(this.Status, StoredPaymentMethodStatuses.Active, StringComparison.Ordinal);
}

/// <summary>
/// The parameters for <see cref="DominaiteClient.ChargePaymentMethodAsync"/>: the same money
/// fields as a session, charged off-session against a stored card.
/// </summary>
/// <remarks>
/// Property order here is the JSON order on the wire, and the body is serialized exactly once:
/// the bytes that are hashed for the signature are the bytes that are sent.
/// </remarks>
public sealed class ChargeRequest
{
    /// <summary>The amount in MINOR units: 2500 is 25.00 EUR. Integers only.</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 currency, e.g. "EUR".</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Your own order id for this charge, at most 100 characters.</summary>
    public string OrderReference { get; set; } = string.Empty;

    /// <summary>Free-text description stored on the transaction.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The idempotency key. Required, and signed exactly like a session create: it travels in the
    /// header and in the signature, never in the body.
    /// </summary>
    /// <remarks>
    /// Derive it from the order with <see cref="IdempotencyKeys.ForOrder"/> (scope "charge"), and
    /// send the same key when you retry: a fresh key on a retry is the double-charge bug. Null or
    /// blank is rejected before anything is sent.
    /// </remarks>
    [JsonIgnore]
    public string? IdempotencyKey { get; set; }
}

/// <summary>
/// The charge status wire values, as constants plus the enumerable
/// <see cref="ChargeStatuses.All"/>.
/// </summary>
public static class ChargeStatuses
{
    /// <summary>The card was charged. The ONLY value that means paid.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>
    /// The issuer declined (HTTP 402); see <see cref="PaymentMethodCharge.DeclineClass"/>.
    /// </summary>
    public const string Failed = "failed";

    /// <summary>
    /// Not decided yet. Not terminal: poll <see cref="DominaiteClient.GetStatusAsync"/> with the
    /// charge's transaction id.
    /// </summary>
    public const string Pending = "pending";

    /// <summary>An authorization voided before capture; no money moved.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The whole vocabulary, in the order the canonical contract lists it.</summary>
    public static IReadOnlyList<string> All { get; } = [Succeeded, Failed, Pending, Cancelled];
}

/// <summary>
/// Decline class wire values, on <see cref="PaymentMethodCharge.DeclineClass"/>. Coarse
/// buckets that tell you what to do next; the provider's own reason is in
/// <see cref="PaymentMethodCharge.DeclineCode"/>.
/// </summary>
public static class DeclineClasses
{
    /// <summary>Do not retry this card: stolen, closed, or the issuer said never.</summary>
    public const string Hard = "hard";

    /// <summary>Insufficient funds; retry in a few days.</summary>
    public const string SoftFunds = "soft_funds";

    /// <summary>
    /// The issuer wants the payer present. Bring them back through a checkout session; an
    /// off-session retry will fail the same way.
    /// </summary>
    public const string SoftScaRequired = "soft_sca_required";

    /// <summary>A transient decline; retry later.</summary>
    public const string SoftOther = "soft_other";

    /// <summary>The whole vocabulary, in the order the canonical contract lists it.</summary>
    public static IReadOnlyList<string> All { get; } = [Hard, SoftFunds, SoftScaRequired, SoftOther];
}

/// <summary>
/// What <see cref="DominaiteClient.ChargePaymentMethodAsync"/> returns, for a placed charge
/// (HTTP 201) and for a provider decline (HTTP 402, status <c>failed</c>) alike. A decline is a
/// result, not an exception: check <see cref="IsPaid"/>, then branch on <see cref="DeclineClass"/>.
/// Also carried on <see cref="DominaiteChargeException.Charge"/> when the gateway attached the
/// charge row to its answer.
/// </summary>
public sealed class PaymentMethodCharge
{
    /// <summary>
    /// <c>ch_</c> followed by 32 hex characters. Store it against the order; it is what support
    /// asks for.
    /// </summary>
    public string ChargeId { get; set; } = string.Empty;

    /// <summary>One of the <see cref="ChargeStatuses"/> values.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// One of the <see cref="DeclineClasses"/> values, set on a 402 decline; null everywhere else
    /// (the SDK reads absent as null).
    /// </summary>
    public string? DeclineClass { get; set; }

    /// <summary>
    /// The provider's raw decline code, for your logs; branch on <see cref="DeclineClass"/>
    /// instead. Null when DeclineClass is.
    /// </summary>
    public string? DeclineCode { get; set; }

    /// <summary>
    /// Dominaite's payment id for this charge. Store it against your order; poll status with it.
    /// </summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>
    /// The unwrapped charge object as the gateway sent it, for fields this class does not model
    /// yet.
    /// </summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }

    /// <summary>True only for <c>succeeded</c>.</summary>
    [JsonIgnore]
    public bool IsPaid => string.Equals(this.Status, ChargeStatuses.Succeeded, StringComparison.Ordinal);

    /// <summary>
    /// False while the charge can still change, true once it cannot. An unrecognised status is
    /// reported as NOT terminal, so a status the API adds later keeps you polling.
    /// </summary>
    [JsonIgnore]
    public bool IsTerminal => this.Status switch
    {
        ChargeStatuses.Succeeded => true,
        ChargeStatuses.Failed => true,
        ChargeStatuses.Cancelled => true,
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
