using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dominaite.MerchantSdk;

/// <summary>
/// Webhook event type wire values. Exact case; anything else is rejected at registration.
/// </summary>
public static class WebhookEventTypes
{
    /// <summary>The payment completed: money is in hand.</summary>
    public const string PaymentSucceeded = "payment.succeeded";

    /// <summary>The payment failed.</summary>
    public const string PaymentFailed = "payment.failed";

    /// <summary>The payer paid and the funds are held awaiting capture.</summary>
    public const string PaymentRequiresCapture = "payment.requires_capture";

    /// <summary>A pre-completion void.</summary>
    public const string PaymentCancelled = "payment.cancelled";

    /// <summary>A checkout that was never paid, closed by the sweep.</summary>
    public const string PaymentAbandoned = "payment.abandoned";

    /// <summary>One refund went back to the customer.</summary>
    public const string PaymentRefunded = "payment.refunded";

    /// <summary>A chargeback was opened.</summary>
    public const string PaymentDisputed = "payment.disputed";

    /// <summary>The agreement's first payment succeeded and it is now billing.</summary>
    public const string AgreementActivated = "agreement.activated";

    /// <summary>The platform gave up on the current period on the agreement's card.</summary>
    public const string AgreementPastDue = "agreement.past_due";

    /// <summary>You cancelled the agreement.</summary>
    public const string AgreementCancelled = "agreement.cancelled";

    /// <summary>A charge on a stored card moved the money.</summary>
    public const string ChargeSucceeded = "charge.succeeded";

    /// <summary>A soft decline on an agreement period; the schedule will try again.</summary>
    public const string ChargeRetrying = "charge.retrying";

    /// <summary>The charge was declined and nothing more will be tried for it.</summary>
    public const string ChargeFailed = "charge.failed";
}

/// <summary>
/// One verified webhook delivery. <see cref="Webhooks.VerifyAndParse(string, string, string, int, long?)"/>
/// is the only way to get one, so an unverified payload never reaches your handler as a typed
/// event.
/// </summary>
/// <remarks>
/// Deliveries can arrive out of order. For <c>agreement.*</c> and <c>charge.*</c> events, order
/// by <see cref="AgreementEventData.Sequence"/> / <see cref="ChargeEventData.Sequence"/> per
/// <c>OrderingKey</c>, never by <see cref="CreatedAt"/>: see the README's "Ordering" section.
/// </remarks>
public sealed class WebhookEvent
{
    /// <summary>The delivery id. Dedupe on it: delivery is at-least-once.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>One of the <see cref="WebhookEventTypes"/> values, or one added later.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// The dated version of the envelope and <c>data</c> shape, e.g. <c>2026-09-25</c>. Additive
    /// fields keep the current value; a new one means a breaking change. Null on a delivery from a
    /// server that predates the field.
    /// </summary>
    public string? ApiVersion { get; set; }

    /// <summary>
    /// When the transition was recorded (UTC). Several events can share one value, so it is not
    /// an ordering key.
    /// </summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>The event's <c>data</c> object as sent, for every type.</summary>
    [JsonIgnore]
    public JsonElement Data { get; set; }

    /// <summary>The <c>data</c> of a <c>payment.*</c> event, typed; null for every other type.</summary>
    [JsonIgnore]
    public PaymentEventData? Payment { get; set; }

    /// <summary>The <c>data</c> of an <c>agreement.*</c> event, typed; null for every other type.</summary>
    [JsonIgnore]
    public AgreementEventData? Agreement { get; set; }

    /// <summary>The <c>data</c> of a <c>charge.*</c> event, typed; null for every other type.</summary>
    [JsonIgnore]
    public ChargeEventData? Charge { get; set; }

    /// <summary>The whole envelope as received, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }
}

/// <summary>
/// The <c>data</c> of a <c>payment.*</c> event: the payment's transition, its amounts, your
/// correlation fields and, when one was stored with the approval, the saved card.
/// </summary>
/// <remarks>
/// <c>payment.*</c> events carry no sequence: dedupe them on <see cref="WebhookEvent.Id"/> and
/// read the status when order matters. Nullable fields may arrive as null or be absent; both read
/// as null.
/// </remarks>
public sealed class PaymentEventData
{
    /// <summary>
    /// Dominaite's payment id, the same value the create call and the status read use. On
    /// <c>payment.refunded</c> it is the refund's own id; the refunded payment is
    /// <see cref="OriginalTransactionId"/>.
    /// </summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>The status after the transition, one of the <see cref="TransactionStatuses"/> values.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The status before the transition, when there was one.</summary>
    public string? PreviousStatus { get; set; }

