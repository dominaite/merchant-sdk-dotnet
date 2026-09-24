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
    [InlineData("checkout", "order 1", 2500, "EUR")]
    [InlineData("checkout", "поръчка-1", 2500, "EUR")]
    [InlineData("check out", "order-1", 2500, "EUR")]
    public void BadPartsAreRejected(string scope, string orderId, long amountMinor, string currency)
    {
        Assert.Throws<DominaiteValidationException>(() => IdempotencyKeys.ForOrder(scope, orderId, amountMinor, currency));
    }

    /// <summary>A supplied key is held to visible ASCII too, on the call itself.</summary>
    [Theory]
    [InlineData("order 1042")]
    [InlineData("order-1042\t")]
    [InlineData("заказ-1042")]
    [InlineData("order-1042\u007F")]
    public async Task ASuppliedKeyOutsideVisibleAsciiIsRejectedBeforeAnythingIsSent(string key)
    {
        using var client = new DominaiteClient(
            "dmk_0123456789abcdef0123456789abcdef",
            "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            new DominaiteClientOptions { BaseUrl = "http://127.0.0.1:9/api" });

        var error = await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
            {
                Amount = 2500,
                Currency = "EUR",
                OrderReference = "order-1042",
                IdempotencyKey = key,
            }));
        Assert.Contains("visible ASCII", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryVisibleAsciiCharacterIsAllowed()
    {
        var visible = new string([.. Enumerable.Range(0x21, 0x7E - 0x21 + 1).Select(c => (char)c)]);

        // In two halves, to stay under the 100-character limit.
        Assert.Equal(94, visible.Length);
        Assert.Equal($"s-{visible[..47]}-1-EUR", IdempotencyKeys.ForOrder("s", visible[..47], 1, "EUR"));
        Assert.Equal($"s-{visible[47..]}-1-EUR", IdempotencyKeys.ForOrder("s", visible[47..], 1, "EUR"));
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
