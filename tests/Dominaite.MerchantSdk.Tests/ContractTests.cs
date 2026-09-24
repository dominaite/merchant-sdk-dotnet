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
/// <remarks>
/// Besides the session surface, this pins the stored-payment-method vocabularies, field sets
/// and examples: the saved-card status, a 201 charge, a 402 decline, every coded charge and
/// revoke error, the bodiless revoke. Every example is pushed through the client twice: as
/// spelled, and with its null members removed, because the gateway omits null fields on the
/// wire.
/// </remarks>
public class ContractTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>The sha256 of the canonical fixture, shared across every SDK that vendors it.</summary>
    private const string FixtureSha256 = "8bd0b6037f245d1c3d4e6c01aad42bef666a66f086f03c6b00a75e5023f88f20";

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
        IdempotencyKey = "checkout-order-1042-8440-EUR",
    };

    private const string PaymentMethodId = "pm_0123456789abcdef0123456789abcdef";

    private static ChargeRequest Charge() => new()
    {
        Amount = 2500,
        Currency = "EUR",
        OrderReference = "order-1043",
        IdempotencyKey = "charge-order-1043-2500-EUR",
    };

    /// <summary>
    /// The example with every null object member removed, recursively: the gateway's serializer
    /// omits nulls, so this is what actually crosses the wire.
    /// </summary>
    private static JsonNode? WithoutNulls(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                var stripped = new JsonObject();
                foreach (var (key, member) in members)
                {
                    if (member is not null)
                    {
                        stripped[key] = WithoutNulls(member);
                    }
                }

                return stripped;
            case JsonArray items:
                return new JsonArray([.. items.Select(item => WithoutNulls(item))]);
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>An example in both wire forms, labelled.</summary>
    private static IEnumerable<(string Form, string Body)> BothWireForms(JsonElement example)
    {
        yield return ("as spelled", example.GetRawText());
        yield return ("without nulls", WithoutNulls(JsonNode.Parse(example.GetRawText()))!.ToJsonString());
    }

    private static List<string> Keys(JsonElement value)
        => [.. value.EnumerateObject().Select(property => property.Name).Order()];

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

    [Fact]
    public void TheStoredPaymentMethodStatusVocabularyIsExactlyTheContracts()
    {
        Assert.Equal(Strings(Contract().GetProperty("storedPaymentMethodStatusVocabulary")), StoredPaymentMethodStatuses.All);
    }

    [Fact]
    public void TheChargeVocabulariesAreExactlyTheContracts()
    {
        var contract = Contract();
        Assert.Equal(Strings(contract.GetProperty("chargeStatusVocabulary")), ChargeStatuses.All);
        Assert.Equal(Strings(contract.GetProperty("declineClassVocabulary")), DeclineClasses.All);
        Assert.Equal(Strings(contract.GetProperty("chargeErrorCodes")), ChargeErrorCodes.All);
        Assert.Equal(Strings(contract.GetProperty("revokeErrorCodes")), RevokeErrorCodes.All);
    }

    [Fact]
    public void TheStoredPaymentMethodObjectMatchesTheContract()
    {
        var getStatus = Endpoint("getStatus");
        AssertFields<StoredPaymentMethod>(Strings(getStatus.GetProperty("storedPaymentMethodFields")));

        foreach (var (form, body) in BothWireForms(getStatus.GetProperty("savedCardExample")))
        {
            var parsed = JsonSerializer.Deserialize<CheckoutStatus>(body, ReadOptions)!;

            // The saved-card example is the plain example plus a storedPaymentMethod: nothing
            // else may move when a card was stored.
            Assert.Equal(TransactionStatuses.Succeeded, parsed.Status);
            Assert.Equal("order-1042", parsed.OrderReference);
            var method = Assert.IsType<StoredPaymentMethod>(parsed.StoredPaymentMethod);
            Assert.Equal(PaymentMethodId, method.Id);
            Assert.Equal("visa", method.Brand);
            Assert.Equal("4242", method.Last4);
            Assert.Equal(12, method.ExpiryMonth);
            Assert.Equal(2029, method.ExpiryYear);
            Assert.Equal(StoredPaymentMethodStatuses.Active, method.Status);
            Assert.True(method.IsChargeable, form);
        }
    }

    [Fact]
    public async Task TheStatusExampleWithoutASavedCardReadsNullInBothWireForms()
    {
        var getStatus = Endpoint("getStatus");
        Assert.Equal(JsonValueKind.Null, getStatus.GetProperty("example").GetProperty("storedPaymentMethod").ValueKind);

        foreach (var (form, body) in BothWireForms(getStatus.GetProperty("example")))
        {
            using var server = new MockServer(Reply.Enveloped(body));
            using var client = ClientFor(server);

            var parsed = await client.GetStatusAsync("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

            Assert.True(parsed.StoredPaymentMethod is null, form);
        }
    }

    [Fact]
    public void AStoredPaymentMethodWithNullDetailsReadsNullInBothWireForms()
    {
        var bare = JsonDocument.Parse(
            $$"""{"id":"{{PaymentMethodId}}","brand":null,"last4":null,"expiryMonth":null,"expiryYear":null,"status":"active"}""").RootElement.Clone();

        foreach (var (form, body) in BothWireForms(bare))
        {
            var method = JsonSerializer.Deserialize<StoredPaymentMethod>(body, ReadOptions)!;
            Assert.Null(method.Brand);
            Assert.Null(method.Last4);
            Assert.Null(method.ExpiryMonth);
            Assert.Null(method.ExpiryYear);
            Assert.True(method.IsChargeable, form);
        }
    }

    [Fact]
    public void EveryStoredPaymentMethodStatusRoundTripsAndOnlyActiveIsChargeable()
    {
        var example = Endpoint("getStatus").GetProperty("savedCardExample").GetProperty("storedPaymentMethod");

        foreach (var value in Strings(Contract().GetProperty("storedPaymentMethodStatusVocabulary")))
        {
            var payload = JsonNode.Parse(example.GetRawText())!;
            payload["status"] = value;

            var method = JsonSerializer.Deserialize<StoredPaymentMethod>(payload.ToJsonString(), ReadOptions)!;

            Assert.Equal(value, method.Status);
            Assert.Equal(value == StoredPaymentMethodStatuses.Active, method.IsChargeable);
        }
    }

    [Fact]
    public void ChargePaymentMethodMatchesTheContract()
    {
        var charge = Endpoint("chargePaymentMethod");
        Assert.Equal("POST", charge.GetProperty("method").GetString());
        Assert.Equal("/merchant-api/payment-methods/{paymentMethodId}/charges", charge.GetProperty("path").GetString());
        Assert.Equal(201, charge.GetProperty("httpStatus").GetInt32());
        Assert.Equal(402, charge.GetProperty("declinedHttpStatus").GetInt32());
        AssertFields<PaymentMethodCharge>(Strings(charge.GetProperty("fields")));

        // Both examples are envelopes whose data carries exactly the declared fields, nulls
        // included.
        var expected = Strings(charge.GetProperty("fields")).Order().ToList();
        foreach (var name in new[] { "successExample", "declinedExample" })
        {
            Assert.Equal(expected, Keys(charge.GetProperty(name).GetProperty("data")));
        }

        Assert.True(charge.GetProperty("successExample").GetProperty("success").GetBoolean());
        Assert.False(charge.GetProperty("declinedExample").GetProperty("success").GetBoolean());
        Assert.Equal("CHARGE_DECLINED", charge.GetProperty("declinedExample").GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void EveryChargeStatusRoundTripsAndOnlySucceededIsPaid()
    {
        var example = Endpoint("chargePaymentMethod").GetProperty("successExample").GetProperty("data");

        foreach (var value in Strings(Contract().GetProperty("chargeStatusVocabulary")))
        {
            var payload = JsonNode.Parse(example.GetRawText())!;
            payload["status"] = value;

            var charge = JsonSerializer.Deserialize<PaymentMethodCharge>(payload.ToJsonString(), ReadOptions)!;

            Assert.Equal(value, charge.Status);
            Assert.Equal(value == ChargeStatuses.Succeeded, charge.IsPaid);

            // Only pending keeps the caller polling.
            Assert.Equal(value != ChargeStatuses.Pending, charge.IsTerminal);
        }
    }

    [Fact]
    public async Task TheChargeSuccessExampleComesBackAsAPaidCharge()
    {
        var charge = Endpoint("chargePaymentMethod");
        foreach (var (form, body) in BothWireForms(charge.GetProperty("successExample")))
        {
            using var server = new MockServer(Reply.Raw(201, body));
            using var client = ClientFor(server);

            var result = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

            Assert.Equal("ch_1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d", result.ChargeId);
            Assert.Equal(ChargeStatuses.Succeeded, result.Status);
            Assert.Null(result.DeclineClass);
            Assert.Null(result.DeclineCode);
            Assert.Equal("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", result.TransactionId);
            Assert.True(result.IsPaid, form);
            Assert.True(result.IsTerminal, form);

            // Raw is the charge object, not the envelope.
            Assert.Equal("ch_1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d", result.Raw.GetProperty("chargeId").GetString());
            Assert.False(result.Raw.TryGetProperty("success", out _));
        }
    }

    [Fact]
    public async Task TheChargeDeclinedExampleComesBackAsAFailedChargeNotAnException()
    {
        var charge = Endpoint("chargePaymentMethod");
        foreach (var (form, body) in BothWireForms(charge.GetProperty("declinedExample")))
        {
            using var server = new MockServer(Reply.Raw(402, body));
            using var client = ClientFor(server);

            var result = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

            Assert.Equal("ch_1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5e", result.ChargeId);
            Assert.Equal(ChargeStatuses.Failed, result.Status);
            Assert.Equal(DeclineClasses.SoftFunds, result.DeclineClass);
            Assert.Equal("51", result.DeclineCode);
            Assert.Equal("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5e", result.TransactionId);
            Assert.False(result.IsPaid, form);
            Assert.True(result.IsTerminal, form);
            Assert.Contains(result.DeclineClass, DeclineClasses.All);
        }
    }

    [Fact]
    public async Task EveryChargeErrorExampleComesBackAsAChargeException()
    {
        var examples = Endpoint("chargePaymentMethod").GetProperty("errorExamples").EnumerateArray().ToList();

        // Eight examples for seven codes: CHARGE_FAILED is spelled both with and without an
        // attached charge row.
        Assert.Equal(8, examples.Count);
        Assert.Equal(
            Strings(Contract().GetProperty("chargeErrorCodes")).Order().ToList(),
            examples.Select(example => example.GetProperty("code").GetString()!).Distinct().Order().ToList());

        foreach (var example in examples)
        {
            var httpStatus = example.GetProperty("httpStatus").GetInt32();
            var code = example.GetProperty("code").GetString()!;
            var body = example.GetProperty("body");
            Assert.Equal(code, body.GetProperty("error").GetProperty("code").GetString());
            var hasData = body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object;
            var expectsCharge = hasData && data.TryGetProperty("chargeId", out var chargeId) && chargeId.ValueKind == JsonValueKind.String;

            foreach (var (form, wire) in BothWireForms(body))
            {
                var label = $"{code} ({httpStatus}, {form})";
                using var server = new MockServer(Reply.Raw(httpStatus, wire));
                using var client = ClientFor(server);

                var error = await Assert.ThrowsAsync<DominaiteChargeException>(
                    () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

                Assert.False(error.IsRetryable, label);
                Assert.Equal(httpStatus, error.HttpStatus);
                Assert.Equal(code, error.Code);
                Assert.Equal(body.GetProperty("error").GetProperty("message").GetString(), error.Message);
                Assert.NotNull(error.IdempotencyKey);

                // RawResult is the whole envelope.
                Assert.Equal(code, error.RawResult.GetProperty("error").GetProperty("code").GetString());
                Assert.False(error.RawResult.GetProperty("success").GetBoolean());

                if (expectsCharge)
                {
                    var charge = Assert.IsType<PaymentMethodCharge>(error.Charge);
                    Assert.Equal(data.GetProperty("chargeId").GetString(), charge.ChargeId);
                    Assert.Equal(data.GetProperty("status").GetString(), charge.Status);
                    Assert.Null(charge.DeclineClass);
                    Assert.Null(charge.DeclineCode);
                    Assert.Equal(data.GetProperty("transactionId").GetString(), error.TransactionId);
                }
                else
                {
                    Assert.True(error.Charge is null, label);
                    Assert.Null(error.TransactionId);
                }
            }
        }
    }

    [Fact]
    public async Task TheChargeOutcomeUnknownExampleNamesTheTransactionToPoll()
    {
        var example = Endpoint("chargePaymentMethod").GetProperty("errorExamples")[0];
        Assert.Equal(ChargeErrorCodes.ChargeOutcomeUnknown, example.GetProperty("code").GetString());

        using var server = new MockServer(Reply.Raw(502, example.GetProperty("body").GetRawText()));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteChargeException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Equal("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5f", error.TransactionId);
        Assert.Equal(ChargeStatuses.Pending, error.Charge!.Status);
    }

    [Fact]
    public async Task TheChargeNotFoundExampleIsAnApiErrorWithItsCode()
    {
        var example = Endpoint("chargePaymentMethod").GetProperty("notFoundExample");
        Assert.Equal(404, example.GetProperty("httpStatus").GetInt32());

        foreach (var (form, body) in BothWireForms(example.GetProperty("body")))
        {
            using var server = new MockServer(Reply.Raw(404, body));
            using var client = ClientFor(server);

            var error = await Assert.ThrowsAsync<DominaiteApiException>(
                () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

            Assert.Equal(404, error.HttpStatus);
            Assert.Equal("PAYMENT_METHOD_NOT_FOUND", error.Code);
            Assert.False(error.IsRetryable, form);
        }
    }

    [Fact]
    public async Task RevokePaymentMethodMatchesTheContract()
    {
        var revoke = Endpoint("revokePaymentMethod");
        Assert.Equal("DELETE", revoke.GetProperty("method").GetString());
        Assert.Equal("/merchant-api/payment-methods/{paymentMethodId}", revoke.GetProperty("path").GetString());
        Assert.Equal(204, revoke.GetProperty("httpStatus").GetInt32());
        Assert.Empty(Strings(revoke.GetProperty("fields")));

        using var server = new MockServer(Reply.Raw(204, string.Empty));
        using var client = ClientFor(server);

        await client.RevokePaymentMethodAsync(PaymentMethodId);

        var sent = server.LastRequest;
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task EveryRevokeErrorExampleComesBackAsARevokeException()
    {
        var examples = Endpoint("revokePaymentMethod").GetProperty("errorExamples").EnumerateArray().ToList();
        Assert.Equal(2, examples.Count);
        var codes = Strings(Contract().GetProperty("revokeErrorCodes"));

        foreach (var example in examples)
        {
            var httpStatus = example.GetProperty("httpStatus").GetInt32();
            var code = example.GetProperty("code").GetString()!;
            var body = example.GetProperty("body");
            Assert.Contains(code, codes);

            foreach (var (form, wire) in BothWireForms(body))
            {
                var label = $"{code} ({httpStatus}, {form})";
                using var server = new MockServer(Reply.Raw(httpStatus, wire));
                using var client = ClientFor(server);

                var error = await Assert.ThrowsAsync<DominaiteRevokeException>(
                    () => client.RevokePaymentMethodAsync(PaymentMethodId));

                Assert.False(error.IsRetryable, label);
                Assert.Equal(httpStatus, error.HttpStatus);
                Assert.Equal(code, error.Code);
                Assert.Equal(body.GetProperty("error").GetProperty("message").GetString(), error.Message);
                Assert.Equal(code, error.RawResult.GetProperty("error").GetProperty("code").GetString());
            }
        }
    }

    [Fact]
    public async Task TheRevokeNotFoundExampleIsAValidationErrorApiError()
    {
        var example = Endpoint("revokePaymentMethod").GetProperty("notFoundExample");
        Assert.Equal(404, example.GetProperty("httpStatus").GetInt32());
        Assert.Equal("VALIDATION_ERROR", example.GetProperty("code").GetString());
        Assert.Equal("id", example.GetProperty("body").GetProperty("error").GetProperty("validationErrors")[0].GetProperty("field").GetString());

        foreach (var (form, body) in BothWireForms(example.GetProperty("body")))
        {
            using var server = new MockServer(Reply.Raw(404, body));
            using var client = ClientFor(server);

            var error = await Assert.ThrowsAsync<DominaiteApiException>(
                () => client.RevokePaymentMethodAsync(PaymentMethodId));

            Assert.Equal(404, error.HttpStatus);
            Assert.Equal("VALIDATION_ERROR", error.Code);
            Assert.False(error.IsRetryable, form);
        }
    }
}
