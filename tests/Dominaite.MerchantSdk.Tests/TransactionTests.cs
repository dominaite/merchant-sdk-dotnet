using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The transaction list against the loopback server. The parameters travel in the query, and the
/// signature covers the bare path.
/// </summary>
public class TransactionTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string FirstPage = """
        {"success":true,"data":{"items":[{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderReference":"order-1042","type":"payment","status":"succeeded","amount":8440,"currency":"EUR","createdAt":"2026-10-01T08:00:00Z","completedAt":"2026-10-01T08:01:10Z","storedPaymentMethodId":null,"refundedAmount":2500},{"transactionId":"33333333-3333-4333-8333-333333333333","orderReference":null,"type":"charge","status":"failed","amount":2500,"currency":"EUR","createdAt":"2026-10-02T06:00:00Z","completedAt":null,"storedPaymentMethodId":"pm_0123456789abcdef0123456789abcdef","refundedAmount":null}],"nextCursor":"eyJjIjoiMjAyNi0xMC0wMlQwNjowMDowMFoiLCJpIjoiMzMzMyJ9+/="}}
        """;

    private const string LastPage = """
        {"success":true,"data":{"items":[{"transactionId":"44444444-4444-4444-8444-444444444444","type":"verification","status":"cancelled","amount":100,"currency":"EUR","createdAt":"2026-10-03T06:00:00Z"}],"nextCursor":null}}
        """;

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

    private static void AssertSignedTheBarePath(RecordedRequest sent)
    {
        var expected = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = sent.Header("X-Timestamp")!,
            Method = "GET",
            Path = DominaiteClient.TransactionsPath,
            IdempotencyKey = string.Empty,
            Body = string.Empty,
        });

        Assert.Equal(expected, sent.Header("X-Signature"));
    }

    [Fact]
    public async Task TheWindowTravelsInTheQueryInUtcAndTheBarePathIsSigned()
    {
        using var server = new MockServer(Reply.Raw(200, FirstPage));
        using var client = ClientFor(server);

        var page = await client.ListTransactionsAsync(new ListTransactionsRequest
        {
            From = new DateTimeOffset(2026, 10, 1, 2, 0, 0, TimeSpan.FromHours(2)),
            To = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
        });

        var sent = server.LastRequest;
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api" + DominaiteClient.TransactionsPath, sent.Path);
        Assert.Equal("from=2026-10-01T00%3A00%3A00Z&to=2026-10-08T00%3A00%3A00Z", sent.Query);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        AssertSignedTheBarePath(sent);

        Assert.Equal(2, page.Items.Count);
        var payment = page.Items[0];
        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", payment.TransactionId);
        Assert.Equal("order-1042", payment.OrderReference);
        Assert.Equal(TransactionTypes.Payment, payment.Type);
        Assert.Equal(TransactionStatuses.Succeeded, payment.Status);
        Assert.Equal(8440, payment.Amount);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 1, 10, TimeSpan.Zero), payment.CompletedAt);
        Assert.Equal(2500, payment.RefundedAmount);
        Assert.Null(payment.StoredPaymentMethodId);

        var charge = page.Items[1];
        Assert.Equal(TransactionTypes.Charge, charge.Type);
        Assert.Equal("pm_0123456789abcdef0123456789abcdef", charge.StoredPaymentMethodId);
        Assert.Null(charge.CompletedAt);
        Assert.Null(charge.RefundedAmount);
        Assert.Equal("33333333-3333-4333-8333-333333333333", charge.Raw.GetProperty("transactionId").GetString());

        Assert.Equal("eyJjIjoiMjAyNi0xMC0wMlQwNjowMDowMFoiLCJpIjoiMzMzMyJ9+/=", page.NextCursor);
        Assert.Equal(2, page.Raw.GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// The cursor goes back exactly as it came: percent-encoded, so a <c>+</c> is not read as a
    /// space. Status, limit and extra parameters follow the window, and the signature still covers
    /// the bare path.
    /// </summary>
    [Fact]
    public async Task TheNextPageSendsTheCursorUnchangedWithTheOtherParameters()
    {
        using var server = new MockServer(Reply.Raw(200, LastPage));
        using var client = ClientFor(server);

        var request = new ListTransactionsRequest
        {
            From = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 10, 8, 12, 30, 15, 250, TimeSpan.Zero),
            Status = TransactionStatuses.Succeeded,
            Limit = 100,
            Cursor = "eyJjIjoiMjAyNi0xMC0wMlQwNjowMDowMFoiLCJpIjoiMzMzMyJ9+/=",
        };
        request.Extra["type"] = "charge";
        request.Extra["limit"] = "5";

        var page = await client.ListTransactionsAsync(request);

        var sent = server.LastRequest;
        Assert.Equal(
            "from=2026-10-01T00%3A00%3A00Z&to=2026-10-08T12%3A30%3A15.2500000Z&status=succeeded&limit=100&cursor=eyJjIjoiMjAyNi0xMC0wMlQwNjowMDowMFoiLCJpIjoiMzMzMyJ9%2B%2F%3D&type=charge",
            sent.Query);
        AssertSignedTheBarePath(sent);

        var verification = Assert.Single(page.Items);
        Assert.Equal(TransactionTypes.Verification, verification.Type);
        Assert.Null(verification.OrderReference);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task AnEmptyWindowGivesAnEmptyLastPage()
    {
        using var server = new MockServer(Reply.Raw(200, """{"success":true,"data":{"items":[],"nextCursor":null}}"""));
        using var client = ClientFor(server);

        var page = await client.ListTransactionsAsync(new ListTransactionsRequest
        {
            From = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
        });

        Assert.Empty(page.Items);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task ARefusedWindowIsAnApiErrorWithTheFieldErrors()
    {
        using var server = new MockServer(Reply.Raw(
            400,
            """{"success":false,"error":{"code":"VALIDATION_ERROR","message":"Validation failed","statusCode":400,"validationErrors":[{"field":"","code":"VALUE_OUT_OF_RANGE","message":"from and to may be at most 31 days apart"}]}}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(() => client.ListTransactionsAsync(new ListTransactionsRequest
        {
            From = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero),
        }));

        Assert.Equal("VALIDATION_ERROR", error.Code);
        Assert.Equal(400, error.HttpStatus);
        Assert.Equal(
            "VALUE_OUT_OF_RANGE",
            error.RawResult.GetProperty("error").GetProperty("validationErrors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task TheListNeedsTheTransactionsScope()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(403, PermissionErrorCodes.InsufficientScope, "This API key does not have the 'transactions:read' scope this route requires"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaitePermissionException>(() => client.ListTransactionsAsync(new ListTransactionsRequest
        {
            From = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
            To = new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
        }));

        Assert.Contains("transactions:read", error.Message, StringComparison.Ordinal);
    }
}
