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
