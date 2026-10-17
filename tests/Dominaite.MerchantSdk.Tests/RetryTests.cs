using System.Net;
using System.Text;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The client's retry policy: which calls and which failures it sends again, that every attempt
/// carries the same key, path and body, and the full-jitter waits between attempts. The waits and
/// the random source are injected, so nothing here sleeps.
/// </summary>
public class RetryTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PaymentMethodId = "pm_0123456789abcdef0123456789abcdef";
    private const string TransactionId = "1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d";
    private const string PlanId = "plan_0123456789abcdef0123456789abcdef";

    private const string ChargeBody =
        """{"success":true,"data":{"chargeId":"ch_1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d","status":"succeeded","transactionId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","sequence":1}}""";

    private const string RefundBody =
        """{"success":true,"data":{"refundId":"re_7c1e9a2b4d6f48a0b3c5d7e9f1a2b3c4","transactionId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","status":"pending","amount":2500,"currency":"EUR"}}""";

    private const string PingBody = """{"success":true,"data":{}}""";

    /// <summary>One request as the handler saw it.</summary>
    private sealed record Sent(string Method, string Path, string Body, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => this.Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// Answers each request with the next step: an exception thrown as a transport failure, or a
    /// status and a body. Records every request, the failed ones included.
    /// </summary>
    private sealed class ScriptedHandler(params object[] steps) : HttpMessageHandler
    {
        private readonly Queue<object> _steps = new(steps);

        public List<Sent> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(header => header.Key, header => string.Join(",", header.Value), StringComparer.OrdinalIgnoreCase);
            this.Requests.Add(new Sent(request.Method.Method, request.RequestUri!.AbsolutePath, body, headers));

            switch (this._steps.Dequeue())
            {
                case Exception error:
                    throw error;
                case HttpResponseMessage response:
                    return response;
                case (int status, string text):
                    return new HttpResponseMessage((HttpStatusCode)status)
                    {
                        Content = new StringContent(text, Encoding.UTF8, "application/json"),
                    };
                default:
                    throw new InvalidOperationException("Unknown step");
            }
        }
    }

    private static (int, string) Answer(int status, string body) => (status, body);

    private static (int, string) ErrorEnvelope(int status, string code)
        => (status, $$$"""{"success":false,"error":{"code":"{{{code}}}","message":"refused","statusCode":{{{status}}}}}""");

    private static HttpRequestException Dropped() => new("The connection was reset.");

    private static DominaiteClient ClientFor(
        ScriptedHandler handler,
        List<TimeSpan>? waits = null,
        int? maxRetries = null,
        double random = 0.5,
        List<string>? warnings = null)
    {
        var options = new DominaiteClientOptions
        {
            BaseUrl = "https://merchant-api.test/api",
            HttpClient = new HttpClient(handler),
            Sleep = (wait, _) =>
            {
                waits?.Add(wait);
                return Task.CompletedTask;
            },
            Random = () => random,
            OnWarning = message => warnings?.Add(message),
        };
        if (maxRetries is { } max)
        {
            options.MaxRetries = max;
        }

        return new DominaiteClient(KeyId, Secret, options);
    }

    private static ChargeRequest Charge() => new()
    {
        Amount = 2500,
        Currency = "EUR",
        OrderReference = "order-1043",
        IdempotencyKey = "charge-order-1043",
    };

    private static RefundRequest Refund() => new() { Amount = 2500, IdempotencyKey = "refund-order-1043" };

    /// <summary>Every attempt sent the same method, path, body and key, and each signature verifies against its own timestamp.</summary>
    private static void AssertSameSignedRequest(IReadOnlyList<Sent> requests, string key)
    {
        Assert.All(requests, sent =>
        {
            Assert.Equal(requests[0].Method, sent.Method);
            Assert.Equal(requests[0].Path, sent.Path);
            Assert.Equal(requests[0].Body, sent.Body);
            Assert.Equal(key, sent.Header("Idempotency-Key"));
            var expected = RequestSigner.Sign(new SignatureInput
            {
                Secret = Secret,
                Timestamp = sent.Header("X-Timestamp")!,
                Method = sent.Method,
                Path = sent.Path["/api".Length..],
                IdempotencyKey = key,
                Body = sent.Body,
            });
            Assert.Equal(expected, sent.Header("X-Signature"));
        });
    }

    [Fact]
    public void RetriesDefaultToTwo()
        => Assert.Equal(2, new DominaiteClientOptions().MaxRetries);

    [Fact]
    public void ANegativeMaxRetriesFailsConstruction()
        => Assert.Throws<DominaiteValidationException>(() => new DominaiteClient(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = DominaiteClient.SandboxBaseUrl,
            MaxRetries = -1,
        }));

    [Fact]
    public async Task ATransportFailureThenSuccessOnAChargeSendsTheSameKeyTwice()
    {
        var handler = new ScriptedHandler(Dropped(), Answer(201, ChargeBody));
        using var client = ClientFor(handler);

        var charge = await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

        Assert.Equal("ch_1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d", charge.ChargeId);
        Assert.Equal(2, handler.Requests.Count);
        AssertSameSignedRequest(handler.Requests, "charge-order-1043");
    }

    [Fact]
    public async Task ATransportFailureThenSuccessOnARefundSendsTheSameKeyTwice()
    {
        var handler = new ScriptedHandler(Dropped(), Answer(201, RefundBody));
        using var client = ClientFor(handler);

        var refund = await client.CreateRefundAsync(TransactionId, Refund());

        Assert.Equal("re_7c1e9a2b4d6f48a0b3c5d7e9f1a2b3c4", refund.RefundId);
        Assert.Equal(2, handler.Requests.Count);
        AssertSameSignedRequest(handler.Requests, "refund-order-1043");
    }

    [Fact]
    public async Task ACheckoutSessionCreateIsRetriedWithTheSameKey()
    {
        var handler = new ScriptedHandler(
            Answer(503, "<h1>down</h1>"),
            Answer(200, """{"success":true,"data":{"success":true,"checkout":{"transactionId":"1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d","cashierToken":"tok","cashierKey":"key","expiresAt":"2026-10-17T12:00:00Z"}}}"""));
        using var client = ClientFor(handler);

        var session = await client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1042",
            IdempotencyKey = "checkout-order-1042",
        });

        Assert.Equal(TransactionId, session.TransactionId);
        AssertSameSignedRequest(handler.Requests, "checkout-order-1042");
    }

    [Fact]
    public async Task MaxRetriesZeroSendsOnce()
    {
        var handler = new ScriptedHandler(Dropped(), Answer(201, ChargeBody));
        var waits = new List<TimeSpan>();
        using var client = ClientFor(handler, waits, maxRetries: 0);

        var error = await Assert.ThrowsAsync<DominaiteTransportException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Single(handler.Requests);
        Assert.Empty(waits);
        Assert.Equal("charge-order-1043", error.IdempotencyKey);
    }

    [Fact]
    public async Task A429IsNotRetriedAndKeepsItsRetryAfter()
    {
        var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"success":false,"error":{"code":"RATE_LIMIT_EXCEEDED","message":"slow down","statusCode":429}}""",
                Encoding.UTF8,
                "application/json"),
        };
        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        var handler = new ScriptedHandler(limited, Answer(201, ChargeBody));
        var waits = new List<TimeSpan>();
        using var client = ClientFor(handler, waits);

        var error = await Assert.ThrowsAsync<DominaiteRateLimitException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Single(handler.Requests);
        Assert.Empty(waits);
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
        Assert.Equal("charge-order-1043", error.IdempotencyKey);
    }

    [Fact]
    public async Task PaymentProcessingUnavailableIsRetried()
    {
        var handler = new ScriptedHandler(
            ErrorEnvelope(503, ErrorCodes.PaymentProcessingUnavailable),
            Answer(201, ChargeBody));
        using var client = ClientFor(handler);

        await client.ChargePaymentMethodAsync(PaymentMethodId, Charge());

        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(502, "CHARGE_OUTCOME_UNKNOWN")]
    [InlineData(502, "CHARGE_FAILED")]
    [InlineData(503, "PAYMENT_METHOD_CHARGES_DISABLED")]
    [InlineData(409, "DUPLICATE_REQUEST")]
    [InlineData(400, "VALIDATION_ERROR")]
    public async Task AChargeAnswerThatIsNotAnOutageIsNotRetried(int status, string code)
    {
        var handler = new ScriptedHandler(ErrorEnvelope(status, code), Answer(201, ChargeBody));
        using var client = ClientFor(handler);

        var error = await Assert.ThrowsAnyAsync<DominaiteException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Equal(code, error.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AReadIsRetried()
    {
        var handler = new ScriptedHandler(Dropped(), Answer(500, "{\"success\":false}"), Answer(200, PingBody));
        using var client = ClientFor(handler);

        await client.PingAsync();

        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, sent => Assert.Null(sent.Header("Idempotency-Key")));
    }

    [Fact]
    public async Task AWebhookEndpointCreateIsNeverRetried()
    {
        var handler = new ScriptedHandler(Dropped(), Answer(201, "{}"));
        using var client = ClientFor(handler, maxRetries: 5);

        await Assert.ThrowsAsync<DominaiteTransportException>(() => client.CreateWebhookEndpointAsync(new CreateWebhookEndpointRequest
        {
            Url = "https://shop.example.com/webhooks/dominaite",
            Events = [WebhookEventTypes.PaymentSucceeded],
            IdempotencyKey = "webhook-orders",
        }));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task APatchAndADeleteAreNotRetried()
    {
        var handler = new ScriptedHandler(Dropped(), Dropped(), Dropped());
        using var client = ClientFor(handler, maxRetries: 5);

        await Assert.ThrowsAsync<DominaiteTransportException>(
            () => client.UpdatePlanAsync(PlanId, new UpdatePlanRequest { Name = "Pro" }));
        await Assert.ThrowsAsync<DominaiteTransportException>(() => client.DeletePlanAsync(PlanId));
        await Assert.ThrowsAsync<DominaiteTransportException>(() => client.RevokePaymentMethodAsync(PaymentMethodId));

        Assert.Equal(["PATCH", "DELETE", "DELETE"], handler.Requests.Select(sent => sent.Method));
    }

    [Fact]
    public async Task WhenEveryAttemptFailsTheLastErrorIsThrownAfterMaxRetriesPlusOneSends()
    {
        var handler = new ScriptedHandler(Dropped(), Dropped(), Answer(502, ""));
        var warnings = new List<string>();
        using var client = ClientFor(handler, warnings: warnings);

        var error = await Assert.ThrowsAsync<DominaiteTransportException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(502, error.HttpStatus);
        Assert.Equal("charge-order-1043", error.IdempotencyKey);
        Assert.Equal(
            [
                $"dominaite-dotnet: retrying POST {DominaiteClient.PaymentMethodsPath}/{PaymentMethodId}/charges after DominaiteTransportException (attempt 2 of 3)",
                $"dominaite-dotnet: retrying POST {DominaiteClient.PaymentMethodsPath}/{PaymentMethodId}/charges after DominaiteTransportException (attempt 3 of 3)",
            ],
            warnings);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public async Task EachWaitIsTheRandomShareOfAnExponentialCeilingCappedAtEightSeconds(double random)
    {
        var steps = Enumerable.Range(0, 7).Select(_ => (object)Dropped()).Append(Answer(200, PingBody)).ToArray();
        var handler = new ScriptedHandler(steps);
        var waits = new List<TimeSpan>();
        using var client = ClientFor(handler, waits, maxRetries: 7, random: random);

        await client.PingAsync();

        double[] ceilings = [500, 1000, 2000, 4000, 8000, 8000, 8000];
        Assert.Equal(ceilings.Select(ceiling => TimeSpan.FromMilliseconds(ceiling * random)), waits);
        Assert.All(waits, wait => Assert.InRange(wait, TimeSpan.Zero, TimeSpan.FromSeconds(8)));
    }

    [Fact]
    public async Task TheDeprecatedWrapperUsesItsOwnAttemptsAndDoesNotStackOnTheClientPolicy()
    {
        var handler = new ScriptedHandler(Dropped(), Dropped(), Dropped(), Dropped(), Dropped(), Dropped());
        var waits = new List<TimeSpan>();
        using var client = ClientFor(handler, waits, maxRetries: 2, random: 1.0);

        await Assert.ThrowsAsync<DominaiteTransportException>(() => client.CreateCheckoutSessionWithRetryAsync(
            new CheckoutSessionRequest
            {
                Amount = 2500,
                Currency = "EUR",
                OrderReference = "order-1042",
                IdempotencyKey = "checkout-order-1042",
            },
            new RetryOptions { Attempts = 4, BaseDelay = TimeSpan.FromMilliseconds(100) }));

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal([100, 200, 400], waits.Select(wait => wait.TotalMilliseconds));
    }

    [Fact]
    public async Task CancellingDuringAWaitStopsTheCall()
    {
        var handler = new ScriptedHandler(Dropped(), Answer(200, PingBody));
        using var cancel = new CancellationTokenSource();
        using var client = new DominaiteClient(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = "https://merchant-api.test/api",
            HttpClient = new HttpClient(handler),
            Sleep = (_, token) =>
            {
                cancel.Cancel();
                return Task.Delay(Timeout.Infinite, token);
            },
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PingAsync(cancel.Token));

        Assert.Single(handler.Requests);
    }
}
