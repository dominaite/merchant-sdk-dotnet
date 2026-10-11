using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// HTTP 401, 403 and 429 on every route. They are three different problems, so they are three
/// types, and only the 401 gets the hint about the key id, the secret and the clock.
/// </summary>
public class PermissionAndRateLimitTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string TransactionId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
    private const string PaymentMethodId = "pm_0123456789abcdef0123456789abcdef";
    private const string RefundId = "re_7c1e9a2b4d6f48a0b3c5d7e9f1a2b3c4";
    private const string CredentialsHint = "check your key id, secret, and server clock";
    private const string ScopeMessage = "This API key does not have the 'payments' scope this route requires";

    private const string SessionPayload = """
        {"success":true,"checkout":{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","cashierKey":"ck_live_2f3a4d5e6f708192","cashierToken":"ctok_5e4f3a2b1c0d9e8f","amount":8440,"currency":"EUR"}}
        """;

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

    private static CheckoutSessionRequest Session() => new()
    {
        Amount = 8440,
        Currency = "EUR",
        OrderReference = "order-1042",
        IdempotencyKey = "checkout-order-1042-8440-EUR",
    };

    private static ChargeRequest Charge() => new()
    {
        Amount = 2500,
        Currency = "EUR",
        OrderReference = "order-1043",
        IdempotencyKey = "charge-order-1043-2500-EUR",
    };

    private static Reply WithRetryAfter(Reply reply, string retryAfter)
    {
        reply.Headers["Retry-After"] = retryAfter;
        return reply;
    }

    /// <summary>One call per route family, so every route is shown to share the same mapping.</summary>
    private static Task Call(DominaiteClient client, string route) => route switch
    {
        "ping" => client.PingAsync(),
        "session" => client.CreateCheckoutSessionAsync(Session()),
        "status" => client.GetStatusAsync(TransactionId),
        "charge" => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()),
        "revoke" => client.RevokePaymentMethodAsync(PaymentMethodId),
        "refund" => client.CreateRefundAsync(TransactionId, new RefundRequest { IdempotencyKey = "refund-rma-77" }),
        "refundRead" => client.GetRefundAsync(TransactionId, RefundId),
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    [Fact]
    public async Task AFourOhOneWithoutAMessageGetsTheCredentialsHint()
    {
        using var server = new MockServer(Reply.Raw(401, """{"success":false,"error":{"code":"INVALID_SIGNATURE","statusCode":401}}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteAuthException>(() => client.PingAsync());

        Assert.Equal("INVALID_SIGNATURE", error.Code);
        Assert.Equal(401, error.HttpStatus);
        Assert.Contains(CredentialsHint, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ping")]
    [InlineData("session")]
    [InlineData("status")]
    [InlineData("charge")]
    [InlineData("revoke")]
    [InlineData("refund")]
    [InlineData("refundRead")]
    public async Task InsufficientScopeIsAPermissionErrorWithItsOwnMessage(string route)
    {
        using var server = new MockServer(Reply.ErrorEnvelope(403, PermissionErrorCodes.InsufficientScope, ScopeMessage));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaitePermissionException>(() => Call(client, route));

        Assert.Equal(PermissionErrorCodes.InsufficientScope, error.Code);
        Assert.Equal(403, error.HttpStatus);
        Assert.False(error.IsRetryable);
        Assert.Equal(ScopeMessage + ". Use an API key that has this scope.", error.Message);
        Assert.DoesNotContain(CredentialsHint, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFourOhThreeWithoutAMessageNeverGetsTheCredentialsHint()
    {
        using var server = new MockServer(Reply.Raw(403, """{"success":false}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaitePermissionException>(() => client.PingAsync());

        Assert.Equal("FORBIDDEN", error.Code);
        Assert.Equal(403, error.HttpStatus);
        Assert.DoesNotContain(CredentialsHint, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyOnlyNotEnabledSaysWeSwitchItOnAndIsNotRetried()
    {
        using var server = new MockServer(
            Reply.ErrorEnvelope(403, PermissionErrorCodes.VerifyOnlyNotEnabled, "Verify-only sessions are not enabled for this account."),
            Reply.Enveloped(SessionPayload));
        using var client = ClientFor(server);

        var request = Session();
        request.Amount = 0;
        request.VerifyOnly = true;
        var error = await Assert.ThrowsAsync<DominaitePermissionException>(
            () => client.CreateCheckoutSessionWithRetryAsync(request));

        Assert.Equal(PermissionErrorCodes.VerifyOnlyNotEnabled, error.Code);
        Assert.Contains("We switch it on for your account.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(CredentialsHint, error.Message, StringComparison.Ordinal);
        Assert.Equal(request.IdempotencyKey, error.IdempotencyKey);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task ARateLimitCarriesRetryAfterAndTheRetryHelperDoesNotRetryIt()
    {
        using var server = new MockServer(
            WithRetryAfter(Reply.ErrorEnvelope(429, RateLimitErrorCodes.RateLimitExceeded, "Too many requests. Please try again later."), "7"),
            Reply.Enveloped(SessionPayload));
        using var client = ClientFor(server);

        var request = Session();
        var error = await Assert.ThrowsAsync<DominaiteRateLimitException>(
            () => client.CreateCheckoutSessionWithRetryAsync(request));

        Assert.Equal(RateLimitErrorCodes.RateLimitExceeded, error.Code);
        Assert.Equal(429, error.HttpStatus);
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
        Assert.False(error.IsRetryable);
        Assert.Equal(request.IdempotencyKey, error.IdempotencyKey);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("ping")]
    [InlineData("status")]
    [InlineData("revoke")]
    [InlineData("refund")]
    [InlineData("refundRead")]
    public async Task EveryRouteThrowsTheSameRateLimit(string route)
    {
        using var server = new MockServer(
            WithRetryAfter(Reply.ErrorEnvelope(429, RateLimitErrorCodes.RateLimitExceeded, "Too many requests. Please try again later."), "2"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRateLimitException>(() => Call(client, route));

        Assert.Equal(RateLimitErrorCodes.RateLimitExceeded, error.Code);
        Assert.Equal(TimeSpan.FromSeconds(2), error.RetryAfter);
    }

    /// <summary>The lockout after failed sign-ins answers 429 with no Retry-After header.</summary>
    [Fact]
    public async Task ARateLimitWithoutTheHeaderHasNoRetryAfter()
    {
        using var server = new MockServer(
            Reply.ErrorEnvelope(429, RateLimitErrorCodes.RateLimitExceeded, "Too many failed authentication attempts. Try again later."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRateLimitException>(() => client.PingAsync());

        Assert.Null(error.RetryAfter);
        Assert.Equal("Too many failed authentication attempts. Try again later.", error.Message);
    }

    [Theory]
    [InlineData(RateLimitErrorCodes.ChargeVelocityExceeded)]
    [InlineData(RateLimitErrorCodes.ChargeDeclineRateExceeded)]
    public async Task AChargeLimitIsARateLimitWithTheChargeCodeAndKey(string code)
    {
        using var server = new MockServer(
            WithRetryAfter(Reply.ErrorEnvelope(429, code, "Nothing was charged; retry with the same Idempotency-Key after Retry-After seconds."), "120"));
        using var client = ClientFor(server);

        var request = Charge();
        var error = await Assert.ThrowsAsync<DominaiteRateLimitException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, request));

        Assert.Equal(code, error.Code);
        Assert.Equal(TimeSpan.FromSeconds(120), error.RetryAfter);
        Assert.Equal(request.IdempotencyKey, error.IdempotencyKey);
    }

    /// <summary>A 429 from an edge in front of the API has no envelope, and is still a rate limit.</summary>
    [Fact]
    public async Task ANonJsonRateLimitIsStillARateLimit()
    {
        using var server = new MockServer(WithRetryAfter(Reply.Raw(429, "<html><body>Too Many Requests</body></html>"), "3"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRateLimitException>(() => client.GetStatusAsync(TransactionId));

        Assert.Null(error.Code);
        Assert.Equal(TimeSpan.FromSeconds(3), error.RetryAfter);
    }

    [Fact]
    public async Task ARetryAfterDateIsReadAsTheWaitUntilThatDate()
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(30).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
        using var server = new MockServer(
            WithRetryAfter(Reply.ErrorEnvelope(429, RateLimitErrorCodes.RateLimitExceeded, "Too many requests."), until));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRateLimitException>(() => client.PingAsync());

        Assert.NotNull(error.RetryAfter);
        Assert.InRange(error.RetryAfter!.Value, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(31));
    }
}
