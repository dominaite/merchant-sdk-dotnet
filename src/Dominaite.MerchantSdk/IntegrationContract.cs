using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dominaite.MerchantSdk;

/// <summary>
/// What <see cref="DominaiteClient.GetIntegrationContractAsync"/> returns: the public integration
/// contract of the API you call.
/// </summary>
/// <remarks>
/// This class models the values an integration reads most. <see cref="Raw"/> has the whole
/// contract: the signing recipes, the field lists, the error codes and the notes.
/// </remarks>
public sealed class IntegrationContract
{
    /// <summary>The contract version, a date stamp, for example <c>2026-09-16</c>.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>The API base URL of this environment.</summary>
    public string ApiBase { get; set; } = string.Empty;

    /// <summary>The transaction status wire values. <see cref="TransactionStatuses"/> has them as constants.</summary>
    public IReadOnlyList<string> Statuses { get; set; } = [];

    /// <summary>
    /// The event types a webhook endpoint can receive. <see cref="WebhookEventTypes"/> has them as
    /// constants.
    /// </summary>
    public IReadOnlyList<string> WebhookEventCatalog { get; set; } = [];

    /// <summary>The subscription values. Null from a server without subscriptions.</summary>
    public IntegrationContractRecurring? Recurring { get; set; }

    /// <summary>The unwrapped contract as the gateway sent it, with every field this class does not model.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }

    /// <summary>
    /// The id the API gave this request: the <c>Dominaite-Request-Id</c> header, else the body's
    /// <c>metadata.requestId</c>. Null when the API sent no id. Quote it to Dominaite support.
    /// </summary>
    [JsonIgnore]
    public string? RequestId { get; internal set; }
}

/// <summary>The subscription part of the <see cref="IntegrationContract"/>.</summary>
public sealed class IntegrationContractRecurring
{
    /// <summary>
    /// The platform consent wording versions a subscription session accepts, newest first. The
    /// first one is the wording the payment page shows today. Send it as
    /// <see cref="AgreementConsent.TextVersion"/>.
    /// </summary>
    public IReadOnlyList<string> ConsentTextVersions { get; set; } = [];

    /// <summary>The interval unit wire values. <see cref="MerchantSdk.IntervalUnits"/> has them as constants.</summary>
    public IReadOnlyList<string> IntervalUnits { get; set; } = [];

    /// <summary>The plan status wire values. <see cref="MerchantSdk.PlanStatuses"/> has them as constants.</summary>
    public IReadOnlyList<string> PlanStatuses { get; set; } = [];

    /// <summary>
    /// The agreement status wire values. <see cref="MerchantSdk.AgreementStatuses"/> has them as
    /// constants.
    /// </summary>
    public IReadOnlyList<string> AgreementStatuses { get; set; } = [];
}
