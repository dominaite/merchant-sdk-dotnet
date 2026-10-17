using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dominaite.MerchantSdk;

/// <summary>
/// The webhook endpoint status wire values, as constants plus the enumerable
/// <see cref="WebhookEndpointStatuses.All"/>. Treat a value that is not listed as valid: the API
/// can add statuses later.
/// </summary>
public static class WebhookEndpointStatuses
{
    /// <summary>The endpoint receives deliveries.</summary>
    public const string Active = "active";

    /// <summary>
    /// The endpoint receives no deliveries: it was switched off in the dashboard, or after repeated
    /// delivery failures.
    /// </summary>
    public const string Inactive = "inactive";

    /// <summary>Every webhook endpoint status, in the gateway's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Active, Inactive];
}

/// <summary>
/// The parameters for <see cref="DominaiteClient.CreateWebhookEndpointAsync"/>: where to deliver,
/// and which events.
/// </summary>
/// <remarks>
/// Property order here is the JSON order on the wire, and the body is serialized exactly once:
/// the bytes that are hashed for the signature are the bytes that are sent. The platform makes the
/// signing secret; you cannot send one.
/// </remarks>
public sealed class CreateWebhookEndpointRequest
{
    /// <summary>
    /// The https URL the deliveries are POSTed to, at most 500 characters. A private, loopback or
    /// metadata host is refused with <c>WEBHOOK_URL_REJECTED</c>.
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// The event types to receive, 1 to 50, each one exactly as the event catalog writes it: the
    /// <see cref="WebhookEventTypes"/> values, or <c>webhookEventCatalog</c> in the integration
    /// contract.
    /// </summary>
    public IList<string> Events { get; set; } = [];

    /// <summary>
    /// The idempotency key. Required. It travels in the header and in the signature, never in the
    /// body.
    /// </summary>
    /// <remarks>
    /// A retry with the same key and body returns the same endpoint, without the secret. Only the
    /// first answer has the secret.
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

/// <summary>A webhook endpoint, as the webhook endpoint routes return it.</summary>
public sealed class WebhookEndpoint
{
    /// <summary>The endpoint id, a lowercase UUID. <see cref="DominaiteClient.DeleteWebhookEndpointAsync"/> takes it.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The URL the deliveries go to.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>The event types the endpoint receives.</summary>
    public IReadOnlyList<string> Events { get; set; } = [];

    /// <summary>One of the <see cref="WebhookEndpointStatuses"/> values, or a value added later.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>When the endpoint was registered (UTC).</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    /// The <c>whsec_</c> signing secret. Only the first answer of
    /// <see cref="DominaiteClient.CreateWebhookEndpointAsync"/> has it: store it then. A replay, the
    /// list and every other route leave it null.
    /// </summary>
    /// <remarks>
    /// If you lose the secret, delete the endpoint and register it again. Verify each delivery with
    /// <see cref="Webhooks.VerifyAndParse(string, string, string, int, long?)"/> and this secret.
    /// </remarks>
    public string? Secret { get; set; }

    /// <summary>
    /// The unwrapped endpoint as the gateway sent it, for fields this class does not model yet. On
    /// the first create answer it holds the secret too.
    /// </summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }

    /// <summary>
    /// The id the API gave this request: the <c>Dominaite-Request-Id</c> header, else the body's
    /// <c>metadata.requestId</c>. Null when the API sent no id. Quote it to Dominaite support.
    /// </summary>
    [JsonIgnore]
    public string? RequestId { get; internal set; }

    /// <summary>True only for <c>active</c>: the endpoint receives deliveries.</summary>
    [JsonIgnore]
    public bool IsActive => string.Equals(this.Status, WebhookEndpointStatuses.Active, StringComparison.Ordinal);

    /// <summary>A description with the secret redacted.</summary>
    /// <returns>The redacted description.</returns>
    public override string ToString()
        => $"WebhookEndpoint {{ Id = {this.Id}, Url = {this.Url}, Status = {this.Status}, Secret = {(this.Secret is null ? "null" : "[REDACTED]")} }}";
}
