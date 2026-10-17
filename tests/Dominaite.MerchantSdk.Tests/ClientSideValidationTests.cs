using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// Values the gateway always refuses fail before anything is sent: an unset value that .NET would
/// otherwise send as a default, and a malformed currency.
/// </summary>
public class ClientSideValidationTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions { BaseUrl = server.BaseUrl, Timeout = TimeSpan.FromSeconds(10) });

    public static TheoryData<string, DateTimeOffset, DateTimeOffset> UnsetWindows()
    {
        var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        return new()
        {
            { "nothing set", default, default },
            { "From unset", default, from.AddDays(1) },
            { "To unset", from, default },
        };
    }

    [Theory]
    [MemberData(nameof(UnsetWindows))]
    public async Task AnUnsetTransactionWindowFailsBeforeSending(string label, DateTimeOffset from, DateTimeOffset to)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.ListTransactionsAsync(new ListTransactionsRequest { From = from, To = to }));

        Assert.True(server.Requests.Count == 0, label);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task APlanWithoutAPositiveAmountFailsBeforeSending(long amount)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.CreatePlanAsync(new CreatePlanRequest
        {
            Name = "Pro monthly",
            Amount = amount,
            Currency = "EUR",
            IntervalUnit = IntervalUnits.Month,
            IdempotencyKey = "plan-pro-monthly",
        }));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task ASessionConsentWithoutAcceptedAtFailsBeforeSending()
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "sub-42-1",
            IdempotencyKey = "checkout-sub-42-1",
            AgreementId = "agr_0123456789abcdef0123456789abcdef",
            Consent = new AgreementConsent { TextVersion = "recurring-2026-09", Ip = "203.0.113.7" },
        }));

        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    public async Task ACurrencyThatIsNotThreeUpperCaseLettersFailsBeforeSending(string currency)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = currency,
            OrderReference = "order-1042",
            IdempotencyKey = "checkout-order-1042",
        }));
        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.ChargePaymentMethodAsync(
            "pm_0123456789abcdef0123456789abcdef",
            new ChargeRequest { Amount = 2500, Currency = currency, OrderReference = "order-1043", IdempotencyKey = "charge-order-1043" }));

        Assert.Empty(server.Requests);
    }
}