    /// <summary>What kind of transaction this is, e.g. <c>sale</c>; null when not classified.</summary>
    public string? Kind { get; set; }

    /// <summary>
    /// What you are PAID, in MINOR units of <see cref="Currency"/>. On <c>payment.refunded</c> it
    /// is the amount of that one refund.
    /// </summary>
    public long Amount { get; set; }

    /// <summary>The card movement in MINOR units: <see cref="Amount"/> plus any surcharge.</summary>
    public long GrossAmount { get; set; }

    /// <summary>The surcharge portion in MINOR units; null when there was none.</summary>
    public long? SurchargeAmount { get; set; }

    /// <summary>ISO 4217 code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// How the payer paid, as a category (<c>card</c>, <c>wallet</c>, ...). Not the saved card:
    /// that is <see cref="StoredPaymentMethod"/>.
    /// </summary>
    public string? PaymentMethod { get; set; }

    /// <summary>The wallet, e.g. <c>apple_pay</c>, on wallet payments only.</summary>
    public string? WalletType { get; set; }

    /// <summary>
    /// The earlier transaction this one hangs off: the refunded payment on
    /// <c>payment.refunded</c>, the authorization on the <c>payment.succeeded</c> of a capture.
    /// Null on events about an ordinary payment itself.
    /// </summary>
    public string? OriginalTransactionId { get; set; }

    /// <summary>The idempotency key you sent on the create call. Null on refund and dispute events.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Your own order reference as stored on the transaction; match events to your orders on it.
    /// Refund and cancel events carry the reference of the original payment.
    /// </summary>
    public string? OrderReference { get; set; }

    /// <summary>The hosted checkout order id, the same value as on the status read.</summary>
    public string? OrderId { get; set; }

    /// <summary>The description you sent on the create call, when you sent one.</summary>
    public string? Description { get; set; }

    /// <summary>Lower-cased card brand once a card payment was attempted.</summary>
    public string? PaymentMethodBrand { get; set; }

    /// <summary>Last four digits of the card once a card payment was attempted.</summary>
    public string? PaymentMethodLast4 { get; set; }

    /// <summary>
    /// The card a <see cref="CheckoutSessionRequest.SaveCard"/> payment stored, with the same shape
    /// and values as <see cref="CheckoutStatus.StoredPaymentMethod"/> on the status read. Null (or
    /// absent) when no card was saved.
    /// </summary>
    /// <remarks>
    /// It can ALSO be null when a card was saved: a card can be stored after the approval was
    /// already announced, for example on a server-to-server sale that succeeded synchronously or
    /// on a sale settled later by the platform. The status read is the source of truth, so on a
    /// SaveCard session whose webhook has this null, read it with
    /// <see cref="DominaiteClient.GetStatusAsync"/>.
    /// </remarks>
    public StoredPaymentMethod? StoredPaymentMethod { get; set; }

    /// <summary>The <c>data</c> object as sent, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }
}

/// <summary>
/// The <c>data</c> of an <c>agreement.*</c> event: the agreement as the agreement routes return
/// it, plus <see cref="PreviousStatus"/> and <see cref="Sequence"/>.
/// </summary>
public sealed class AgreementEventData
{
    /// <summary>The agreement handle, <c>agr_</c> followed by 32 hex characters.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The plan the agreement was created from.</summary>
    public string? PlanId { get; set; }

    /// <summary>Your own customer reference, as sent.</summary>
    public string? CustomerReference { get; set; }

    /// <summary>The card on file (<c>pm_</c>...), when one is attached.</summary>
    public string? StoredPaymentMethodId { get; set; }

    /// <summary>Status after the transition: <c>pending</c>, <c>active</c>, <c>past_due</c>, <c>cancelled</c>, <c>completed</c> or <c>failed</c>.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Status before the transition; null when the agreement was created directly in <see cref="Status"/>.</summary>
    public string? PreviousStatus { get; set; }

