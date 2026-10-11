using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dominaite.MerchantSdk;

/// <summary>
/// The billing interval unit wire values, as constants plus the enumerable
/// <see cref="IntervalUnits.All"/>.
/// </summary>
public static class IntervalUnits
{
    /// <summary>A period of days.</summary>
    public const string Day = "day";

    /// <summary>A period of weeks.</summary>
    public const string Week = "week";

    /// <summary>A period of months.</summary>
    public const string Month = "month";

    /// <summary>A period of years.</summary>
    public const string Year = "year";

    /// <summary>Every interval unit, in the gateway's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Day, Week, Month, Year];
}

/// <summary>
/// The plan status wire values, as constants plus the enumerable <see cref="PlanStatuses.All"/>.
/// Treat a value that is not listed as valid: the API can add statuses later.
/// </summary>
public static class PlanStatuses
{
    /// <summary>The plan takes new agreements.</summary>
    public const string Active = "active";

    /// <summary>
    /// <see cref="DominaiteClient.DeletePlanAsync"/> archived the plan. It takes no new
    /// agreements. The agreements on it keep their own price and continue.
    /// </summary>
    public const string Archived = "archived";

    /// <summary>Every plan status, in the gateway's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Active, Archived];
}

/// <summary>
/// The parameters for <see cref="DominaiteClient.CreatePlanAsync"/>: a price you bill every
/// period.
/// </summary>
/// <remarks>
/// Property order here is the JSON order on the wire, and the body is serialized exactly once:
/// the bytes that are hashed for the signature are the bytes that are sent. Null members are
/// omitted. Amount, currency and interval cannot change after the create call.
/// </remarks>
public sealed class CreatePlanRequest
{
    /// <summary>The plan's display name, 1 to 100 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The charge for one period in MINOR units: 2500 is 25.00 EUR. At least 1.</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 currency, e.g. "EUR".</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>One of the <see cref="IntervalUnits"/> values.</summary>
    public string IntervalUnit { get; set; } = string.Empty;

    /// <summary>
    /// How many units make one period: <see cref="IntervalUnits.Month"/> with 3 bills every three
    /// months. At least 1, and one period is at most one year.
    /// </summary>
    public int IntervalCount { get; set; } = 1;

    /// <summary>
    /// The number of periods, after which each agreement completes. Null bills until the
    /// agreement is cancelled.
    /// </summary>
    public int? PeriodCount { get; set; }

    /// <summary>
    /// The days from activation to the first charge, 0 to 365. Null or 0 is no trial.
    /// </summary>
    public int? TrialDays { get; set; }

    /// <summary>
    /// Your page where a customer cancels the subscription: an absolute https URL, at most 2048
    /// characters. The customer's receipts and decline emails show it. Null if you have none yet.
    /// </summary>
    public string? CancelUrl { get; set; }

    /// <summary>
    /// The idempotency key. Required. It travels in the header and in the signature, never in the
    /// body.
    /// </summary>
    /// <remarks>
    /// The same key with the same body returns the plan made the first time (HTTP 200). The same
    /// key with a different body is refused with <c>IDEMPOTENCY_KEY_REUSED</c> (HTTP 422).
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
/// The parameters for <see cref="DominaiteClient.UpdatePlanAsync"/>. Set only what you change: a
/// null property is left out of the body and keeps its value. Send at least one.
/// </summary>
/// <remarks>
/// Amount, currency and interval cannot change: create a new plan for a new price. The agreements
/// on the plan keep their own copy of the price.
/// </remarks>
public sealed class UpdatePlanRequest
{
    /// <summary>The new display name, 1 to 100 characters.</summary>
    public string? Name { get; set; }

    /// <summary>The new trial in days, 0 to 365, for agreements created from now on. 0 removes the trial.</summary>
    public int? TrialDays { get; set; }

    /// <summary>
    /// The new cancel page: an absolute https URL, at most 2048 characters. An empty string removes
    /// it.
    /// </summary>
    public string? CancelUrl { get; set; }

