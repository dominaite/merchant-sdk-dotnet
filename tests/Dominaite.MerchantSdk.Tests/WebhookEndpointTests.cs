using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The webhook endpoint routes against the loopback server: what each call sends and signs, and
/// what it reads back. The signing secret is on the first create answer only.
/// </summary>
public class WebhookEndpointTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string WebhookId = "6f1c2d3e-4a5b-4c6d-8e7f-9a0b1c2d3e4f";
    private const string WebhookKey = "webhook-orders-endpoint";
    private const string SigningSecret = "whsec_cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd";

    private const string CreatedEndpoint = """
        {"id":"6f1c2d3e-4a5b-4c6d-8e7f-9a0b1c2d3e4f","url":"https://shop.example.com/webhooks/dominaite","events":["payment.succeeded","payment.refunded"],"status":"active","createdAt":"2026-10-01T08:00:00Z","secret":"whsec_cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd"}
        """;

    private const string ReplayedEndpoint = """
        {"id":"6f1c2d3e-4a5b-4c6d-8e7f-9a0b1c2d3e4f","url":"https://shop.example.com/webhooks/dominaite","events":["payment.succeeded","payment.refunded"],"status":"active","createdAt":"2026-10-01T08:00:00Z"}
        """;

    private static string WebhookPath => $"{DominaiteClient.WebhooksPath}/{WebhookId}";

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

    private static Reply Data(int status, string data) => Reply.Raw(status, $$"""{"success":true,"data":{{data}}}""");

    private static CreateWebhookEndpointRequest Request() => new()
    {
        Url = "https://shop.example.com/webhooks/dominaite",
        Events = [WebhookEventTypes.PaymentSucceeded, WebhookEventTypes.PaymentRefunded],
        IdempotencyKey = WebhookKey,
    };

    private static void AssertSignatureMatches(RecordedRequest sent, string canonicalPath, string idempotencyKey)
    {
        var expected = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = sent.Header("X-Timestamp")!,
            Method = sent.Method,
            Path = canonicalPath,
            IdempotencyKey = idempotencyKey,
            Body = sent.Body,
        });

        Assert.Equal(expected, sent.Header("X-Signature"));
    }

    [Fact]
    public async Task CreateSendsTheUrlAndEventsAndReadsTheSecretOnce()
    {
        using var server = new MockServer(Data(201, CreatedEndpoint), Data(201, ReplayedEndpoint));
        using var client = ClientFor(server);

        var request = Request();
        request.Extra["description"] = "orders";
        var endpoint = await client.CreateWebhookEndpointAsync(request);

        var sent = server.LastRequest;
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/api" + DominaiteClient.WebhooksPath, sent.Path);
        Assert.Equal(
            """{"url":"https://shop.example.com/webhooks/dominaite","events":["payment.succeeded","payment.refunded"],"description":"orders"}""",
            sent.Body);
        Assert.Equal(WebhookKey, sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, DominaiteClient.WebhooksPath, WebhookKey);

        Assert.Equal(WebhookId, endpoint.Id);
        Assert.Equal(SigningSecret, endpoint.Secret);
        Assert.True(endpoint.IsActive);
        Assert.Equal(new[] { WebhookEventTypes.PaymentSucceeded, WebhookEventTypes.PaymentRefunded }, endpoint.Events);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), endpoint.CreatedAt);

        var replay = await client.CreateWebhookEndpointAsync(Request());
        Assert.Equal(WebhookId, replay.Id);
        Assert.Null(replay.Secret);
    }

    [Fact]
    public void TheEndpointDescriptionNeverShowsTheSecret()
    {
        var endpoint = new WebhookEndpoint { Id = WebhookId, Secret = SigningSecret };

        Assert.DoesNotContain(SigningSecret, endpoint.ToString(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", endpoint.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateRefusesAMissingKeyBeforeSending()
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        var request = Request();
        request.IdempotencyKey = null;
        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.CreateWebhookEndpointAsync(request));

        Assert.Empty(server.Requests);
    }

    /// <summary>
    /// A key reused after its replay window names the endpoint it registered, so the caller can
    /// find it instead of registering a second one.
    /// </summary>
    [Fact]
    public async Task AlreadyRegisteredCarriesTheIdOfTheExistingEndpoint()
    {
        using var server = new MockServer(Reply.Raw(
            409,
            """{"success":false,"error":{"code":"WEBHOOK_ALREADY_REGISTERED","message":"An endpoint registered with this Idempotency-Key still exists; its id is in params.webhookId. Use a fresh key to register another endpoint.","statusCode":409,"validationErrors":[{"field":"idempotencyKey","code":"WEBHOOK_ALREADY_REGISTERED","message":"An endpoint registered with this Idempotency-Key still exists.","params":{"webhookId":"6f1c2d3e-4a5b-4c6d-8e7f-9a0b1c2d3e4f"}}],"params":{"webhookId":"6f1c2d3e-4a5b-4c6d-8e7f-9a0b1c2d3e4f"}}}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(() => client.CreateWebhookEndpointAsync(Request()));

        Assert.Equal("WEBHOOK_ALREADY_REGISTERED", error.Code);
        Assert.Equal(409, error.HttpStatus);
        Assert.Equal(WebhookKey, error.IdempotencyKey);
        Assert.Equal(WebhookId, error.RawResult.GetProperty("error").GetProperty("params").GetProperty("webhookId").GetString());
    }

    [Fact]
    public async Task ARejectedUrlIsAnApiErrorWithItsCode()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(400, "WEBHOOK_URL_REJECTED", "The webhook URL was rejected"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(() => client.CreateWebhookEndpointAsync(Request()));

        Assert.Equal("WEBHOOK_URL_REJECTED", error.Code);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task TheListSignsAGetWithNoKeyAndReadsEveryEndpointWithoutASecret()
    {
        using var server = new MockServer(Data(
            200,
            $$"""[{{ReplayedEndpoint.Trim()}},{"id":"0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d","url":"https://old.example.com/hook","events":["agreement.past_due"],"status":"inactive","createdAt":"2026-09-01T08:00:00Z"}]"""));
        using var client = ClientFor(server);

        var endpoints = await client.ListWebhookEndpointsAsync();

        var sent = server.LastRequest;
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api" + DominaiteClient.WebhooksPath, sent.Path);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, DominaiteClient.WebhooksPath, string.Empty);

        Assert.Equal(2, endpoints.Count);
        Assert.All(endpoints, endpoint => Assert.Null(endpoint.Secret));
        Assert.False(endpoints[1].IsActive);
        Assert.Equal(WebhookEndpointStatuses.Inactive, endpoints[1].Status);
        Assert.Equal("https://old.example.com/hook", endpoints[1].Raw.GetProperty("url").GetString());
    }

    [Fact]
    public async Task TheListNeedsTheWebhooksScope()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(403, PermissionErrorCodes.InsufficientScope, "This API key does not have the 'webhooks:manage' scope this route requires"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaitePermissionException>(() => client.ListWebhookEndpointsAsync());

        Assert.Equal(PermissionErrorCodes.InsufficientScope, error.Code);
        Assert.Contains("webhooks:manage", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The gateway signs the id as a lowercase UUID, whatever case the route carried.</summary>
    [Fact]
    public async Task DeleteSendsAndSignsTheIdAsALowercaseUuid()
    {
        using var server = new MockServer(Reply.Raw(204, string.Empty));
        using var client = ClientFor(server);

        await client.DeleteWebhookEndpointAsync(" 6F1C2D3E-4A5B-4C6D-8E7F-9A0B1C2D3E4F ");

        var sent = server.LastRequest;
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal("/api" + WebhookPath, sent.Path);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, WebhookPath, string.Empty);
    }

    [Fact]
    public async Task DeleteOfAnUnknownEndpointIsAnApiErrorWithItsCode()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(404, "WEBHOOK_NOT_FOUND", "No webhook endpoint with this id under your account."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(() => client.DeleteWebhookEndpointAsync(WebhookId));

        Assert.Equal("WEBHOOK_NOT_FOUND", error.Code);
        Assert.Equal(404, error.HttpStatus);
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("6f1c2d3e4a5b4c6d8e7f9a0b1c2d3e4f")]
    [InlineData("")]
    public async Task DeleteRefusesAnIdThatIsNotAUuidBeforeSending(string webhookId)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.DeleteWebhookEndpointAsync(webhookId));

        Assert.Empty(server.Requests);
    }
}