    /// <summary>Per-period charge in MINOR units of <see cref="Currency"/>.</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary><c>day</c>, <c>week</c>, <c>month</c> or <c>year</c>.</summary>
    public string? IntervalUnit { get; set; }

    /// <summary>Units per billing period.</summary>
    public int? IntervalCount { get; set; }

    /// <summary>Total periods; null when the agreement bills until cancelled.</summary>
    public int? PeriodCount { get; set; }

    /// <summary>Days between activation and the first charge.</summary>
    public int? TrialDays { get; set; }

    /// <summary>The next scheduled charge (UTC); null while pending and once ended.</summary>
    public DateTimeOffset? NextChargeAt { get; set; }

    /// <summary>When the agreement activated (UTC), when it has.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>When the agreement was cancelled (UTC), when it was.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>The agreement's terms version.</summary>
    public int? Version { get; set; }

    /// <summary>
    /// Per-agreement counter: higher means later. Keep the highest you have processed per
    /// <see cref="OrderingKey"/> and discard an event whose sequence is not higher. 0 comes only
    /// from an event recorded before the counter existed and is older than any positive value;
    /// null means the server predates the field, treat it like 0.
    /// </summary>
    public long? Sequence { get; set; }

    /// <summary>The object <see cref="Sequence"/> counts for: the agreement id.</summary>
    [JsonIgnore]
    public string OrderingKey => this.Id;

    /// <summary>The <c>data</c> object as sent, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }
}

/// <summary>
/// The <c>data</c> of a <c>charge.*</c> event: the charge as the stored-card routes return it,
/// plus the outcome detail and <see cref="Sequence"/>. Fires for your own one-off charges
/// (<see cref="DominaiteClient.ChargePaymentMethodAsync"/>) and for agreement periods the
/// platform charges.
/// </summary>
public sealed class ChargeEventData
{
    /// <summary><c>ch_</c> followed by 32 hex characters.</summary>
    public string ChargeId { get; set; } = string.Empty;

    /// <summary>Dominaite's payment id for this charge.</summary>
    public string? TransactionId { get; set; }

    /// <summary>The card on file the charge was placed on (<c>pm_</c>...).</summary>
    public string? StoredPaymentMethodId { get; set; }

    /// <summary>The agreement the charge belongs to; null for a one-off charge you initiated.</summary>
    public string? AgreementId { get; set; }

    /// <summary>Your customer reference from the agreement; null for a one-off charge.</summary>
    public string? CustomerReference { get; set; }

    /// <summary><c>succeeded</c>, <c>retrying</c> or <c>failed</c>.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>The agreement period the charge is for; null for a one-off charge.</summary>
    public int? PeriodNumber { get; set; }

    /// <summary>Which attempt at that period this was.</summary>
    public int? AttemptNumber { get; set; }

    /// <summary>Charge amount in MINOR units of <see cref="Currency"/>.</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Your order reference, as sent on the charge.</summary>
    public string? OrderReference { get; set; }

    /// <summary>The charge description, when there is one.</summary>
    public string? Description { get; set; }

    /// <summary>One of the <see cref="DeclineClasses"/> values on a decline; null otherwise, or while unclassified.</summary>
    public string? DeclineClass { get; set; }

    /// <summary>The provider's raw decline code, for your logs.</summary>
    public string? DeclineCode { get; set; }

    /// <summary>When the schedule will try again, on <c>charge.retrying</c>.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    /// <summary>The agreement's next scheduled charge after this one.</summary>
    public DateTimeOffset? NextChargeAt { get; set; }

    /// <summary>
    /// Per-object counter: higher means later. Keep the highest you have processed per
    /// <see cref="OrderingKey"/> and discard an event whose sequence is not higher. 0 comes only
    /// from an event recorded before the counter existed and is older than any positive value;
    /// null means the server predates the field, treat it like 0.
    /// </summary>
    public long? Sequence { get; set; }

    /// <summary>
    /// The object <see cref="Sequence"/> counts for: the agreement period
    /// (<c>{AgreementId}/{PeriodNumber}</c>) for a platform charge, the <see cref="ChargeId"/>
    /// for a one-off charge.
    /// </summary>
    [JsonIgnore]
    public string OrderingKey => this.AgreementId is { Length: > 0 } agreementId
        ? $"{agreementId}/{this.PeriodNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        : this.ChargeId;

    /// <summary>The <c>data</c> object as sent, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }
}
