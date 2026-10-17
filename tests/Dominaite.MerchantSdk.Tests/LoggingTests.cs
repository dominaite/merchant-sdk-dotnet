using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The warning hook, the Dominaite-Sdk-Notice header and the debug log: what reaches the
/// callbacks, how often, and what never does.
/// </summary>
public class LoggingTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string CashierToken = "ctok_5e4f3a2b1c0d9e8f";

    private const string SessionPayload = """
        {"success":true,"checkout":{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","cashierKey":"ck_live_2f3a4d5e6f708192","cashierToken":"ctok_5e4f3a2b1c0d9e8f","amount":8440,"currency":"EUR","expiresAt":"2026-08-21T11:15:30.000Z"}}
        """;

    private const string Notice = "deprecated; current=\"0.7.0\"; sunset=\"2027-01-31\"; info=\"https://docs.example/sdk\"";

    private static Reply Ping(long skew = 0, Dictionary<string, string>? headers = null)
        => new()
        {
            Status = 200,
            Body = $"{{\"success\":true,\"data\":{{\"pong\":true,\"merchantId\":\"m_1\",\"clockSkewSeconds\":{skew}}}}}",
            Headers = headers ?? [],
        };

    private static Dictionary<string, string> NoticeHeader(string value) => new() { ["Dominaite-Sdk-Notice"] = value };

    private static DominaiteClient Client(MockServer server, List<string>? warnings, List<string>? debug)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
            OnWarning = warnings is null ? null : warnings.Add,
            OnDebug = debug is null ? null : debug.Add,
        });

    private static CheckoutSessionRequest Request() => new()
    {
        Amount = 8440,
        Currency = "EUR",
        OrderReference = "order-1042",
        IdempotencyKey = "checkout-order-1042-8440-EUR",
    };

    [Fact]
    public async Task TheSameNoticeWarnsOnceWithTheUpgradeMessage()
    {
        using var server = new MockServer(
            Ping(headers: NoticeHeader(Notice)),
            Ping(headers: NoticeHeader(Notice)),
            Ping(headers: NoticeHeader(Notice)));
        var warnings = new List<string>();
        using var client = Client(server, warnings, null);

        await client.PingAsync();
        await client.PingAsync();
        await client.PingAsync();

        var warning = Assert.Single(warnings);
        Assert.Equal(
            $"dominaite-dotnet {DominaiteClient.Version} is deprecated; upgrade to 0.7.0 (sunset 2027-01-31). https://docs.example/sdk",
            warning);
    }

    [Fact]
    public async Task ADistinctNoticeWarnsAgain()
    {
        using var server = new MockServer(
            Ping(headers: NoticeHeader(Notice)),
            Ping(headers: NoticeHeader("deprecated; current=\"0.8.0\"")));
        var warnings = new List<string>();
        using var client = Client(server, warnings, null);

        await client.PingAsync();
        await client.PingAsync();

        Assert.Equal(2, warnings.Count);
        Assert.Equal($"dominaite-dotnet {DominaiteClient.Version} is deprecated; upgrade to 0.8.0.", warnings[1]);
    }

    [Fact]
    public async Task ANoticeOnAnErrorResponseWarnsToo()
    {
        var reply = Reply.ErrorEnvelope(404, "NOT_FOUND", "Transaction not found");
        reply.Headers["Dominaite-Sdk-Notice"] = "deprecated";
        using var server = new MockServer(reply);
        var warnings = new List<string>();
        using var client = Client(server, warnings, null);

        await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.GetStatusAsync("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"));

        Assert.Equal($"dominaite-dotnet {DominaiteClient.Version} is deprecated.", Assert.Single(warnings));
    }

    [Fact]
    public async Task ANoticeOfAnotherKindIsPassedOnAsItIs()
    {
        using var server = new MockServer(Ping(headers: NoticeHeader("maintenance; at=\"2026-11-01\"")));
        var warnings = new List<string>();
        using var client = Client(server, warnings, null);

        await client.PingAsync();

        Assert.Equal($"dominaite-dotnet {DominaiteClient.Version}: maintenance; at=\"2026-11-01\"", Assert.Single(warnings));
    }

    [Fact]
    public async Task NoHeaderMeansNoWarning()
    {
        using var server = new MockServer(Ping(), Reply.Enveloped(SessionPayload));
        var warnings = new List<string>();
        using var client = Client(server, warnings, null);

        await client.PingAsync();
        await client.CreateCheckoutSessionAsync(Request());

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task DebugIsOffByDefault()
    {
        var notice = Ping(headers: NoticeHeader(Notice));
        notice.Headers["Dominaite-Request-Id"] = "req-1";
        using var server = new MockServer(notice, Reply.Enveloped(SessionPayload));
        var warnings = new List<string>();
        using var client = Client(server, warnings, null);

        await client.PingAsync();
        await client.CreateCheckoutSessionAsync(Request());

        // Only the notice: no debug line went to the warning hook.
        Assert.Single(warnings);
        Assert.DoesNotContain(warnings, line => line.Contains("/merchant-api/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheDebugLogHasOneLinePerRequest()
    {
        var created = Reply.Enveloped(SessionPayload);
        created.Headers["Dominaite-Request-Id"] = "req-42";
        using var server = new MockServer(Ping(), created);
        var debug = new List<string>();
        using var client = Client(server, [], debug);

        await client.PingAsync();
        await client.CreateCheckoutSessionAsync(Request());

        Assert.Equal(2, debug.Count);
        Assert.Matches(@"^dominaite-dotnet: GET /merchant-api/ping 200 \d+ms$", debug[0]);
        Assert.Matches(
            @"^dominaite-dotnet: POST /merchant-api/checkout/sessions 200 \d+ms request_id=req-42 idempotency_key=checkout-order-1042-8440-EUR$",
            debug[1]);
    }

    [Fact]
    public async Task ATransportFailureIsADebugLineWithErrorAsTheStatus()
    {
        var debug = new List<string>();
        using var client = new DominaiteClient(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = "http://127.0.0.1:1",
            Timeout = TimeSpan.FromSeconds(5),
            OnDebug = debug.Add,
            OnWarning = _ => { },
        });

        await Assert.ThrowsAsync<DominaiteTransportException>(() => client.PingAsync());

        Assert.Matches(@"^dominaite-dotnet: GET /merchant-api/ping error \d+ms$", Assert.Single(debug));
    }

    [Fact]
    public async Task NoLineEverHoldsTheSecretTheSignatureTheCashierTokenOrABody()
    {
        var created = Reply.Enveloped(SessionPayload);
        created.Headers["Dominaite-Sdk-Notice"] = Notice;
        created.Headers["Dominaite-Request-Id"] = "req-7";
        using var server = new MockServer(
            created,
            Reply.ErrorEnvelope(404, "NOT_FOUND", "Transaction not found"),
            Ping(skew: 120));
        var lines = new List<string>();
        using var client = Client(server, lines, lines);

        await client.CreateCheckoutSessionAsync(Request());
        await Assert.ThrowsAsync<DominaiteApiException>(
            () => client.GetStatusAsync("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"));
        await client.PingAsync();

        var signatures = server.Requests.Select(sent => sent.Header("X-Signature")!).ToList();
        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.DoesNotContain(Secret, line, StringComparison.Ordinal);
            Assert.DoesNotContain(CashierToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain("Transaction not found", line, StringComparison.Ordinal);
            Assert.DoesNotContain("8440", line.Replace("idempotency_key=checkout-order-1042-8440-EUR", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.DoesNotContain("{", line, StringComparison.Ordinal);
            foreach (var signature in signatures)
            {
                Assert.DoesNotContain(signature, line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task AThrowingHookDoesNotFailTheRequest()
    {
        using var server = new MockServer(Ping(skew: 90, headers: NoticeHeader(Notice)));
        using var client = new DominaiteClient(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            OnWarning = _ => throw new InvalidOperationException("broken sink"),
            OnDebug = _ => throw new InvalidOperationException("broken sink"),
        });

        var ping = await client.PingAsync();

        Assert.Equal(90, ping.ClockSkewSeconds);
    }

    [Theory]
    [InlineData(61, true)]
    [InlineData(-61, true)]
    [InlineData(60, false)]
    [InlineData(-60, false)]
    public async Task APingClockSkewOverSixtySecondsWarns(long skew, bool warns)
    {
        using var server = new MockServer(Ping(skew));
        var warnings = new List<string>();
        using var client = Client(server, warnings, null);

        await client.PingAsync();

        if (warns)
        {
            Assert.Equal(
                $"dominaite-dotnet: clock skew is {skew} s; requests fail at 300 s. Sync this server's clock.",
                Assert.Single(warnings));
        }
        else
        {
            Assert.Empty(warnings);
        }
    }

    [Fact]
    public async Task EachRetryAfterAFailureWarns()
    {
        using var server = new MockServer(Reply.Raw(503, ""), Reply.Raw(503, ""), Reply.Enveloped(SessionPayload));
        var warnings = new List<string>();
        using var client = Client(server, warnings, null);

        await client.CreateCheckoutSessionWithRetryAsync(
            Request(),
            new RetryOptions { Attempts = 3, BaseDelay = TimeSpan.FromMilliseconds(1) });

        Assert.Equal(2, warnings.Count);
        Assert.StartsWith("dominaite-dotnet: retrying CreateCheckoutSessionWithRetryAsync after ", warnings[0], StringComparison.Ordinal);
        Assert.EndsWith(" (attempt 2 of 3)", warnings[0], StringComparison.Ordinal);
        Assert.EndsWith(" (attempt 3 of 3)", warnings[1], StringComparison.Ordinal);
    }
}
