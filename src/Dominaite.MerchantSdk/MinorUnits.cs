using System.Collections.Frozen;

namespace Dominaite.MerchantSdk;

/// <summary>
/// Converts a decimal price into the integer MINOR units every amount on the wire is in, by the
/// currency's exponent as the Dominaite gateway reads it: 0.30 EUR is 30, 2500 JPY is 2500,
/// 1.250 KWD is 1250.
/// </summary>
/// <remarks>
/// <para>
/// The exponents are the gateway's, not ISO 4217's. They agree everywhere except HUF: the gateway
/// counts whole forints (exponent 0) where ISO 4217 says 2, so 1500 HUF goes on the wire as 1500.
/// ISK, KRW, OMR, JOD and TND are refused as not supported, because the gateway and ISO 4217
/// disagree on them and a wrong guess is a silent 100x or 10x charge. Any other currency this SDK
/// has no exponent for is refused too, rather than guessed at x100.
/// </para>
/// <para>
/// <see cref="decimal"/> only, never <see cref="double"/>: 0.1 + 0.2 in binary floating point is
/// not 0.3. The conversion is strict about the decimals written: a value with more fractional
/// digits than the currency has is refused even when they are zeros (<c>25.000m</c> EUR,
/// <c>2500.00m</c> JPY), and a negative value is refused. Rounding a price is a business decision
/// this SDK does not make for you.
/// </para>
/// </remarks>
public static class MinorUnits
{
    private static readonly FrozenDictionary<string, int> Exponents = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["EUR"] = 2,
        ["USD"] = 2,
        ["GBP"] = 2,
        ["CAD"] = 2,
        ["AUD"] = 2,
        ["CHF"] = 2,
        ["BGN"] = 2,
        ["RON"] = 2,
        ["PLN"] = 2,
        ["CZK"] = 2,
        ["SEK"] = 2,
        ["DKK"] = 2,
        ["NOK"] = 2,
        ["JPY"] = 0,

        // Whole forints: the gateway's exponent, not ISO 4217's 2.
        ["HUF"] = 0,
        ["BHD"] = 3,
        ["KWD"] = 3,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Currencies where the gateway and ISO 4217 disagree on the exponent.</summary>
    private static readonly FrozenSet<string> Unsupported
        = new[] { "ISK", "KRW", "OMR", "JOD", "TND" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The currencies this SDK knows the exponent of.</summary>
    public static IReadOnlyCollection<string> Currencies => Exponents.Keys;

    /// <summary>
    /// How many decimals the currency's minor unit has, as the gateway reads it: 2 for EUR, 0 for
    /// JPY and HUF, 3 for KWD.
    /// </summary>
    /// <param name="currency">ISO 4217 code, any case.</param>
    /// <returns>The exponent.</returns>
    /// <exception cref="DominaiteValidationException">A currency that is not supported or not known.</exception>
    public static int Exponent(string currency)
    {
        var code = currency?.Trim().ToUpperInvariant() ?? string.Empty;
        if (Unsupported.Contains(code))
        {
            throw new DominaiteValidationException(
                $"Currency {code} is not supported: the gateway and ISO 4217 disagree on its minor unit, so the amount cannot be converted safely");
        }

        if (!Exponents.TryGetValue(code, out var exponent))
        {
            throw new DominaiteValidationException(
                $"Unknown currency '{currency}': no minor-unit exponent for it, so the amount cannot be converted safely");
        }

        return exponent;
    }

    /// <summary>
    /// The amount in MINOR units: <c>MinorUnits.From(0.30m, "EUR")</c> is 30.
    /// </summary>
    /// <param name="amount">The price in major units, written with at most the currency's decimals.</param>
    /// <param name="currency">ISO 4217 code, any case.</param>
    /// <returns>The integer amount to put on the request.</returns>
    /// <exception cref="DominaiteValidationException">
    /// A currency that is not supported or not known, more fractional digits than the currency
    /// has, a negative value, or a value too large for a long.
    /// </exception>
    public static long From(decimal amount, string currency)
    {
        var exponent = Exponent(currency);
        var code = currency.Trim().ToUpperInvariant();

        if (amount < 0)
        {
            throw new DominaiteValidationException($"Amount {amount} {code} must not be negative");
        }

        // The scale is the number of decimals as written, so 25.000m is refused for EUR even
        // though its value would fit.
        if (amount.Scale > exponent)
        {
            throw new DominaiteValidationException(
                $"Amount {amount} has more decimals than {code} allows ({exponent}); round it yourself first");
        }

        try
        {
            return decimal.ToInt64(amount * Pow10(exponent));
        }
        catch (OverflowException)
        {
            throw new DominaiteValidationException($"Amount {amount} {code} is too large to convert");
        }
    }

    private static decimal Pow10(int exponent)
    {
        var factor = 1m;
        for (var i = 0; i < exponent; i++)
        {
            factor *= 10m;
        }

        return factor;
    }
}
