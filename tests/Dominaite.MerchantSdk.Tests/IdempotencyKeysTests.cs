using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The order-derived key: the same order at the same amount is the same key, anything that
/// changes what gets charged is a different key, and a key that breaks the rules fails here.
/// </summary>
public class IdempotencyKeysTests
{
    [Fact]
    public void TheKeyIsScopeOrderAmountAndUppercasedCurrency()
    {
        Assert.Equal("checkout-order-1042-2500-EUR", IdempotencyKeys.ForOrder("checkout", "order-1042", 2500, "eur"));
    }

    /// <summary>A reload or a back button rebuilds the key from the same order and must land on the same session.</summary>
    [Fact]
    public void TheSameOrderAndAmountAlwaysGiveTheSameKey()
    {
        Assert.Equal(
            IdempotencyKeys.ForOrder("checkout", "order-1042", 2500, "EUR"),
            IdempotencyKeys.ForOrder("checkout", "order-1042", 2500, " eur "));
    }

    /// <summary>
    /// A changed cart has to get a new key: the gateway refuses a reused key with a different
    /// amount as IDEMPOTENCY_KEY_REUSED.
    /// </summary>
    [Fact]
    public void AChangedAmountCurrencyOrScopeGivesADifferentKey()
    {
        var original = IdempotencyKeys.ForOrder("checkout", "order-1042", 2500, "EUR");

        Assert.NotEqual(original, IdempotencyKeys.ForOrder("checkout", "order-1042", 2600, "EUR"));
        Assert.NotEqual(original, IdempotencyKeys.ForOrder("checkout", "order-1042", 2500, "BGN"));
        Assert.NotEqual(original, IdempotencyKeys.ForOrder("charge", "order-1042", 2500, "EUR"));
    }

    [Theory]
    [InlineData("", "order-1", 2500, "EUR")]
    [InlineData("checkout", " ", 2500, "EUR")]
    [InlineData("checkout", "order-1", 0, "EUR")]
    [InlineData("checkout", "order-1", -2500, "EUR")]
    [InlineData("checkout", "order-1", 2500, "EU")]
    [InlineData("checkout", "order-1", 2500, "EURO")]
    [InlineData("checkout", "order-1", 2500, "E1R")]
    [InlineData("checkout", "order-1", 2500, "")]
    [InlineData("checkout", "order\n1", 2500, "EUR")]
    public void BadPartsAreRejected(string scope, string orderId, long amountMinor, string currency)
    {
        Assert.Throws<DominaiteValidationException>(() => IdempotencyKeys.ForOrder(scope, orderId, amountMinor, currency));
    }

    /// <summary>The derived key obeys the same 100-character limit as a key you supply yourself.</summary>
    [Fact]
    public void AKeyLongerThanTheLimitIsRejected()
    {
        // "checkout-" + 83 + "-2500-EUR" = 101 characters.
        var orderId = new string('o', 83);

        var error = Assert.Throws<DominaiteValidationException>(
            () => IdempotencyKeys.ForOrder("checkout", orderId, 2500, "EUR"));
        Assert.Contains("100", error.Message, StringComparison.Ordinal);

        Assert.Equal(100, IdempotencyKeys.ForOrder("checkout", orderId[1..], 2500, "EUR").Length);
    }
}
