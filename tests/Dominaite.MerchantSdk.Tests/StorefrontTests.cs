using System.Net;
using System.Text;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// Storefront refusals on session create: whatever status carries them, they surface as one typed
/// exception with the code intact, and they are never retried.
/// </summary>
/// <remarks>
/// These run against a stub <see cref="HttpMessageHandler"/> handed in through
/// <see cref="DominaiteClientOptions.HttpClient"/>, which is also how a factory-managed client is
/// plugged in.
/// </remarks>
public class StorefrontTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private sealed class StubHandler(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _replies = new(replies);

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            this.Calls++;
            var (status, body) = this._replies.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static DominaiteClient ClientFor(StubHandler handler)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = "https://merchant-api.test/api",
            HttpClient = new HttpClient(handler),
        });

    private static CheckoutSessionRequest Request() => new()
    {
        Amount = 2500,
        Currency = "EUR",
        OrderReference = "order-1042",
        IdempotencyKey = "checkout-order-1042-2500-EUR",
    };

    private static string ErrorEnvelope(string code, int status)
        => $$$"""{"success":false,"error":{"code":"{{{code}}}","message":"refused by storefront","statusCode":{{{status}}}}}""";

    [Fact]
    public async Task ANotWhitelistedStorefrontIsATypedConflictWithItsCode()
    {
        var handler = new StubHandler((HttpStatusCode.Conflict, ErrorEnvelope(ErrorCodes.StorefrontNotWhitelisted, 409)));
        using var client = ClientFor(handler);

        var error = await Assert.ThrowsAsync<DominaiteStorefrontException>(
            () => client.CreateCheckoutSessionAsync(Request()));

        Assert.Equal("STOREFRONT_NOT_WHITELISTED", error.Code);
        Assert.Equal(409, error.HttpStatus);
        Assert.Equal("refused by storefront", error.Message);
        Assert.Equal("checkout-order-1042-2500-EUR", error.IdempotencyKey);
        Assert.False(error.IsRetryable);
        Assert.Null(error.TransactionId);
        Assert.Equal(
            "STOREFRONT_NOT_WHITELISTED",
            error.RawResult.GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(ErrorCodes.StorefrontInactive, 409)]
    [InlineData(ErrorCodes.StorefrontMismatch, 400)]
    public async Task EveryStorefrontCodeIsTheSameType(string code, int status)
    {
        var handler = new StubHandler(((HttpStatusCode)status, ErrorEnvelope(code, status)));
        using var client = ClientFor(handler);

        var error = await Assert.ThrowsAsync<DominaiteStorefrontException>(
            () => client.CreateCheckoutSessionAsync(Request()));

        Assert.Equal(code, error.Code);
        Assert.Equal(status, error.HttpStatus);
    }

    /// <summary>An idempotent replay against a different storefront answers 200 with success=false.</summary>
    [Fact]
    public async Task AStorefrontMismatchOnAReplayIsTheSameTypeAndNamesTheTransaction()
    {
        const string body = """
            {"success":true,"data":{"success":false,"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","errorCode":"STOREFRONT_MISMATCH","errorMessage":"This API key is bound to a different storefront than the request names."}}
            """;
        var handler = new StubHandler((HttpStatusCode.OK, body));
        using var client = ClientFor(handler);

        var error = await Assert.ThrowsAsync<DominaiteStorefrontException>(
            () => client.CreateCheckoutSessionAsync(Request()));

        Assert.Equal(ErrorCodes.StorefrontMismatch, error.Code);
        Assert.Equal(200, error.HttpStatus);
        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", error.TransactionId);
    }

    [Fact]
    public async Task TheRetryHelperNeverRetriesAStorefrontRefusal()
    {
        var handler = new StubHandler(
            (HttpStatusCode.Conflict, ErrorEnvelope(ErrorCodes.StorefrontNotWhitelisted, 409)),
            (HttpStatusCode.Conflict, ErrorEnvelope(ErrorCodes.StorefrontNotWhitelisted, 409)));
        using var client = ClientFor(handler);

        await Assert.ThrowsAsync<DominaiteStorefrontException>(
            () => client.CreateCheckoutSessionWithRetryAsync(
                Request(),
                new RetryOptions { Attempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) }));

        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Any other 409 keeps its code on the generic API error rather than being taken for a storefront problem.</summary>
    [Fact]
    public async Task AnUnrelatedConflictStaysAGenericApiError()
    {
        var handler = new StubHandler((HttpStatusCode.Conflict, ErrorEnvelope("SOMETHING_ELSE", 409)));
        using var client = ClientFor(handler);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.CreateCheckoutSessionAsync(Request()));

        Assert.Equal("SOMETHING_ELSE", error.Code);
        Assert.Equal(409, error.HttpStatus);
    }

    [Fact]
    public void TheConstantsSpellTheGatewayCodes()
    {
        Assert.Equal(
            ["STOREFRONT_NOT_WHITELISTED", "STOREFRONT_INACTIVE", "STOREFRONT_MISMATCH"],
            ErrorCodes.Storefront);
        Assert.Equal("ALREADY_PROCESSED", ErrorCodes.AlreadyProcessed);
        Assert.Equal("PRIOR_ATTEMPT_FAILED", ErrorCodes.PriorAttemptFailed);
        Assert.Equal("DUPLICATE_REQUEST", ErrorCodes.DuplicateRequest);
        Assert.Equal("PAYMENT_PROCESSING_UNAVAILABLE", ErrorCodes.PaymentProcessingUnavailable);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", ErrorCodes.IdempotencyKeyReused);
    }
}
