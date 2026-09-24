using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// Decimal prices to integer minor units, by the currency's exponent, never by a hardcoded x100.
/// </summary>
public class MinorUnitsTests
{
    [Theory]
    [InlineData("EUR", 2)]
    [InlineData("USD", 2)]
    [InlineData("GBP", 2)]
    [InlineData("BGN", 2)]
    [InlineData("RON", 2)]
    [InlineData("CHF", 2)]
    [InlineData("PLN", 2)]
    [InlineData("CZK", 2)]
    [InlineData("SEK", 2)]
    [InlineData("DKK", 2)]
    [InlineData("NOK", 2)]
    [InlineData("CAD", 2)]
    [InlineData("AUD", 2)]
    [InlineData("JPY", 0)]
    [InlineData("HUF", 0)]
    [InlineData("BHD", 3)]
    [InlineData("KWD", 3)]
    [InlineData("eur", 2)]
    [InlineData(" jpy ", 0)]
    public void TheExponentFollowsTheCurrency(string currency, int exponent)
    {
        Assert.Equal(exponent, MinorUnits.Exponent(currency));
    }

    /// <summary>The case the doc pins: 0.1 + 0.2 EUR must go out as 30, which double arithmetic gets wrong.</summary>
    [Fact]
    public void ThirtyCentsIsThirty()
    {
        Assert.Equal(30, MinorUnits.From(0.30m, "EUR"));
        Assert.Equal(30, MinorUnits.From(0.1m + 0.2m, "EUR"));
    }

    [Theory]
    [InlineData("25.00", "EUR", 2500)]
    [InlineData("25", "EUR", 2500)]
    [InlineData("0.01", "EUR", 1)]
    [InlineData("25.0", "EUR", 2500)]
    [InlineData("2500", "JPY", 2500)]
    [InlineData("1500", "HUF", 1500)]
    [InlineData("1.250", "KWD", 1250)]
    [InlineData("1.25", "BHD", 1250)]
    [InlineData("0", "EUR", 0)]
    public void TheAmountIsScaledByTheExponent(string amount, string currency, long expected)
    {
        Assert.Equal(expected, MinorUnits.From(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency));
    }

    /// <summary>
    /// Rounding a price is a business decision; the helper refuses instead of guessing. Strict on
    /// the decimals as written, so trailing zeros past the exponent are refused too.
    /// </summary>
    [Theory]
    [InlineData("0.305", "EUR")]
    [InlineData("25.000", "EUR")]
    [InlineData("1.5", "JPY")]
    [InlineData("2500.00", "JPY")]
    [InlineData("1500.00", "HUF")]
    [InlineData("1.2505", "KWD")]
    public void MoreDecimalsThanTheCurrencyHasAreRefused(string amount, string currency)
    {
        Assert.Throws<DominaiteValidationException>(
            () => MinorUnits.From(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency));
    }

    /// <summary>
    /// HUF is whole forints on the gateway, not ISO 4217's two decimals: 1500 HUF is 1500 on the
    /// wire. Getting this wrong is a 100x charge.
    /// </summary>
    [Fact]
    public void HufFollowsTheGatewayNotIso()
    {
        Assert.Equal(0, MinorUnits.Exponent("HUF"));
        Assert.Equal(1500, MinorUnits.From(1500m, "HUF"));
    }

    [Theory]
    [InlineData("ISK")]
    [InlineData("KRW")]
    [InlineData("OMR")]
    [InlineData("JOD")]
    [InlineData("TND")]
    [InlineData("isk")]
    public void CurrenciesWhereGatewayAndIsoDisagreeAreNotSupported(string currency)
    {
        var error = Assert.Throws<DominaiteValidationException>(() => MinorUnits.Exponent(currency));
        Assert.Contains("not supported", error.Message, StringComparison.Ordinal);
        Assert.Throws<DominaiteValidationException>(() => MinorUnits.From(1m, currency));
    }

    [Fact]
    public void ANegativeAmountIsRefused()
    {
        Assert.Throws<DominaiteValidationException>(() => MinorUnits.From(-0.30m, "EUR"));
    }

    [Theory]
    [InlineData("XYZ")]
    [InlineData("NZD")]
    [InlineData("")]
    [InlineData("EURO")]
    public void AnUnknownCurrencyIsRefused(string currency)
    {
        Assert.Throws<DominaiteValidationException>(() => MinorUnits.Exponent(currency));
        Assert.Throws<DominaiteValidationException>(() => MinorUnits.From(1m, currency));
    }

    [Fact]
    public void AValueTooLargeForALongIsRefused()
    {
        Assert.Throws<DominaiteValidationException>(() => MinorUnits.From(decimal.Truncate(decimal.MaxValue), "EUR"));
        Assert.Throws<DominaiteValidationException>(() => MinorUnits.From(100_000_000_000_000_000m, "EUR"));
    }
}
