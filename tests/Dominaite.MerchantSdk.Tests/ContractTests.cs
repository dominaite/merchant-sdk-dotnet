using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The response contract, pinned against the canonical fixture.
/// </summary>
/// <remarks>
/// <c>merchant-api-contract.json</c> next to this file is a byte-identical copy of the one every
/// Dominaite SDK vendors. It is generated from the gateway DTOs; do NOT edit it here to make a
/// test pass - a mismatch means either this SDK drifted or the gateway changed, and both are
/// fixed somewhere other than the fixture.
/// </remarks>
public class ContractTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>The sha256 of the canonical fixture, shared across every SDK that vendors it.</summary>
    private const string FixtureSha256 = "d8f4b47b1c371e1ebf99bf1a0a77586a8743b00f20ad265b641f6665a5a5c172";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static byte[] FixtureBytes()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "merchant-api-contract.json"));

    private static JsonElement Contract()
        => JsonDocument.Parse(FixtureBytes()).RootElement.Clone();

    private static JsonElement Endpoint(string name)
        => Contract().GetProperty("endpoints").GetProperty(name);

    private static List<string> Strings(JsonElement array)
        => [.. array.EnumerateArray().Select(item => item.GetString()!)];

    /// <summary>
    /// The JSON property names a response type actually exposes on the wire: public properties,
    /// minus the ones marked <see cref="JsonIgnoreAttribute"/>, named the way the client's
    /// serializer names them. Read off the type rather than a hand-maintained copy of it.
    /// </summary>
    private static List<string> WireFields<T>()
        => [.. typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>() is null)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name))];

    private static void AssertFields<T>(IEnumerable<string> expected)
        => Assert.Equal(expected.Order().ToList(), WireFields<T>().Order().ToList());

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

    private static CheckoutSessionRequest Request() => new()
    {
        Amount = 8440,
        Currency = "EUR",
        OrderReference = "order-1042",
    };

    /// <summary>
    /// The vendored fixture must be byte-identical to the canonical one. Editing it locally to
    /// make a test pass is the exact failure mode the pin exists to catch.
    /// </summary>
    [Fact]
    public void TheVendoredFixtureIsByteIdentical()
    {
        var digest = Convert.ToHexString(SHA256.HashData(FixtureBytes())).ToLowerInvariant();
        Assert.Equal(FixtureSha256, digest);
    }

    [Fact]
    public void TheStatusVocabularyIsExactlyTheContracts()
    {
        Assert.Equal(Strings(Contract().GetProperty("statusVocabulary")), TransactionStatuses.All);
    }

    [Fact]
    public void EveryContractStatusRoundTripsThroughTheStatusResponse()
    {
        var example = Endpoint("getStatus").GetProperty("example");

        foreach (var value in Strings(Contract().GetProperty("statusVocabulary")))
        {
            var payload = JsonNode.Parse(example.GetRawText())!;
            payload["status"] = value;

            var status = JsonSerializer.Deserialize<CheckoutStatus>(payload.ToJsonString(), ReadOptions)!;

            Assert.Equal(value, status.Status);
            Assert.Equal(value == TransactionStatuses.Succeeded, status.IsPaid);
        }
    }

    /// <summary>
    /// Not in the fixture on purpose: the contract can grow, and a status this SDK has never heard
    /// of must keep the caller polling rather than close an order that is still open.
    /// </summary>
    [Fact]
    public void AnUnknownStatusStaysNonTerminal()
    {
        var payload = JsonNode.Parse(Endpoint("getStatus").GetProperty("example").GetRawText())!;
        payload["status"] = "chargeback_reversed";

        var status = JsonSerializer.Deserialize<CheckoutStatus>(payload.ToJsonString(), ReadOptions)!;

        Assert.False(status.IsPaid);
        Assert.False(status.IsTerminal);
    }

    /// <summary>
    /// requires_capture is the trap: the payer HAS paid, the money is held awaiting capture, and
    /// there is no checkout window left. Neither paid nor finished, and never an abandoned order.
    /// </summary>
    [Fact]
    public void RequiresCaptureIsNeitherPaidNorTerminal()
    {
        var payload = JsonNode.Parse(Endpoint("getStatus").GetProperty("example").GetRawText())!;
        payload["status"] = TransactionStatuses.RequiresCapture;

        var status = JsonSerializer.Deserialize<CheckoutStatus>(payload.ToJsonString(), ReadOptions)!;

        Assert.False(status.IsPaid);
        Assert.False(status.IsTerminal);
    }

    [Fact]
    public void PingMatchesTheContract()
    {
        var ping = Endpoint("ping");
        AssertFields<PingResponse>(Strings(ping.GetProperty("fields")));

        var parsed = JsonSerializer.Deserialize<PingResponse>(ping.GetProperty("example").GetRawText(), ReadOptions)!;

        Assert.True(parsed.Pong);
        Assert.Equal("6f2b6a1e-0c4d-4e8a-9b1c-2d3e4f5a6b70", parsed.MerchantId);
        Assert.Equal(1755767730, parsed.ServerUnixTime);
        Assert.Equal(2, parsed.ClockSkewSeconds);
    }

    [Fact]
    public void TheCheckoutObjectMatchesTheContract()
    {
        var create = Endpoint("createCheckoutSession");
        AssertFields<CheckoutSession>(Strings(create.GetProperty("checkoutFields")));

        var checkout = create.GetProperty("successExample").GetProperty("checkout");
        var parsed = JsonSerializer.Deserialize<CheckoutSession>(checkout.GetRawText(), ReadOptions)!;

        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", parsed.TransactionId);
        Assert.Equal("dom_9a8b7c6d5e4f", parsed.OrderId);
        Assert.Equal("ck_live_2f3a4d5e6f708192", parsed.CashierKey);
        Assert.Equal("ctok_5e4f3a2b1c0d9e8f", parsed.CashierToken);
        Assert.Equal(8440, parsed.Amount);
        Assert.Equal("EUR", parsed.Currency);
        Assert.Equal(DateTimeOffset.Parse("2026-08-21T11:15:30.000Z"), parsed.ExpiresAt);
    }

    [Fact]
    public void GetStatusMatchesTheContract()
    {
        var getStatus = Endpoint("getStatus");
        AssertFields<CheckoutStatus>(Strings(getStatus.GetProperty("fields")));

        var parsed = JsonSerializer.Deserialize<CheckoutStatus>(
            getStatus.GetProperty("example").GetRawText(),
            ReadOptions)!;

        Assert.Equal("dom_9a8b7c6d5e4f", parsed.OrderId);
        Assert.Equal("order-1042", parsed.OrderReference);
        Assert.Equal(TransactionStatuses.Succeeded, parsed.Status);
        Assert.Equal(8440, parsed.Amount);
        Assert.Equal("EUR", parsed.Currency);

        // Null in the example, and null is not zero: nothing was refunded, and the SDK must not
        // invent a 0 that reads as "a refund of nothing happened".
        Assert.Null(parsed.RefundedAmount);
        Assert.Equal(PaymentMethodCategories.Wallet, parsed.PaymentMethod);
        Assert.Equal(WalletTypes.ApplePay, parsed.WalletType);
        Assert.Equal(DateTimeOffset.Parse("2026-08-21T09:15:30.000Z"), parsed.CreatedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-08-21T09:16:05.000Z"), parsed.UpdatedAt);

        // Terminal, so there is no window left for the payer to act in.
        Assert.Null(parsed.ExpiresAt);
        Assert.True(parsed.IsPaid);
        Assert.True(parsed.IsTerminal);
    }

    /// <summary>
    /// The fixture spells the nullable fields out as present-null, but the gateway serializes with
    /// WhenWritingNull and OMITS them. Both shapes have to land in the same object, so the absent
    /// case gets its own assertion rather than riding on the fixture's.
    /// </summary>
    [Fact]
    public void OmittedNullableFieldsDeserializeTheSameAsExplicitNulls()
    {
        var trimmed = """
            {"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","status":"pending","amount":8440,"currency":"EUR","createdAt":"2026-08-21T09:15:30.000Z"}
            """;

        var parsed = JsonSerializer.Deserialize<CheckoutStatus>(trimmed, ReadOptions)!;

        Assert.Null(parsed.OrderReference);
        Assert.Null(parsed.RefundedAmount);
        Assert.Null(parsed.PaymentMethod);
        Assert.Null(parsed.WalletType);
        Assert.Null(parsed.UpdatedAt);
        Assert.Null(parsed.ExpiresAt);
        Assert.False(parsed.IsTerminal);
    }

    /// <summary>
    /// The create envelope is the one shape with a success flag of its own, and the SDK splits it
    /// into a return value or a throw rather than handing the flag to the caller. So the field list
    /// is asserted against the two examples the fixture pins, and the split itself through the
    /// client.
    /// </summary>
    [Fact]
    public void TheCreateEnvelopeCarriesExactlyTheContractFields()
    {
        var create = Endpoint("createCheckoutSession");
        var expected = Strings(create.GetProperty("fields")).Order().ToList();

        foreach (var name in new[] { "successExample", "refusalExample" })
        {
            var keys = create.GetProperty(name)
                .EnumerateObject()
                .Select(property => property.Name)
                .Order()
                .ToList();

            Assert.Equal(expected, keys);
        }
    }

    [Fact]
    public async Task TheSuccessExampleComesBackAsASession()
    {
        var create = Endpoint("createCheckoutSession");
        using var server = new MockServer(Reply.Enveloped(create.GetProperty("successExample").GetRawText()));
        using var client = ClientFor(server);

        var session = await client.CreateCheckoutSessionAsync(Request());

        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", session.TransactionId);
        Assert.Equal("ctok_5e4f3a2b1c0d9e8f", session.CashierToken);
    }

    [Fact]
    public async Task TheRefusalExampleComesBackAsARefusalWithItsTransaction()
    {
        var create = Endpoint("createCheckoutSession");
        using var server = new MockServer(Reply.Enveloped(create.GetProperty("refusalExample").GetRawText()));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRefusalException>(
            () => client.CreateCheckoutSessionAsync(Request()));

        // HTTP 200 all the way, and never retryable: it will not change on its own.
        Assert.False(error.IsRetryable);
        Assert.Equal("DUPLICATE_REQUEST", error.Code);
        Assert.Equal("An identical request was already processed.", error.Message);

        // The recovery path: read the colliding payment back instead of minting a second one.
        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", error.TransactionId);
    }

    [Fact]
    public async Task EveryContractRefusalCodeSurvivesAsARefusal()
    {
        var codes = Strings(Contract().GetProperty("sessionRefusalErrorCodes"));
        Assert.Equal(5, codes.Count);
        Assert.Contains("PRIOR_ATTEMPT_FAILED", codes);

        // An unlisted code rides along too: the gateway can add one, and it must still arrive as a
        // refusal with its code intact rather than as a generic API error nobody can branch on.
        foreach (var code in codes.Append("A_NEW_CODE"))
        {
            using var server = new MockServer(Reply.Enveloped(
                $$"""{"success":false,"errorCode":"{{code}}","errorMessage":"refused"}"""));
            using var client = ClientFor(server);

            var error = await Assert.ThrowsAsync<DominaiteRefusalException>(
                () => client.CreateCheckoutSessionAsync(Request()));

            Assert.Equal(code, error.Code);
            Assert.False(error.IsRetryable);
        }
    }

    /// <summary>
    /// Validation codes are the other half of the create endpoint's error surface, and they are a
    /// different shape: a real HTTP 400 rather than the 200 with success=false that carries a
    /// refusal. Both must stay machine-readable and must not collapse into each other - a caller
    /// that treats a rejected request as a refused payment goes looking for a transaction that was
    /// never created.
    /// </summary>
    [Fact]
    public async Task EveryContractValidationCodeSurvivesWithItsStatus()
    {
        var codes = Strings(Contract().GetProperty("validationErrorCodes"));
        Assert.NotEmpty(codes);

        foreach (var code in codes)
        {
            using var server = new MockServer(Reply.ErrorEnvelope(400, code, "rejected"));
            using var client = ClientFor(server);

            var error = await Assert.ThrowsAsync<DominaiteApiException>(
                () => client.CreateCheckoutSessionAsync(Request()));

            Assert.Equal(code, error.Code);
            Assert.Equal(400, error.HttpStatus);
            Assert.False(error.IsRetryable);
        }
    }
}
