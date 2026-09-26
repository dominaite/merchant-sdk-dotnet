using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// Refunds against the loopback server: <c>POST /merchant-api/payments/{id}/refunds</c> with a
/// signed idempotency key, and the <c>GET .../refunds/{refundId}</c> status read.
/// </summary>
public class RefundTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string TransactionId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
    private const string RefundId = "re_7c1e9a2b4d6f48a0b3c5d7e9f1a2b3c4";
    private const string RefundKey = "refund-rma-77-2500-EUR";

    private const string PendingRefund = """
        {"success":true,"data":{"refundId":"re_7c1e9a2b4d6f48a0b3c5d7e9f1a2b3c4","transactionId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","status":"pending","amount":2500,"currency":"EUR"}}
        """;

    private const string SucceededRefund = """
        {"success":true,"data":{"refundId":"re_7c1e9a2b4d6f48a0b3c5d7e9f1a2b3c4","transactionId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","status":"succeeded","amount":2500,"currency":"EUR","completedAt":"2026-09-26T10:05:40.1200000Z"}}
        """;

    private const string FailedRefund = """
        {"success":true,"data":{"refundId":"re_7c1e9a2b4d6f48a0b3c5d7e9f1a2b3c4","transactionId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","status":"failed","currency":"EUR","failureCode":"REFUND_FAILED","failureMessage":"The refund could not be completed.","completedAt":"2026-09-26T10:05:41.0000000Z"}}
        """;

    private static string CreatePath => $"{DominaiteClient.PaymentsPath}/{TransactionId}/refunds";

    private static string ReadPath => $"{CreatePath}/{RefundId}";

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

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
    public async Task APartialRefundSendsTheAmountAndSignsItsKey()
    {
        using var server = new MockServer(Reply.Raw(202, PendingRefund));
        using var client = ClientFor(server);

        var refund = await client.CreateRefundAsync(TransactionId, new RefundRequest
        {
            Amount = 2500,
            Reason = "Returned item",
            IdempotencyKey = RefundKey,
        });

        var sent = server.LastRequest;
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/api" + CreatePath, sent.Path);
        Assert.Equal("""{"amount":2500,"reason":"Returned item"}""", sent.Body);
        Assert.Equal(RefundKey, sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, CreatePath, RefundKey);

        Assert.Equal(RefundId, refund.RefundId);
        Assert.Equal(TransactionId, refund.TransactionId);
        Assert.Equal(RefundStatuses.Pending, refund.Status);
        Assert.Equal(2500, refund.Amount);
        Assert.Equal("EUR", refund.Currency);
        Assert.Null(refund.FailureCode);
        Assert.Null(refund.CompletedAt);
        Assert.False(refund.IsTerminal);
        Assert.Equal(RefundId, refund.Raw.GetProperty("refundId").GetString());
    }

    /// <summary>
    /// A full refund leaves the amount out entirely. <c>"amount":null</c> is not the same request:
    /// the key must be absent.
    /// </summary>
    [Fact]
    public async Task AFullRefundSendsNoAmountKey()
    {
        using var server = new MockServer(Reply.Raw(202, PendingRefund));
        using var client = ClientFor(server);

        await client.CreateRefundAsync(TransactionId, new RefundRequest { IdempotencyKey = RefundKey });

        var sent = server.LastRequest;
        Assert.Equal("{}", sent.Body);
        AssertSignatureMatches(sent, CreatePath, RefundKey);
    }

    [Fact]
    public async Task TheTransactionIdIsSignedInLowercase()
    {
        using var server = new MockServer(Reply.Raw(202, PendingRefund));
        using var client = ClientFor(server);

        await client.CreateRefundAsync(TransactionId.ToUpperInvariant(), new RefundRequest { IdempotencyKey = RefundKey });

        AssertSignatureMatches(server.LastRequest, CreatePath, RefundKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMissingIdempotencyKeyIsRefusedBeforeSending(string? key)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateRefundAsync(TransactionId, new RefundRequest { Amount = 100, IdempotencyKey = key }));

        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public async Task ANonPositiveAmountIsRefusedBeforeSending(long amount)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateRefundAsync(TransactionId, new RefundRequest { Amount = amount, IdempotencyKey = RefundKey }));

        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("../payments")]
    public async Task ATransactionIdThatIsNotAUuidIsRefusedBeforeSending(string transactionId)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateRefundAsync(transactionId, new RefundRequest { IdempotencyKey = RefundKey }));
        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.GetRefundAsync(transactionId, RefundId));

        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("re_1/../../x")]
    [InlineData("re_1?x=1")]
    [InlineData("")]
    public async Task ARefundIdThatWouldNotStayOnePathSegmentIsRefusedBeforeSigning(string refundId)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.GetRefundAsync(TransactionId, refundId));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task GetRefundSignsAnEmptyKeyAndAnEmptyBody()
    {
        using var server = new MockServer(Reply.Raw(200, SucceededRefund));
        using var client = ClientFor(server);

        var refund = await client.GetRefundAsync(TransactionId, RefundId);

        var sent = server.LastRequest;
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api" + ReadPath, sent.Path);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, ReadPath, string.Empty);

        Assert.Equal(RefundStatuses.Succeeded, refund.Status);
        Assert.Equal(2500, refund.Amount);
        Assert.Equal(DateTimeOffset.Parse("2026-09-26T10:05:40.12Z"), refund.CompletedAt);
        Assert.True(refund.IsSucceeded);
        Assert.True(refund.IsTerminal);
    }

    [Fact]
    public async Task AFailedRefundIsAResultWithNoAmountAndAFailureCode()
    {
        using var server = new MockServer(Reply.Raw(200, FailedRefund));
        using var client = ClientFor(server);

        var refund = await client.GetRefundAsync(TransactionId, RefundId);

        Assert.Equal(RefundStatuses.Failed, refund.Status);
        Assert.Null(refund.Amount);
        Assert.Equal(RefundFailureCodes.RefundFailed, refund.FailureCode);
        Assert.Equal("The refund could not be completed.", refund.FailureMessage);
        Assert.False(refund.IsSucceeded);
        Assert.True(refund.IsTerminal);
    }

    [Theory]
    [InlineData(404, "PAYMENT_NOT_FOUND", false)]
    [InlineData(422, "PAYMENT_NOT_REFUNDABLE", false)]
    [InlineData(422, "REFUND_AMOUNT_EXCEEDED", false)]
    [InlineData(422, "IDEMPOTENCY_KEY_REUSED", false)]
    [InlineData(409, "DUPLICATE_REQUEST", true)]
    [InlineData(400, "IDEMPOTENCY_KEY_REQUIRED", false)]
    [InlineData(422, "A_NEW_CODE", false)]
    public async Task CreateRefundMapsEveryCodeToARefundException(int status, string code, bool retryable)
    {
        using var server = new MockServer(Reply.ErrorEnvelope(status, code, "refused"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRefundException>(
            () => client.CreateRefundAsync(TransactionId, new RefundRequest { Amount = 2500, IdempotencyKey = RefundKey }));

        Assert.Equal(status, error.HttpStatus);
        Assert.Equal(code, error.Code);
        Assert.Equal("refused", error.Message);
        Assert.Equal(retryable, error.IsRetryable);

        // The key to retry with, or to know is still unused.
        Assert.Equal(RefundKey, error.IdempotencyKey);
        Assert.Equal(code, error.RawResult.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(404, "REFUND_NOT_FOUND", true)]
    [InlineData(404, "PAYMENT_NOT_FOUND", false)]
    public async Task GetRefundMapsItsCodesToARefundException(int status, string code, bool retryable)
    {
        using var server = new MockServer(Reply.ErrorEnvelope(status, code, "not found"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRefundException>(() => client.GetRefundAsync(TransactionId, RefundId));

        Assert.Equal(status, error.HttpStatus);
        Assert.Equal(code, error.Code);
        Assert.Equal(retryable, error.IsRetryable);
    }

    /// <summary>A 500 queued nothing: retry with the same key, which the exception carries.</summary>
    [Theory]
    [InlineData("""{"success":false,"error":{"code":"INTERNAL_ERROR","message":"boom","statusCode":500}}""")]
    [InlineData("<html>Bad Gateway</html>")]
    public async Task AFiveHundredIsARetryableTransportErrorWithTheSameKey(string body)
    {
        using var server = new MockServer(Reply.Raw(500, body));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteTransportException>(
            () => client.CreateRefundAsync(TransactionId, new RefundRequest { IdempotencyKey = RefundKey }));

        Assert.True(error.IsRetryable);
        Assert.Equal(500, error.HttpStatus);
        Assert.Equal(RefundKey, error.IdempotencyKey);
    }

    [Fact]
    public async Task CredentialsFailuresStayAuthErrors()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(401, "INVALID_SIGNATURE", "bad signature"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteAuthException>(() => client.GetRefundAsync(TransactionId, RefundId));

        Assert.Equal("INVALID_SIGNATURE", error.Code);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task A202WithoutARefundBodyIsAnApiError()
    {
        using var server = new MockServer(Reply.Raw(202, """{"success":true}"""));
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.CreateRefundAsync(TransactionId, new RefundRequest { IdempotencyKey = RefundKey }));
    }

    [Theory]
    [InlineData("pending", false)]
    [InlineData("processing", false)]
    [InlineData("succeeded", true)]
    [InlineData("failed", true)]
    [InlineData("reversed_later", false)]
    public void OnlySucceededAndFailedAreTerminal(string status, bool terminal)
    {
        var refund = new Refund { Status = status };

        Assert.Equal(terminal, refund.IsTerminal);
        Assert.Equal(status == RefundStatuses.Succeeded, refund.IsSucceeded);
    }
}
