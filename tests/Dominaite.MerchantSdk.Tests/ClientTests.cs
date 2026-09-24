using System.Text.Json;
using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The client against a real loopback HTTP server: what it puts on the wire, and how it maps
/// answers onto the error taxonomy.
/// </summary>
public class ClientTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string SuccessPayload = """
        {"success":true,"checkout":{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","cashierKey":"ck_live_2f3a4d5e6f708192","cashierToken":"ctok_5e4f3a2b1c0d9e8f","amount":8440,"currency":"EUR","expiresAt":"2026-08-21T11:15:30.000Z"}}
        """;

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

    [Fact]
    public async Task CreateSendsASignedRequestAndReturnsTheSession()
    {
        using var server = new MockServer(Reply.Enveloped(SuccessPayload));
        using var client = ClientFor(server);

        var request = Request();
        var session = await client.CreateCheckoutSessionAsync(request);

        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", session.TransactionId);
        Assert.Equal("ctok_5e4f3a2b1c0d9e8f", session.CashierToken);
        Assert.Equal(8440, session.Amount);

        var sent = server.LastRequest;
        Assert.Equal("POST", sent.Method);

        // The URL carries the base URL's /api prefix...
        Assert.Equal("/api" + DominaiteClient.SessionsPath, sent.Path);
        Assert.Equal(KeyId, sent.Header("X-Api-Key-Id"));
        Assert.Equal(request.IdempotencyKey, sent.Header("Idempotency-Key"));
        Assert.StartsWith("dominaite-dotnet/", sent.Header("User-Agent"), StringComparison.Ordinal);

        // ...and the signature covers the canonical path WITHOUT it, over exactly the bytes that
        // were sent.
        var expected = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = sent.Header("X-Timestamp")!,
            Method = "POST",
            Path = DominaiteClient.SessionsPath,
            IdempotencyKey = sent.Header("Idempotency-Key")!,
            Body = sent.Body,
        });

        Assert.Equal(expected, sent.Header("X-Signature"));
    }

    /// <summary>
    /// The body is serialized once and the hash covers exactly the transmitted bytes. Non-ASCII
    /// payer names are the case that catches a second serialization pass, and this is the same
    /// body the cross-SDK non-ASCII vector pins.
    /// </summary>
    [Fact]
    public async Task TheBodyOnTheWireIsTheBodyThatWasHashed()
    {
        using var server = new MockServer(Reply.Enveloped(SuccessPayload));
        using var client = ClientFor(server);

        var request = new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1042",
            Customer = new Customer { FirstName = "Анна", LastName = "Müller" },
            IdempotencyKey = "00000000-0000-4000-8000-000000000001",
        };

        await client.CreateCheckoutSessionAsync(request);

        var sent = server.LastRequest;
        Assert.Equal(
            """{"amount":2500,"currency":"EUR","orderReference":"order-1042","customer":{"firstName":"Анна","lastName":"Müller"}}""",
            sent.Body);
        Assert.Equal(
            "baf00d6116d9f2eec6c3a422af0bc2c342717f669aa2350ef6ed556f57ac34b5",
            RequestSigner.Sha256Hex(sent.Body));
    }

    [Fact]
    public async Task ExtraFieldsAreMergedIntoTheBody()
    {
        using var server = new MockServer(Reply.Enveloped(SuccessPayload));
        using var client = ClientFor(server);

        var request = Request();
        request.Extra["splitPayment"] = true;

        await client.CreateCheckoutSessionAsync(request);

        Assert.Equal(
            """{"amount":8440,"currency":"EUR","orderReference":"order-1042","splitPayment":true}""",
            server.LastRequest.Body);
    }

    /// <summary>
    /// The key is required: the SDK never makes one up, because a key made up per call cannot
    /// recognise the same order coming back. A missing or blank key fails before the network,
    /// on the plain call and on the retry helper alike.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingIdempotencyKeyIsRejectedBeforeAnythingIsSent(string? key)
    {
        using var server = new MockServer(Reply.Enveloped(SuccessPayload));
        using var client = ClientFor(server);

        var request = Request();
        request.IdempotencyKey = key;

        var error = await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateCheckoutSessionAsync(request));
        Assert.Contains("IdempotencyKey", error.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateCheckoutSessionWithRetryAsync(request));

        Assert.Equal(key, request.IdempotencyKey);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task TheSuppliedKeyIsSentAndSignedUnchanged()
    {
        using var server = new MockServer(Reply.Enveloped(SuccessPayload));
        using var client = ClientFor(server);

        var request = Request();
        request.IdempotencyKey = IdempotencyKeys.ForOrder("checkout", "order-1042", 8440, "eur");

        await client.CreateCheckoutSessionAsync(request);

        Assert.Equal("checkout-order-1042-8440-EUR", request.IdempotencyKey);
        Assert.Equal("checkout-order-1042-8440-EUR", server.LastRequest.Header("Idempotency-Key"));
    }

    [Fact]
    public async Task GetStatusSignsAnEmptyKeyAndSendsNoIdempotencyHeader()
    {
        const string payload = """
            {"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","orderReference":"order-1042","status":"succeeded","amount":8440,"currency":"EUR","createdAt":"2026-08-21T09:15:30.000Z"}
            """;

        using var server = new MockServer(Reply.Enveloped(payload));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

        Assert.True(status.IsPaid);
        Assert.True(status.IsTerminal);

        // Absent nullable keys are the real gateway's shape (WhenWritingNull), and they must
        // deserialize as null rather than blow up.
        Assert.Null(status.RefundedAmount);
        Assert.Null(status.UpdatedAt);
        Assert.Null(status.ExpiresAt);

        var sent = server.LastRequest;
        Assert.Equal("GET", sent.Method);
        Assert.Null(sent.Header("Idempotency-Key"));
        Assert.Empty(sent.Body);

        var expected = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = sent.Header("X-Timestamp")!,
            Method = "GET",
            Path = $"{DominaiteClient.SessionsPath}/0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
            IdempotencyKey = string.Empty,
            Body = string.Empty,
        });

        Assert.Equal(expected, sent.Header("X-Signature"));
    }

    [Fact]
    public async Task PingSignsTheLiteralPingPath()
    {
        const string payload = """
            {"pong":true,"merchantId":"6f2b6a1e-0c4d-4e8a-9b1c-2d3e4f5a6b70","serverTime":"2026-08-21T09:15:30.000Z","serverUnixTime":1755767730,"clockSkewSeconds":2}
            """;

        using var server = new MockServer(Reply.Enveloped(payload));
        using var client = ClientFor(server);

        var ping = await client.PingAsync();

        Assert.True(ping.Pong);
        Assert.Equal(2, ping.ClockSkewSeconds);

        var sent = server.LastRequest;
        Assert.Equal("/api" + DominaiteClient.PingPath, sent.Path);

        var expected = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = sent.Header("X-Timestamp")!,
            Method = "GET",
            Path = DominaiteClient.PingPath,
            IdempotencyKey = string.Empty,
            Body = string.Empty,
        });

        Assert.Equal(expected, sent.Header("X-Signature"));
    }

    /// <summary>
    /// A replay refusal is HTTP 200 with success=false. It must arrive as a typed refusal carrying
    /// the transaction the key collided with, plus the raw payload.
    /// </summary>
    [Fact]
    public async Task AReplayRefusalCarriesItsTransactionAndRawResult()
    {
        const string payload = """
            {"success":false,"checkout":null,"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","errorCode":"ALREADY_PROCESSED","errorMessage":"This payment already completed."}
            """;

        using var server = new MockServer(Reply.Enveloped(payload));
        using var client = ClientFor(server);

        var request = Request();
        var error = await Assert.ThrowsAsync<DominaiteRefusalException>(
            () => client.CreateCheckoutSessionAsync(request));

        Assert.Equal("ALREADY_PROCESSED", error.Code);
        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", error.TransactionId);
        Assert.False(error.IsRetryable);
        Assert.Equal(request.IdempotencyKey, error.IdempotencyKey);
        Assert.Equal(
            "This payment already completed.",
            error.RawResult.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task AnAuthFailureIsTypedAndKeepsItsCode()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(401, "TIMESTAMP_OUT_OF_RANGE", "Check your clock"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteAuthException>(() => client.PingAsync());

        Assert.Equal("TIMESTAMP_OUT_OF_RANGE", error.Code);
        Assert.Equal(401, error.HttpStatus);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task AnUnknownTransactionIsA404ApiError()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(404, "NOT_FOUND", "No such transaction"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.GetStatusAsync("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"));

        Assert.Equal(404, error.HttpStatus);
        Assert.False(error.IsRetryable);
    }

    /// <summary>
    /// A 5xx with an HTML body from a proxy is still a retryable transport failure. Parsing has to
    /// happen after the status branch, or this surfaces as a bogus parse error.
    /// </summary>
    [Theory]
    [InlineData(500, "<html><body>502 Bad Gateway</body></html>")]
    [InlineData(502, "")]
    [InlineData(503, "{\"success\":false,\"error\":{\"code\":\"MERCHANT_API_UNAVAILABLE\"}}")]
    public async Task AFiveHundredIsRetryableWhateverTheBodyIs(int status, string body)
    {
        using var server = new MockServer(Reply.Raw(status, body));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteTransportException>(
            () => client.CreateCheckoutSessionAsync(Request()));

        Assert.True(error.IsRetryable);
        Assert.Equal(status, error.HttpStatus);
    }

    [Fact]
    public async Task ANonJsonTwoHundredIsAnApiError()
    {
        using var server = new MockServer(Reply.Raw(200, "not json at all"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.CreateCheckoutSessionAsync(Request()));

        Assert.False(error.IsRetryable);
    }

    /// <summary>
    /// The API never redirects. A 3xx means something in front of it is answering, so the handler
    /// must not follow it and the SDK must not retry it.
    /// </summary>
    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task ARedirectIsNeverFollowedAndNeverRetried(int status)
    {
        using var server = new MockServer(
            Reply.Redirect(status, "https://evil.example.com/merchant-api/checkout/sessions"),
            Reply.Enveloped(SuccessPayload));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.CreateCheckoutSessionWithRetryAsync(Request()));

        Assert.Equal(status, error.HttpStatus);
        Assert.False(error.IsRetryable);
        Assert.Contains("never redirects", error.Message, StringComparison.Ordinal);

        // One request: the redirect was not followed, and the retry helper did not try again.
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RetryReusesTheSameIdempotencyKeyAcrossAttempts()
    {
        using var server = new MockServer(
            Reply.Raw(503, ""),
            Reply.Raw(503, ""),
            Reply.Enveloped(SuccessPayload));
        using var client = ClientFor(server);

        var request = Request();
        var session = await client.CreateCheckoutSessionWithRetryAsync(
            request,
            new RetryOptions { Attempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) });

        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", session.TransactionId);
        Assert.Equal(3, server.Requests.Count);

        var keys = server.Requests.Select(sent => sent.Header("Idempotency-Key")).Distinct().ToList();
        Assert.Single(keys);
        Assert.Equal(request.IdempotencyKey, keys[0]);
    }

    [Fact]
    public async Task RetryGivesUpWithTheLastTransportError()
    {
        using var server = new MockServer(Reply.Raw(503, ""), Reply.Raw(503, ""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteTransportException>(
            () => client.CreateCheckoutSessionWithRetryAsync(
                Request(),
                new RetryOptions { Attempts = 2, BaseDelay = TimeSpan.FromMilliseconds(1) }));

        Assert.Equal(503, error.HttpStatus);
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task RetryNeverRetriesARefusal()
    {
        const string refusal = """
            {"success":false,"errorCode":"IDEMPOTENCY_KEY_REUSED","errorMessage":"Same key, different body."}
            """;

        using var server = new MockServer(Reply.Enveloped(refusal), Reply.Enveloped(SuccessPayload));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRefusalException>(
            () => client.CreateCheckoutSessionWithRetryAsync(
                Request(),
                new RetryOptions { Attempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) }));

        Assert.Equal("IDEMPOTENCY_KEY_REUSED", error.Code);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RetryNeverRetriesAnAuthFailure()
    {
        using var server = new MockServer(
            Reply.ErrorEnvelope(401, "INVALID_SIGNATURE", "Nope"),
            Reply.Enveloped(SuccessPayload));
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteAuthException>(
            () => client.CreateCheckoutSessionWithRetryAsync(
                Request(),
                new RetryOptions { Attempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) }));

        Assert.Single(server.Requests);
    }

    [Fact]
    public void TheSecretNeverAppearsInToString()
    {
        using var client = new DominaiteClient(KeyId, Secret);

        var text = client.ToString();

        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("dms_", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }

    /// <summary>The cashier token is a per-session bearer value. It must not ride along in a log line.</summary>
    [Fact]
    public void TheCashierTokenNeverAppearsInToString()
    {
        var session = JsonSerializer.Deserialize<CheckoutSession>(
            """{"transactionId":"t","cashierKey":"ck","cashierToken":"ctok_secret_value"}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.DoesNotContain("ctok_secret_value", session.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nope_key", Secret, "keyId")]
    [InlineData(KeyId, "nope_secret", "secret")]
    public void SwappedOrMalformedCredentialsAreRejectedBeforeAnythingIsSent(string keyId, string secret, string expected)
    {
        var error = Assert.Throws<DominaiteValidationException>(() => new DominaiteClient(keyId, secret));
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BadArgumentsAreRejectedBeforeAnythingIsSent()
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateCheckoutSessionAsync(new CheckoutSessionRequest { Amount = 0, Currency = "EUR", OrderReference = "x", IdempotencyKey = "k" }));
        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateCheckoutSessionAsync(new CheckoutSessionRequest { Amount = 100, Currency = " ", OrderReference = "x", IdempotencyKey = "k" }));
        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateCheckoutSessionAsync(new CheckoutSessionRequest { Amount = 100, Currency = "EUR", OrderReference = "", IdempotencyKey = "k" }));
        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.GetStatusAsync("not-a-uuid"));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public void AnEmptyBaseUrlKeepsProduction()
    {
        using var unset = new DominaiteClient(KeyId, Secret, new DominaiteClientOptions { BaseUrl = "   " });
        Assert.Equal(DominaiteClient.DefaultBaseUrl, unset.BaseUrl);

        using var trailing = new DominaiteClient(KeyId, Secret, new DominaiteClientOptions { BaseUrl = "https://example.test/api/" });
        Assert.Equal("https://example.test/api", trailing.BaseUrl);
    }

    [Fact]
    public void TheDefaultTimeoutIsFortyFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(45), DominaiteClientOptions.DefaultTimeout);
        Assert.Equal(TimeSpan.FromSeconds(45), new DominaiteClientOptions().Timeout);
    }

    [Fact]
    public void TheReportedVersionMatchesTheAssembly()
    {
        var assembly = typeof(DominaiteClient).Assembly.GetName().Version!;
        Assert.Equal(DominaiteClient.Version, $"{assembly.Major}.{assembly.Minor}.{assembly.Build}");
    }
}