    /// <summary>
    /// Extra body fields this class does not model yet. Merged into the JSON body at the top
    /// level; a key that collides with a modelled property is ignored.
    /// </summary>
    [JsonIgnore]
    public IDictionary<string, object?> Extra { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>A plan, as the plan routes return it.</summary>
public sealed class Plan
{
    /// <summary>
    /// <c>plan_</c> followed by 32 hex characters. The plan routes take it, and an agreement names
    /// its plan with it.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The charge for one period in MINOR units of <see cref="Currency"/>.</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 currency.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>One of the <see cref="IntervalUnits"/> values, or a value added later.</summary>
    public string IntervalUnit { get; set; } = string.Empty;

    /// <summary>How many units make one period.</summary>
    public int IntervalCount { get; set; }

    /// <summary>The number of periods. Null bills until the agreement is cancelled.</summary>
    public int? PeriodCount { get; set; }

    /// <summary>The days from activation to the first charge. 0 is no trial.</summary>
    public int TrialDays { get; set; }

    /// <summary>One of the <see cref="PlanStatuses"/> values, or a value added later.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Your cancel page, shown on the customer's emails. Null when you gave none.</summary>
    public string? CancelUrl { get; set; }

    /// <summary>When the plan was created (UTC).</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>When the plan was archived (UTC). Null while it is active.</summary>
    public DateTimeOffset? ArchivedAt { get; set; }

    /// <summary>The unwrapped plan as the gateway sent it, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }

    /// <summary>True only for <c>active</c>: the plan takes new agreements.</summary>
    [JsonIgnore]
    public bool IsActive => string.Equals(this.Status, PlanStatuses.Active, StringComparison.Ordinal);
}

/// <summary>
/// Why an agreement is <c>past_due</c>: the wire values of <see cref="Agreement.PastDueReason"/>,
/// as constants plus the enumerable <see cref="PastDueReasons.All"/>. Treat a value that is not
/// listed as valid: the API can add reasons later.
/// </summary>
public static class PastDueReasons
{
    /// <summary>The saved card expired. Ask the customer for a new card.</summary>
    public const string CardExpired = "card_expired";

    /// <summary>The saved card is no longer active, or the agreement has no card. Ask the customer for a new card.</summary>
    public const string CardRevoked = "card_revoked";

    /// <summary>The issuer declined and said not to retry this card.</summary>
    public const string HardDecline = "hard_decline";

    /// <summary>The retries after soft declines ran out.</summary>
    public const string DunningExhausted = "dunning_exhausted";

    /// <summary>
    /// The issuer wants strong customer authentication. The card is fine, but the customer must
    /// pay once with a challenge, in a checkout session.
    /// </summary>
    public const string ScaRequired = "sca_required";

    /// <summary>The platform could not process the charge, and a retry cannot fix it.</summary>
    public const string Unprocessable = "unprocessable";

    /// <summary>Every past-due reason, in the gateway's order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        CardExpired,
        CardRevoked,
        HardDecline,
        DunningExhausted,
        ScaRequired,
        Unprocessable,
    ];
}

/// <summary>
/// The parameters for <see cref="DominaiteClient.CreateAgreementAsync"/>: subscribe one of your
/// customers to a plan.
/// </summary>
/// <remarks>
/// <para>
/// Property order here is the JSON order on the wire, and the body is serialized exactly once:
/// the bytes that are hashed for the signature are the bytes that are sent. Null members are
/// omitted.
/// </para>
/// <para>
/// The agreement starts <c>pending</c> and nothing is charged. A checkout session with
/// <see cref="CheckoutSessionRequest.AgreementId"/> takes the first payment and activates it.
/// </para>
/// </remarks>
public sealed class CreateAgreementRequest
{
    /// <summary>The <c>plan_</c> id of one of your active plans.</summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>
    /// Your own id for the customer, 1 to 100 characters. Keep it the same for the same customer.
    /// </summary>
    public string CustomerReference { get; set; } = string.Empty;

    /// <summary>
    /// The <c>pm_</c> id of a card you already saved, to bill that card. The card must be active
    /// and saved in the plan's currency. Send <see cref="Consent"/> with it. Null when the
    /// subscription session saves the card.
    /// </summary>
    public string? StoredPaymentMethodId { get; set; }

    /// <summary>
    /// The customer's acceptance of the recurring terms, when you took it on your own page. Null
    /// when the subscription session records it.
    /// </summary>
    public AgreementConsent? Consent { get; set; }

