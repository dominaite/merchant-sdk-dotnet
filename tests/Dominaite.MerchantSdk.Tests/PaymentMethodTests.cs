using System.Text.Json;
using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// Stored payment methods against the loopback server: <c>saveCard</c> on a session,
/// <c>paymentMethod</c> on its status, then off-session charges and revocation on
/// <c>/merchant-api/payment-methods/{id}</c>.
/// </summary>
public class PaymentMethodTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string TransactionId = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string PaymentMethodId = "pm_0123456789abcdef0123456789abcdef";

    // The charge vector from SigningVectorTests: with the vector timestamp, the header the client
    // sends is the vector signature.
    private const string ChargeKey = "00000000-0000-4000-8000-000000000003";
    private const string ChargeBody = """{"amount":2500,"currency":"EUR","orderReference":"order-1043"}""";
    private const string ChargeSignature = "9ce9f54efa2533a46aa4493b97b56aeb657f41d6a18f1c008c7fd412029aebf9";
    private const string RevokeSignature = "9330100343c4b820504890a09829a193d5815ca39e92160fdfc13d320a802a02";

    private const string SessionPayload = """
        {"success":true,"checkout":{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","cashierKey":"ck_live_2f3a4d5e6f708192","cashierToken":"ctok_5e4f3a2b1c0d9e8f","amount":2500,"currency":"EUR","expiresAt":"2026-08-21T11:15:30.000Z"}}
        """;

    private const string ChargePayload = """
        {"chargeId":"chg_1","status":"succeeded","declineClass":null,"declineCode":null,"transactionId":"33333333-3333-4333-8333-333333333333"}
        """;

    private static string ChargePath => $"{DominaiteClient.PaymentMethodsPath}/{PaymentMethodId}/charges";

    private static string RevokePath => $"{DominaiteClient.PaymentMethodsPath}/{PaymentMethodId}";

    private static Reply ChargeCreated() => Reply.Raw(201, ChargePayload);

    private static Reply Revoked() => Reply.Raw(204, string.Empty);

    private static Reply StatusWithMethod(string status)
        => Reply.Enveloped(
            $$$"""{"transactionId":"{{{TransactionId}}}","orderId":"dom_9a8b7c6d5e4f","status":"succeeded","amount":2500,"currency":"EUR","paymentMethod":{"id":"{{{PaymentMethodId}}}","brand":"visa","last4":"4242","expiryMonth":12,"expiryYear":2029,"status":"{{{status}}}"}}""");

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

    private static ChargeRequest Charge() => new()
    {
        Amount = 2500,
        Currency = "EUR",
        OrderReference = "order-1043",
        IdempotencyKey = ChargeKey,
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
    public async Task SaveCardIsSentInTheSessionBodyAndNowhereElse()
    {
        using var server = new MockServer(Reply.Enveloped(SessionPayload));
        using var client = ClientFor(server);

        var request = new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1042",
            SaveCard = true,
            IdempotencyKey = "00000000-0000-4000-8000-000000000001",
        };

        await client.CreateCheckoutSessionAsync(request);

        var sent = server.LastRequest;
        Assert.Equal(
            """{"amount":2500,"currency":"EUR","orderReference":"order-1042","saveCard":true}""",
            sent.Body);
        AssertSignatureMatches(sent, DominaiteClient.SessionsPath, "00000000-0000-4000-8000-000000000001");
    }

    /// <summary>
    /// A session that never sets SaveCard keeps the exact vector body: the flag is omitted, not
    /// sent as false, so the signed bytes of every existing integration do not move.
    /// </summary>
    [Fact]
    public async Task ASessionWithoutSaveCardKeepsTheVectorBody()
    {
        using var server = new MockServer(Reply.Enveloped(SessionPayload));
        using var client = ClientFor(server);

        await client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1042",
        });

        Assert.Equal(
            """{"amount":2500,"currency":"EUR","orderReference":"order-1042"}""",
            server.LastRequest.Body);
    }

    [Fact]
    public async Task GetStatusCarriesTheStoredPaymentMethod()
    {
        using var server = new MockServer(StatusWithMethod("active"));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        var method = Assert.IsType<PaymentMethod>(status.PaymentMethod);
        Assert.Equal(PaymentMethodId, method.Id);
        Assert.Equal("visa", method.Brand);
        Assert.Equal("4242", method.Last4);
        Assert.Equal(12, method.ExpiryMonth);
        Assert.Equal(2029, method.ExpiryYear);
        Assert.Equal(PaymentMethodStatuses.Active, method.Status);
        Assert.True(method.IsChargeable);
    }

    [Theory]
    [InlineData("""{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","status":"succeeded","amount":2500,"currency":"EUR","paymentMethod":null}""")]
    [InlineData("""{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","status":"succeeded","amount":2500,"currency":"EUR"}""")]
    public async Task GetStatusWithoutASavedCardLeavesPaymentMethodNull(string payload)
    {
        using var server = new MockServer(Reply.Enveloped(payload));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        Assert.Null(status.PaymentMethod);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("frozen")]
    public async Task ARevokedExpiredOrUnknownMethodIsNotChargeable(string value)
    {
        using var server = new MockServer(StatusWithMethod(value));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        Assert.False(status.PaymentMethod!.IsChargeable);
    }

    [Fact]
    public async Task ChargeSignsTheChargeVectorByteForByte()
    {
        using var server = new MockServer(ChargeCreated());
        using var client = ClientFor(server);

        var charge = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

        Assert.Equal("chg_1", charge.ChargeId);
        Assert.Equal(ChargeStatuses.Succeeded, charge.Status);
        Assert.True(charge.IsPaid);
        Assert.True(charge.IsTerminal);
        Assert.Null(charge.DeclineClass);
        Assert.Null(charge.DeclineCode);
        Assert.Equal("33333333-3333-4333-8333-333333333333", charge.TransactionId);
        Assert.Equal("chg_1", charge.Raw.GetProperty("chargeId").GetString());

        var sent = server.LastRequest;
        Assert.Equal("POST", sent.Method);

        // The URL carries the base URL's /api prefix; the signature covers the canonical path.
        Assert.Equal("/api" + ChargePath, sent.Path);
        Assert.Equal(ChargeBody, sent.Body);
        Assert.Equal(ChargeKey, sent.Header("Idempotency-Key"));
        Assert.Equal(KeyId, sent.Header("X-Api-Key-Id"));
        AssertSignatureMatches(sent, ChargePath, ChargeKey);

        // With the vector timestamp, the same recipe over the same bytes IS the published vector.
        var pinned = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = sent.Method,
            Path = ChargePath,
            IdempotencyKey = ChargeKey,
            Body = sent.Body,
        });
        Assert.Equal(ChargeSignature, pinned);
    }

    [Fact]
    public async Task ChargeGeneratesAKeyWritesItBackAndSendsTheDescriptionLast()
    {
        using var server = new MockServer(ChargeCreated());
        using var client = ClientFor(server);

        var request = new ChargeRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1043",
            Description = "Monthly plan",
        };
        Assert.Null(request.IdempotencyKey);

        await client.ChargePaymentMethodAsync(PaymentMethodId, request);

        var sent = server.LastRequest;
        Assert.NotNull(request.IdempotencyKey);
        Assert.True(Guid.TryParseExact(request.IdempotencyKey, "D", out _));
        Assert.Equal(request.IdempotencyKey, sent.Header("Idempotency-Key"));
        Assert.Equal(
            """{"amount":2500,"currency":"EUR","orderReference":"order-1043","description":"Monthly plan"}""",
            sent.Body);
        AssertSignatureMatches(sent, ChargePath, request.IdempotencyKey!);
    }

    /// <summary>Through the envelope too: the unwrap path must not eat the decline.</summary>
    [Fact]
    public async Task ADeclinedChargeIsAResultWithADeclineClassNotAnException()
    {
        using var server = new MockServer(Reply.Raw(
            201,
            """{"success":true,"data":{"chargeId":"chg_2","status":"failed","declineClass":"soft_funds","declineCode":"51","transactionId":"33333333-3333-4333-8333-333333333334"}}"""));
        using var client = ClientFor(server);

        var charge = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

        Assert.Equal(ChargeStatuses.Failed, charge.Status);
        Assert.False(charge.IsPaid);
        Assert.True(charge.IsTerminal);
        Assert.Equal(DeclineClasses.SoftFunds, charge.DeclineClass);
        Assert.Equal("51", charge.DeclineCode);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("reviewing")]
    public async Task APendingOrUnknownChargeStatusIsNotTerminal(string value)
    {
        using var server = new MockServer(Reply.Raw(
            201,
            $$"""{"chargeId":"chg_3","status":"{{value}}","transactionId":"33333333-3333-4333-8333-333333333335"}"""));
        using var client = ClientFor(server);

        var charge = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

        Assert.False(charge.IsPaid);
        Assert.False(charge.IsTerminal);
    }

    [Fact]
    public async Task AChargeTheGatewayRefusesToAttemptIsARefusalWithItsCodeAndKey()
    {
        using var server = new MockServer(Reply.Enveloped(
            """{"success":false,"errorCode":"ALREADY_PROCESSED","errorMessage":"Already charged","transactionId":"33333333-3333-4333-8333-333333333333"}"""));
        using var client = ClientFor(server);

        var request = Charge();
        var error = await Assert.ThrowsAsync<DominaiteRefusalException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, request));

        Assert.Equal("ALREADY_PROCESSED", error.Code);
        Assert.Equal("Already charged", error.Message);
        Assert.Equal("33333333-3333-4333-8333-333333333333", error.TransactionId);
        Assert.Equal(ChargeKey, error.IdempotencyKey);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task APayloadWithoutAChargeIdIsARefusalWhateverSuccessSays()
    {
        using var server = new MockServer(Reply.Enveloped("""{"success":true}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRefusalException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Equal("UNKNOWN", error.Code);
    }

    [Fact]
    public async Task AChargeAgainstAMethodThatIsNotYoursIsA404ApiError()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(404, "NOT_FOUND", "No such payment method"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Equal(404, error.HttpStatus);
        Assert.Equal("NOT_FOUND", error.Code);
        Assert.Equal(ChargeKey, error.IdempotencyKey);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task AFiveHundredOnAChargeIsRetryableWithTheSameKey()
    {
        using var server = new MockServer(Reply.Raw(503, "<h1>down</h1>"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteTransportException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.True(error.IsRetryable);
        Assert.Equal(ChargeKey, error.IdempotencyKey);
    }

    [Fact]
    public async Task ChargeValidatesMoneyParamsLikeASession()
    {
        using var server = new MockServer(ChargeCreated());
        using var client = ClientFor(server);

        var bad = new[]
        {
            new ChargeRequest { Amount = 0, Currency = "EUR", OrderReference = "order-1" },
            new ChargeRequest { Amount = -500, Currency = "EUR", OrderReference = "order-1" },
            new ChargeRequest { Amount = 2500, Currency = " ", OrderReference = "order-1" },
            new ChargeRequest { Amount = 2500, Currency = "EUR", OrderReference = "" },
            new ChargeRequest { Amount = 2500, Currency = "EUR", OrderReference = new string('x', 101) },
            new ChargeRequest { Amount = 2500, Currency = "EUR", OrderReference = "order-1", IdempotencyKey = " " },
            new ChargeRequest { Amount = 2500, Currency = "EUR", OrderReference = "order-1", IdempotencyKey = new string('k', 101) },
        };

        foreach (var request in bad)
        {
            await Assert.ThrowsAsync<DominaiteValidationException>(
                () => client.ChargePaymentMethodAsync(PaymentMethodId, request));
        }

        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("pm_1/charges")]
    [InlineData("pm_1?x=1")]
    [InlineData("pm_1#f")]
    [InlineData("pm 1")]
    [InlineData("pm_1%2F")]
    [InlineData("../sessions")]
    public async Task APaymentMethodIdThatWouldNotStayOnePathSegmentIsRefusedBeforeSigning(string id)
    {
        using var server = new MockServer(ChargeCreated(), Revoked());
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.ChargePaymentMethodAsync(id, Charge()));
        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.RevokePaymentMethodAsync(id));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AnOverLongPaymentMethodIdIsRefusedBeforeSigning()
    {
        using var server = new MockServer(Revoked());
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.RevokePaymentMethodAsync(new string('p', 101)));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task APaddedPaymentMethodIdIsTrimmed()
    {
        using var server = new MockServer(Revoked());
        using var client = ClientFor(server);

        await client.RevokePaymentMethodAsync($"  {PaymentMethodId} ");

        Assert.Equal("/api" + RevokePath, server.LastRequest.Path);
    }

    [Fact]
    public async Task RevokeSignsTheRevokeVectorAndResolvesOn204()
    {
        using var server = new MockServer(Revoked());
        using var client = ClientFor(server);

        await client.RevokePaymentMethodAsync(PaymentMethodId);

        var sent = server.LastRequest;
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal("/api" + RevokePath, sent.Path);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, RevokePath, string.Empty);

        var pinned = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "DELETE",
            Path = RevokePath,
            IdempotencyKey = string.Empty,
            Body = string.Empty,
        });
        Assert.Equal(RevokeSignature, pinned);
    }

    [Fact]
    public async Task RevokeMapsA404AndA5xx()
    {
        using (var server = new MockServer(Reply.ErrorEnvelope(404, "NOT_FOUND", "No such payment method")))
        using (var client = ClientFor(server))
        {
            var error = await Assert.ThrowsAsync<DominaiteApiException>(
                () => client.RevokePaymentMethodAsync(PaymentMethodId));
            Assert.Equal(404, error.HttpStatus);
            Assert.False(error.IsRetryable);
        }

        using (var server = new MockServer(Reply.Raw(503, """{"success":false}""")))
        using (var client = ClientFor(server))
        {
            var error = await Assert.ThrowsAsync<DominaiteTransportException>(
                () => client.RevokePaymentMethodAsync(PaymentMethodId));
            Assert.True(error.IsRetryable);
        }
    }

    [Fact]
    public void TheVocabulariesAreExposedAsConstants()
    {
        Assert.Equal(new[] { "active", "revoked", "expired" }, PaymentMethodStatuses.All);
        Assert.Equal(new[] { "succeeded", "failed", "pending" }, ChargeStatuses.All);
        Assert.Equal(new[] { "hard", "soft_funds", "soft_sca_required", "soft_other" }, DeclineClasses.All);
    }

    [Fact]
    public void AChargeDeserializesWithNullsOmittedTheWayTheGatewaySerializes()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        var charge = JsonSerializer.Deserialize<PaymentMethodCharge>(
            """{"chargeId":"chg_1","status":"succeeded","transactionId":"t"}""",
            options)!;

        Assert.Null(charge.DeclineClass);
        Assert.Null(charge.DeclineCode);
        Assert.True(charge.IsPaid);
    }
}
