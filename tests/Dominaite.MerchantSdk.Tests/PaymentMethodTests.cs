using System.Text.Json;
using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// Stored payment methods against the loopback server: <c>saveCard</c> on a session,
/// <c>storedPaymentMethod</c> on its status, then off-session charges and revocation on
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

    private const string ChargeId = "ch_33333333333343338333333333333333";
    private const string ChargeTransactionId = "33333333-3333-4333-8333-333333333333";

    private const string ChargePayload = """
        {"chargeId":"ch_33333333333343338333333333333333","status":"succeeded","declineClass":null,"declineCode":null,"transactionId":"33333333-3333-4333-8333-333333333333"}
        """;

    private static string ChargePath => $"{DominaiteClient.PaymentMethodsPath}/{PaymentMethodId}/charges";

    private static string RevokePath => $"{DominaiteClient.PaymentMethodsPath}/{PaymentMethodId}";

    /// <summary>
    /// The gateway envelope around a charge answer: <c>data</c> when there is a charge row,
    /// <c>error</c> when there is a code.
    /// </summary>
    private static Reply ChargeEnvelope(int status, string? code = null, string? message = null, string? data = null)
    {
        var parts = new List<string> { $"\"success\":{(code is null ? "true" : "false")}" };
        if (data is not null)
        {
            parts.Add($"\"data\":{data}");
        }

        if (code is not null)
        {
            parts.Add($"\"error\":{{\"message\":\"{message ?? "refused"}\",\"code\":\"{code}\",\"statusCode\":{status},\"timestamp\":\"2026-09-15T18:02:11.4183920Z\"}}");
        }

        parts.Add("\"metadata\":{\"requestId\":\"c2a1e6d4-3b5f-4c7e-9a8d-1f2e3d4c5b6a\"}");
        return Reply.Raw(status, "{" + string.Join(",", parts) + "}");
    }

    private static Reply ChargeCreated() => ChargeEnvelope(201, data: ChargePayload);

    private static Reply Revoked() => Reply.Raw(204, string.Empty);

    private static Reply StatusWithMethod(string status)
        => Reply.Enveloped(
            $$$"""{"transactionId":"{{{TransactionId}}}","orderId":"dom_9a8b7c6d5e4f","status":"succeeded","amount":2500,"currency":"EUR","paymentMethod":"card","storedPaymentMethod":{"id":"{{{PaymentMethodId}}}","brand":"visa","last4":"4242","expiryMonth":12,"expiryYear":2029,"status":"{{{status}}}"}}""");

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
            IdempotencyKey = "00000000-0000-4000-8000-000000000001",
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

        var method = Assert.IsType<StoredPaymentMethod>(status.StoredPaymentMethod);
        Assert.Equal(PaymentMethodId, method.Id);
        Assert.Equal("visa", method.Brand);
        Assert.Equal("4242", method.Last4);
        Assert.Equal(12, method.ExpiryMonth);
        Assert.Equal(2029, method.ExpiryYear);
        Assert.Equal(StoredPaymentMethodStatuses.Active, method.Status);
        Assert.True(method.IsChargeable);

        // paymentMethod is the gateway's string category of how the payer paid; it is not the
        // stored card and stays on Raw untyped.
        Assert.Equal("card", status.Raw.GetProperty("paymentMethod").GetString());
    }

    [Theory]
    [InlineData("""{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","status":"succeeded","amount":2500,"currency":"EUR","paymentMethod":"card","storedPaymentMethod":null}""")]
    [InlineData("""{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","status":"succeeded","amount":2500,"currency":"EUR","paymentMethod":"card"}""")]
    public async Task GetStatusWithoutASavedCardLeavesStoredPaymentMethodNull(string payload)
    {
        using var server = new MockServer(Reply.Enveloped(payload));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        Assert.Null(status.StoredPaymentMethod);
    }

    /// <summary>The gateway omits null fields on the wire; the SDK reads absent as null.</summary>
    [Theory]
    [InlineData("""{"id":"pm_0123456789abcdef0123456789abcdef","brand":null,"last4":null,"expiryMonth":null,"expiryYear":null,"status":"active"}""")]
    [InlineData("""{"id":"pm_0123456789abcdef0123456789abcdef","status":"active"}""")]
    public async Task AStoredPaymentMethodTheProviderDidNotDescribeReadsAsNullFields(string method)
    {
        using var server = new MockServer(Reply.Enveloped(
            $$"""{"transactionId":"{{TransactionId}}","orderId":"dom_9a8b7c6d5e4f","status":"succeeded","amount":2500,"currency":"EUR","storedPaymentMethod":{{method}} }"""));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        var stored = Assert.IsType<StoredPaymentMethod>(status.StoredPaymentMethod);
        Assert.Equal(PaymentMethodId, stored.Id);
        Assert.Null(stored.Brand);
        Assert.Null(stored.Last4);
        Assert.Null(stored.ExpiryMonth);
        Assert.Null(stored.ExpiryYear);
        Assert.True(stored.IsChargeable);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("retired")]
    [InlineData("frozen")]
    public async Task ARevokedExpiredRetiredOrUnknownMethodIsNotChargeable(string value)
    {
        using var server = new MockServer(StatusWithMethod(value));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        Assert.False(status.StoredPaymentMethod!.IsChargeable);
    }

    [Fact]
    public async Task ChargeSignsTheChargeVectorByteForByte()
    {
        using var server = new MockServer(ChargeCreated());
        using var client = ClientFor(server);

        var charge = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

        Assert.Equal(ChargeId, charge.ChargeId);
        Assert.Equal(ChargeStatuses.Succeeded, charge.Status);
        Assert.True(charge.IsPaid);
        Assert.True(charge.IsTerminal);
        Assert.Null(charge.DeclineClass);
        Assert.Null(charge.DeclineCode);
        Assert.Equal(ChargeTransactionId, charge.TransactionId);

        // Raw is the unwrapped charge object, not the envelope.
        Assert.Equal(ChargeId, charge.Raw.GetProperty("chargeId").GetString());
        Assert.False(charge.Raw.TryGetProperty("success", out _));

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
    public async Task ChargeSendsItsKeyAndTheDescriptionLast()
    {
        using var server = new MockServer(ChargeCreated());
        using var client = ClientFor(server);

        var request = new ChargeRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1043",
            Description = "Monthly plan",
            IdempotencyKey = IdempotencyKeys.ForOrder("charge", "order-1043", 2500, "EUR"),
        };

        await client.ChargePaymentMethodAsync(PaymentMethodId, request);

        var sent = server.LastRequest;
        Assert.Equal("charge-order-1043-2500-EUR", sent.Header("Idempotency-Key"));
        Assert.Equal(
            """{"amount":2500,"currency":"EUR","orderReference":"order-1043","description":"Monthly plan"}""",
            sent.Body);
        AssertSignatureMatches(sent, ChargePath, request.IdempotencyKey!);
    }

    /// <summary>
    /// The envelope says success=false and names CHARGE_DECLINED, but the charge row is right
    /// there: a decline is a result, not an exception.
    /// </summary>
    [Fact]
    public async Task A402DeclineIsAResultWithADeclineClassNotAnException()
    {
        using var server = new MockServer(ChargeEnvelope(
            402,
            "CHARGE_DECLINED",
            "The payment provider declined the charge.",
            """{"chargeId":"ch_33333333333343338333333333333334","status":"failed","declineClass":"soft_funds","declineCode":"51","transactionId":"33333333-3333-4333-8333-333333333334"}"""));
        using var client = ClientFor(server);

        var charge = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

        Assert.Equal("ch_33333333333343338333333333333334", charge.ChargeId);
        Assert.Equal(ChargeStatuses.Failed, charge.Status);
        Assert.False(charge.IsPaid);
        Assert.True(charge.IsTerminal);
        Assert.Equal(DeclineClasses.SoftFunds, charge.DeclineClass);
        Assert.Equal("51", charge.DeclineCode);
        Assert.Equal("33333333-3333-4333-8333-333333333334", charge.TransactionId);
    }

    /// <summary>A durable replay of the same key answers 200 with the original charge.</summary>
    [Fact]
    public async Task A200ReplayIsReturnedAsTheCharge()
    {
        using var server = new MockServer(ChargeEnvelope(200, data: ChargePayload));
        using var client = ClientFor(server);

        var charge = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

        Assert.Equal(ChargeId, charge.ChargeId);
        Assert.True(charge.IsPaid);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("reviewing")]
    public async Task APendingOrUnknownChargeStatusIsNotTerminal(string value)
    {
        using var server = new MockServer(ChargeEnvelope(
            201,
            data: $$"""{"chargeId":"{{ChargeId}}","status":"{{value}}","transactionId":"{{ChargeTransactionId}}"}"""));
        using var client = ClientFor(server);

        var charge = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

        Assert.False(charge.IsPaid);
        Assert.False(charge.IsTerminal);

        // Absent on the wire reads as null, like an explicit null.
        Assert.Null(charge.DeclineClass);
        Assert.Null(charge.DeclineCode);
    }

    [Fact]
    public async Task ACancelledChargeIsTerminalAndNotPaid()
    {
        using var server = new MockServer(ChargeEnvelope(
            201,
            data: $$"""{"chargeId":"{{ChargeId}}","status":"cancelled","transactionId":"{{ChargeTransactionId}}"}"""));
        using var client = ClientFor(server);

        var charge = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

        Assert.Equal(ChargeStatuses.Cancelled, charge.Status);
        Assert.False(charge.IsPaid);
        Assert.True(charge.IsTerminal);
    }

    [Fact]
    public async Task ChargeOutcomeUnknownCarriesTheTransactionToPoll()
    {
        using var server = new MockServer(ChargeEnvelope(
            502,
            "CHARGE_OUTCOME_UNKNOWN",
            "The payment provider gave no verdict.",
            $$"""{"chargeId":"{{ChargeId}}","status":"pending","declineClass":null,"declineCode":null,"transactionId":"{{ChargeTransactionId}}"}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteChargeException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        // Never blind-retried: the money may have moved.
        Assert.False(error.IsRetryable);
        Assert.Equal(502, error.HttpStatus);
        Assert.Equal(ChargeErrorCodes.ChargeOutcomeUnknown, error.Code);
        Assert.Equal("The payment provider gave no verdict.", error.Message);
        Assert.Equal(ChargeKey, error.IdempotencyKey);

        var charge = Assert.IsType<PaymentMethodCharge>(error.Charge);
        Assert.Equal(ChargeId, charge.ChargeId);
        Assert.Equal(ChargeStatuses.Pending, charge.Status);
        Assert.Null(charge.DeclineClass);
        Assert.Equal(ChargeTransactionId, error.TransactionId);

        // RawResult is the whole envelope.
        Assert.False(error.RawResult.GetProperty("success").GetBoolean());
        Assert.Equal("CHARGE_OUTCOME_UNKNOWN", error.RawResult.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(ChargeId, error.RawResult.GetProperty("data").GetProperty("chargeId").GetString());
    }

    [Theory]
    [InlineData(409, ChargeErrorCodes.PaymentMethodNotActive)]
    [InlineData(409, ChargeErrorCodes.DuplicateRequest)]
    [InlineData(422, ChargeErrorCodes.IdempotencyKeyReused)]
    [InlineData(502, ChargeErrorCodes.ChargeFailed)]
    [InlineData(503, ChargeErrorCodes.PaymentMethodChargesDisabled)]
    [InlineData(503, ChargeErrorCodes.PaymentProcessingUnavailable)]
    [InlineData(409, "A_NEW_CODE")]
    public async Task ChargeErrorsWithoutDataHaveNoCharge(int status, string code)
    {
        using var server = new MockServer(ChargeEnvelope(status, code, "refused"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteChargeException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Equal(code == ChargeErrorCodes.PaymentProcessingUnavailable, error.IsRetryable);
        Assert.Equal(status, error.HttpStatus);
        Assert.Equal(code, error.Code);
        Assert.Equal("refused", error.Message);
        Assert.Equal(ChargeKey, error.IdempotencyKey);
        Assert.Null(error.Charge);
        Assert.Null(error.TransactionId);
        Assert.Equal(code, error.RawResult.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AChargeAgainstAMethodThatIsNotYoursIsA404ApiError()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(404, "PAYMENT_METHOD_NOT_FOUND", "No stored payment method with this id."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Equal(404, error.HttpStatus);
        Assert.Equal("PAYMENT_METHOD_NOT_FOUND", error.Code);
        Assert.Equal(ChargeKey, error.IdempotencyKey);
        Assert.False(error.IsRetryable);
    }

    /// <summary>A 400 with a code is input validation, not a charge outcome.</summary>
    [Fact]
    public async Task AChargeKeepsTheGenericApiErrorForA400()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(400, "IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key is required"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Equal(400, error.HttpStatus);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", error.Code);
    }

    /// <summary>A 5xx without a gateway code is an outage, whatever the body looks like.</summary>
    [Theory]
    [InlineData(503, "<h1>down</h1>")]
    [InlineData(503, """{"success":false}""")]
    [InlineData(502, "")]
    public async Task ACodelessFiveHundredOnAChargeIsRetryableWithTheSameKey(int status, string body)
    {
        using var server = new MockServer(Reply.Raw(status, body));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteTransportException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.True(error.IsRetryable);
        Assert.Equal(status, error.HttpStatus);
        Assert.Equal(ChargeKey, error.IdempotencyKey);
    }

    [Theory]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"success":true,"data":{}}""")]
    public async Task A201WithoutAChargeBodyIsAnApiError(string body)
    {
        using var server = new MockServer(Reply.Raw(201, body));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Equal(201, error.HttpStatus);
        Assert.Null(error.Code);
        Assert.Equal(ChargeKey, error.IdempotencyKey);
    }

    [Fact]
    public async Task ChargeValidatesMoneyParamsLikeASession()
    {
        using var server = new MockServer(ChargeCreated());
        using var client = ClientFor(server);

        var bad = new[]
        {
            new ChargeRequest { Amount = 0, Currency = "EUR", OrderReference = "order-1", IdempotencyKey = ChargeKey },
            new ChargeRequest { Amount = -500, Currency = "EUR", OrderReference = "order-1", IdempotencyKey = ChargeKey },
            new ChargeRequest { Amount = 2500, Currency = " ", OrderReference = "order-1", IdempotencyKey = ChargeKey },
            new ChargeRequest { Amount = 2500, Currency = "EUR", OrderReference = "", IdempotencyKey = ChargeKey },
            new ChargeRequest { Amount = 2500, Currency = "EUR", OrderReference = new string('x', 101), IdempotencyKey = ChargeKey },
            new ChargeRequest { Amount = 2500, Currency = "EUR", OrderReference = "order-1" },
            new ChargeRequest { Amount = 2500, Currency = "EUR", OrderReference = "order-1", IdempotencyKey = "" },
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

    [Theory]
    [InlineData(502, RevokeErrorCodes.UpstreamContractError, "The payment provider refused to delete the stored credential.")]
    [InlineData(503, RevokeErrorCodes.MerchantApiUnavailable, "The payment provider is unavailable. Nothing changed; retry later.")]
    [InlineData(502, "A_NEW_CODE", "refused")]
    public async Task RevokeThrowsRevokeExceptionsForCodedFailures(int status, string code, string message)
    {
        using var server = new MockServer(Reply.ErrorEnvelope(status, code, message));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRevokeException>(
            () => client.RevokePaymentMethodAsync(PaymentMethodId));

        Assert.False(error.IsRetryable);
        Assert.Equal(status, error.HttpStatus);
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
        Assert.False(error.RawResult.GetProperty("success").GetBoolean());
        Assert.Equal(code, error.RawResult.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// The gateway's 404 for an id that is not yours is a VALIDATION_ERROR envelope; it stays the
    /// generic API error with that code.
    /// </summary>
    [Fact]
    public async Task RevokeMapsA404ToAnApiErrorWithItsCode()
    {
        using var server = new MockServer(Reply.Raw(
            404,
            $$$"""{"success":false,"error":{"message":"Validation failed","code":"VALIDATION_ERROR","statusCode":404,"validationErrors":[{"field":"id","message":"'{{{PaymentMethodId}}}' not found","code":"VALIDATION_FAILED"}]}}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.RevokePaymentMethodAsync(PaymentMethodId));

        Assert.Equal(404, error.HttpStatus);
        Assert.Equal("VALIDATION_ERROR", error.Code);
        Assert.False(error.IsRetryable);
    }

    [Theory]
    [InlineData("""{"success":false}""")]
    [InlineData("<h1>down</h1>")]
    public async Task RevokeMapsACodeless5xxToATransportError(string body)
    {
        using var server = new MockServer(Reply.Raw(503, body));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteTransportException>(
            () => client.RevokePaymentMethodAsync(PaymentMethodId));

        Assert.True(error.IsRetryable);
        Assert.Equal(503, error.HttpStatus);
    }

    [Fact]
    public void TheVocabulariesAreExposedAsConstants()
    {
        Assert.Equal(new[] { "active", "revoked", "expired", "retired" }, StoredPaymentMethodStatuses.All);
        Assert.Equal(new[] { "hard_decline", "chargeback", "source_sale_reversed" }, StoredPaymentMethodRetiredReasons.All);
        Assert.Equal(new[] { "succeeded", "failed", "pending", "cancelled" }, ChargeStatuses.All);
        Assert.Equal(new[] { "hard", "soft_funds", "soft_sca_required", "soft_other" }, DeclineClasses.All);
        Assert.Equal(
            new[]
            {
                "PAYMENT_METHOD_NOT_ACTIVE",
                "DUPLICATE_REQUEST",
                "IDEMPOTENCY_KEY_REUSED",
                "CHARGE_OUTCOME_UNKNOWN",
                "CHARGE_FAILED",
                "PAYMENT_METHOD_CHARGES_DISABLED",
                "PAYMENT_PROCESSING_UNAVAILABLE",
            },
            ChargeErrorCodes.All);
        Assert.Equal(new[] { "UPSTREAM_CONTRACT_ERROR", "MERCHANT_API_UNAVAILABLE" }, RevokeErrorCodes.All);
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
            """{"chargeId":"ch_33333333333343338333333333333333","status":"succeeded","transactionId":"t"}""",
            options)!;

        Assert.Null(charge.DeclineClass);
        Assert.Null(charge.DeclineCode);
        Assert.True(charge.IsPaid);
    }
}