    /// <summary>
    /// The most one charge may be, in MINOR units of the plan's currency, when the customer agreed
    /// to a variable amount. At least the plan's amount, and only with <see cref="Consent"/>.
    /// Null for a fixed agreement, which is charged exactly the plan's amount.
    /// </summary>
    /// <remarks>
    /// A checkout session cannot take the first payment of an agreement with a maximum: it
    /// refuses with <c>AGREEMENT_VARIABLE_AMOUNT_UNSUPPORTED</c>.
    /// </remarks>
    public long? MaxAmountPerPeriod { get; set; }

    /// <summary>
    /// The idempotency key. Required. It travels in the header and in the signature, never in the
    /// body.
    /// </summary>
    /// <remarks>
    /// The same key with the same plan, customer, card and maximum returns the agreement made the
    /// first time (HTTP 200); the consent is not compared. The same key with other values is
    /// refused with <c>IDEMPOTENCY_KEY_REUSED</c> (HTTP 422).
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
/// An agreement, as the agreement routes return it. It keeps its own copy of the price, which can
/// differ from the plan's price today.
/// </summary>
public sealed class Agreement
{
    /// <summary><c>agr_</c> followed by 32 hex characters. The agreement routes take it.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The <c>plan_</c> id the agreement was created from.</summary>
    public string PlanId { get; set; } = string.Empty;

    /// <summary>Your own id for the customer.</summary>
    public string CustomerReference { get; set; } = string.Empty;

    /// <summary>The <c>pm_</c> id of the card on file. Null until a card is saved.</summary>
    public string? StoredPaymentMethodId { get; set; }

    /// <summary>One of the <see cref="AgreementStatuses"/> values, or a value added later.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The charge for one period in MINOR units of <see cref="Currency"/>.</summary>
    public long Amount { get; set; }

    /// <summary>
    /// The most one charge may be, in MINOR units, on a variable agreement. Null on a fixed
    /// agreement: every charge is exactly <see cref="Amount"/>.
    /// </summary>
    public long? MaxAmountPerPeriod { get; set; }

    /// <summary>ISO 4217 currency.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>One of the <see cref="IntervalUnits"/> values, or a value added later.</summary>
    public string IntervalUnit { get; set; } = string.Empty;

    /// <summary>How many units make one period.</summary>
    public int IntervalCount { get; set; }

    /// <summary>The number of periods. Null bills until the agreement is cancelled.</summary>
    public int? PeriodCount { get; set; }

    /// <summary>The days from activation to the first charge. 0 is no trial.</summary>
    public int TrialDays { get; set; }

    /// <summary>When the next charge is due (UTC). Null while pending and after the agreement ended.</summary>
    public DateTimeOffset? NextChargeAt { get; set; }

    /// <summary>
    /// Why the agreement is <c>past_due</c>: one of the <see cref="PastDueReasons"/> values, or a
    /// value added later. Null in every other status.
    /// </summary>
    public string? PastDueReason { get; set; }

    /// <summary>When the agreement went past due (UTC). Null in every other status.</summary>
    public DateTimeOffset? PastDueAt { get; set; }

    /// <summary>The recorded consent. Null until the customer gave it.</summary>
    public AgreementConsentRecord? Consent { get; set; }

    /// <summary>
    /// The version of the agreement's terms, starting at 1. A price change makes the next version,
    /// from the next period.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// The agreement's change counter: the <c>data.sequence</c> of the latest
    /// <c>agreement.*</c> webhook. 0 before the first change.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>When the agreement was created (UTC).</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>When the first payment activated the agreement (UTC). Null while pending.</summary>
    public DateTimeOffset? ActivatedAt { get; set; }

    /// <summary>When the agreement was cancelled (UTC). Null unless it was.</summary>
    public DateTimeOffset? CancelledAt { get; set; }

    /// <summary>The unwrapped agreement as the gateway sent it, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }
}

/// <summary>
/// The consent on an <see cref="Agreement"/>: the three values as they were sent, and when the
/// platform received them.
/// </summary>
public sealed class AgreementConsentRecord
{
    /// <summary>The version of the wording the customer accepted.</summary>
    public string TextVersion { get; set; } = string.Empty;

    /// <summary>When the customer accepted, as it was sent.</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>The customer's IP address when they accepted, as it was sent.</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>When the platform received the consent (UTC, the platform's clock).</summary>
    public DateTimeOffset? ReceivedAt { get; set; }

    /// <summary>
    /// The address of the server that sent the consent to the platform (yours, not the
    /// customer's). Null when the platform did not record it.
    /// </summary>
    public string? ReceivedFromIp { get; set; }
}
