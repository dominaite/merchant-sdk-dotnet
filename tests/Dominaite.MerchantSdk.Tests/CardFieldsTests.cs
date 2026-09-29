using System.Text.Json;
using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// Card fields on a checkout session: the <c>integration</c> request field, and the echoed
/// <c>integration</c> and <c>clientSecret</c> on the response, pinned against the canonical fixture.
/// </summary>
public class CardFieldsTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string IdempotencyKey = "checkout-order-1042-2500-EUR";

    private static JsonElement Contract()
        => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "merchant-api-contract.json")))
            .RootElement.Clone();

    private static JsonElement CreateEndpoint()
        => Contract().GetProperty("endpoints").GetProperty("createCheckoutSession");

    private static Reply Example(string name)
        => Reply.Enveloped(CreateEndpoint().GetProperty(name).GetRawText());

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

    private static CheckoutSessionRequest Request(string? integration) => new()
    {
        Amount = 2500,
        Currency = "EUR",
        OrderReference = "order-1042",
        Integration = integration,
        IdempotencyKey = IdempotencyKey,
    };

    /// <summary>
    /// Asking for card fields must reach the gateway in the signed body, or the merchant gets a
    /// widget session and a page that cannot mount card fields.
    /// </summary>
    [Fact]
    public async Task FieldsIsSentInTheSignedBody()
    {
        using var server = new MockServer(Example("fieldsSuccessExample"));
        using var client = ClientFor(server);

        await client.CreateCheckoutSessionAsync(Request(CheckoutIntegrations.Fields));

        var sent = server.LastRequest;
        Assert.Equal(
            """{"amount":2500,"currency":"EUR","orderReference":"order-1042","integration":"fields"}""",
            sent.Body);
        var expected = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = sent.Header("X-Timestamp")!,
            Method = sent.Method,
            Path = DominaiteClient.SessionsPath,
            IdempotencyKey = IdempotencyKey,
            Body = sent.Body,
        });
        Assert.Equal(expected, sent.Header("X-Signature"));
    }

    [Fact]
    public async Task WidgetIsSentWhenSetExplicitly()
    {
        using var server = new MockServer(Example("successExample"));
        using var client = ClientFor(server);

        await client.CreateCheckoutSessionAsync(Request(CheckoutIntegrations.Widget));

        Assert.Equal(
            """{"amount":2500,"currency":"EUR","orderReference":"order-1042","integration":"widget"}""",
            server.LastRequest.Body);
    }

    /// <summary>
    /// A request that never sets Integration is omitted, not sent as null, so the signed bytes of
    /// every existing widget integration do not move.
    /// </summary>
    [Fact]
    public async Task AnUnsetIntegrationIsOmittedFromTheBody()
    {
        using var server = new MockServer(Example("successExample"));
        using var client = ClientFor(server);

        await client.CreateCheckoutSessionAsync(Request(null));

        Assert.Equal(
            """{"amount":2500,"currency":"EUR","orderReference":"order-1042"}""",
            server.LastRequest.Body);
    }

    [Fact]
    public void TheIntegrationVocabularyMatchesTheContract()
    {
        var vocabulary = Contract().GetProperty("integrationVocabulary")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();

        Assert.Equal(vocabulary, CheckoutIntegrations.All);
    }

    /// <summary>
    /// The fields session is what the drop-in is mounted with: every one of these values goes to
    /// the payer's page, so a dropped one is a dead checkout.
    /// </summary>
    [Fact]
    public async Task TheFieldsExampleComesBackWithIntegrationAndClientSecret()
    {
        using var server = new MockServer(Example("fieldsSuccessExample"));
        using var client = ClientFor(server);

        var session = await client.CreateCheckoutSessionAsync(Request(CheckoutIntegrations.Fields));

        Assert.Equal(CheckoutIntegrations.Fields, session.Integration);
        Assert.Equal("cs_4f3e2d1c0b9a8f7e6d5c4b3a2f1e0d9c", session.ClientSecret);
        Assert.Equal("7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d", session.TransactionId);
        Assert.Equal("ck_live_blox_8c7d6e5f4a3b2c1d", session.CashierKey);
        Assert.Equal("ctok_blox_0a1b2c3d4e5f6a7b", session.CashierToken);
    }

    [Fact]
    public async Task TheWidgetExampleEchoesWidgetWithoutASecret()
    {
        using var server = new MockServer(Example("successExample"));
        using var client = ClientFor(server);

        var session = await client.CreateCheckoutSessionAsync(Request(null));

        Assert.Equal(CheckoutIntegrations.Widget, session.Integration);
        Assert.Null(session.ClientSecret);
    }

    /// <summary>
    /// The fields example carries every checkout field; the widget example carries all of them
    /// except clientSecret, which exists only for fields.
    /// </summary>
    [Fact]
    public void TheExamplesCarryTheCheckoutFields()
    {
        var create = CreateEndpoint();
        var fields = create.GetProperty("checkoutFields")
            .EnumerateArray()
            .Select(item => item.GetString()!)
            .ToList();

        List<string> Keys(string example)
            => [.. create.GetProperty(example).GetProperty("checkout")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order()];

        Assert.Equal(fields.Order().ToList(), Keys("fieldsSuccessExample"));
        Assert.Equal(fields.Where(name => name != "clientSecret").Order().ToList(), Keys("successExample"));
    }

    /// <summary>
    /// Card fields on an account without them is a 400 INVALID_SELECTION. It has to arrive as a
    /// machine-readable code, not as a refusal: nothing was created.
    /// </summary>
    [Fact]
    public async Task FieldsNotEnabledSurfacesAsAnApiExceptionWithItsCode()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(
            400,
            "INVALID_SELECTION",
            "Card fields are not enabled for this account. Ask Dominaite support to enable them."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.CreateCheckoutSessionAsync(Request(CheckoutIntegrations.Fields)));

        Assert.Equal(400, error.HttpStatus);
        Assert.Equal("INVALID_SELECTION", error.Code);
    }

    /// <summary>The client secret is a per-session bearer value. It must not ride along in a log line.</summary>
    [Fact]
    public void TheClientSecretNeverAppearsInToString()
    {
        var session = JsonSerializer.Deserialize<CheckoutSession>(
            """{"transactionId":"t","integration":"fields","clientSecret":"cs_secret_value"}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.DoesNotContain("cs_secret_value", session.ToString(), StringComparison.Ordinal);
    }
}
