using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// Every result and every error carries the id the API gave the request: the
/// <c>Dominaite-Request-Id</c> header when there is one, else the body's <c>metadata.requestId</c>.
/// </summary>
public class RequestIdTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PaymentMethodId = "pm_0123456789abcdef0123456789abcdef";
    private const string TransactionId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";

    private const string HeaderId = "req-header-6b1f";
    private const string BodyId = "req-body-2c9a";

    private const string RefundData =
        """{"refundId":"re_7c1e9a2b4d6f48a0b3c5d7e9f1a2b3c4","transactionId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","status":"pending","amount":2500,"currency":"EUR"}""";

    /// <summary>Answers every request with one status, body and optional request id header.</summary>
    private sealed class FixedHandler(int status, string body, string? headerId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (headerId is not null)
            {
                response.Headers.TryAddWithoutValidation("Dominaite-Request-Id", headerId);
            }

            return Task.FromResult(response);
        }
    }

    private static DominaiteClient ClientFor(int status, string body, string? headerId)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = "https://merchant-api.test/api",
            HttpClient = new HttpClient(new FixedHandler(status, body, headerId)),
            MaxRetries = 0,
        });

    private static string Success(string data, string? bodyId)
        => bodyId is null
            ? $$$"""{"success":true,"data":{{{data}}}}"""
            : $$$"""{"success":true,"data":{{{data}}},"metadata":{"requestId":"{{{bodyId}}}","apiVersion":"1.0"}}""";

    private static string Failure(int status, string code, string? bodyId)
        => bodyId is null
            ? $$$"""{"success":false,"error":{"code":"{{{code}}}","message":"refused","statusCode":{{{status}}}}}"""
            : $$$"""{"success":false,"error":{"code":"{{{code}}}","message":"refused","statusCode":{{{status}}}},"metadata":{"requestId":"{{{bodyId}}}"}}""";

    private static RefundRequest Refund() => new() { Amount = 2500, IdempotencyKey = "refund-order-1043" };

    [Fact]
    public async Task TheHeaderWinsOverTheBody()
    {
        using var client = ClientFor(201, Success(RefundData, BodyId), HeaderId);

        var refund = await client.CreateRefundAsync(TransactionId, Refund());

        Assert.Equal(HeaderId, refund.RequestId);
    }

    [Fact]
    public async Task WithoutTheHeaderTheBodyIdIsUsed()
    {
        using var client = ClientFor(201, Success(RefundData, BodyId), null);

        var refund = await client.CreateRefundAsync(TransactionId, Refund());

        Assert.Equal(BodyId, refund.RequestId);
    }

    [Fact]
    public async Task WithNeitherTheIdIsNull()
    {
        using var client = ClientFor(201, Success(RefundData, null), null);

        var refund = await client.CreateRefundAsync(TransactionId, Refund());

        Assert.Null(refund.RequestId);
    }

    [Theory]
    [InlineData(HeaderId, BodyId, HeaderId)]
    [InlineData(null, BodyId, BodyId)]
    public async Task AnErrorCarriesTheId(string? header, string body, string expected)
    {
        using var client = ClientFor(422, Failure(422, RefundErrorCodes.IdempotencyKeyReused, body), header);

        var error = await Assert.ThrowsAsync<DominaiteRefundException>(() => client.CreateRefundAsync(TransactionId, Refund()));

        Assert.Equal(expected, error.RequestId);
    }

    [Theory]
    [InlineData(401, typeof(DominaiteAuthException))]
    [InlineData(403, typeof(DominaitePermissionException))]
    [InlineData(429, typeof(DominaiteRateLimitException))]
    [InlineData(500, typeof(DominaiteTransportException))]
    [InlineData(404, typeof(DominaiteApiException))]
    public async Task EveryGenericErrorCarriesTheBodyId(int status, Type type)
    {
        using var client = ClientFor(status, Failure(status, "SOMETHING", BodyId), null);

        var error = await Assert.ThrowsAnyAsync<DominaiteException>(() => client.GetStatusAsync(TransactionId));

        Assert.IsType(type, error);
        Assert.Equal(BodyId, error.RequestId);
    }

    [Fact]
    public async Task AnErrorWithABodyThatIsNotJsonCarriesTheHeaderId()
    {
        using var client = ClientFor(502, "<h1>bad gateway</h1>", HeaderId);

        var error = await Assert.ThrowsAsync<DominaiteTransportException>(() => client.PingAsync());

        Assert.Equal(HeaderId, error.RequestId);
    }

    [Fact]
    public async Task AChargeDeclineAndAChargeErrorCarryTheId()
    {
        const string charge =
            """{"chargeId":"ch_1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d","status":"failed","declineClass":"soft_funds","declineCode":"51","transactionId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","sequence":1}""";
        var request = new ChargeRequest { Amount = 2500, Currency = "EUR", OrderReference = "order-1043", IdempotencyKey = "charge-order-1043" };

        using var declined = ClientFor(
            402,
            $$$"""{"success":false,"data":{{{charge}}},"error":{"code":"CHARGE_DECLINED","message":"declined","statusCode":402},"metadata":{"requestId":"{{{BodyId}}}"}}""",
            null);
        Assert.Equal(BodyId, (await declined.ChargePaymentMethodAsync(PaymentMethodId, request)).RequestId);

        using var unknown = ClientFor(502, Failure(502, ChargeErrorCodes.ChargeOutcomeUnknown, null), HeaderId);
        var error = await Assert.ThrowsAsync<DominaiteChargeException>(() => unknown.ChargePaymentMethodAsync(PaymentMethodId, request));
        Assert.Equal(HeaderId, error.RequestId);
    }

    [Fact]
    public async Task AReplayRefusalOnSessionCreateCarriesTheId()
    {
        using var client = ClientFor(
            200,
            Success($$$"""{"success":false,"errorCode":"ALREADY_PROCESSED","errorMessage":"done","transactionId":"{{{TransactionId}}}"}""", BodyId),
            null);

        var error = await Assert.ThrowsAsync<DominaiteRefusalException>(() => client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1042",
            IdempotencyKey = "checkout-order-1042",
        }));

        Assert.Equal(BodyId, error.RequestId);
        Assert.Equal("checkout-order-1042", error.IdempotencyKey);
    }

    [Fact]
    public async Task EveryListItemCarriesTheId()
    {
        const string plan =
            """{"id":"plan_0123456789abcdef0123456789abcdef","name":"Pro","amount":2500,"currency":"EUR","intervalUnit":"month","intervalCount":1,"status":"active"}""";
        using var client = ClientFor(200, $$$"""{"success":true,"data":[{{{plan}}},{{{plan}}}]}""", HeaderId);

        var plans = await client.ListPlansAsync();

        Assert.Equal(2, plans.Count);
        Assert.All(plans, item => Assert.Equal(HeaderId, item.RequestId));
    }

    [Fact]
    public async Task AControlCharacterInTheHeaderIsRemoved()
    {
        using var client = ClientFor(200, Success("{}", null), "req-\u0007abc");

        var ping = await client.PingAsync();

        Assert.Equal("req-abc", ping.RequestId);
    }

    [Fact]
    public async Task TheContractChargeExampleCarriesItsRequestId()
    {
        var example = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "merchant-api-contract.json")))
            .RootElement.GetProperty("endpoints").GetProperty("chargePaymentMethod").GetProperty("successExample");
        using var client = ClientFor(201, example.GetRawText(), null);

        var charge = await client.ChargePaymentMethodAsync(
            PaymentMethodId,
            new ChargeRequest { Amount = 2500, Currency = "EUR", OrderReference = "order-1043", IdempotencyKey = "charge-order-1043" });

        Assert.Equal(example.GetProperty("metadata").GetProperty("requestId").GetString(), charge.RequestId);
    }

    [Fact]
    public void TheRequestIdIsNotSerialized()
    {
        var json = JsonSerializer.Serialize(new Refund());

        Assert.DoesNotContain("requestId", json, StringComparison.OrdinalIgnoreCase);
    }
}
