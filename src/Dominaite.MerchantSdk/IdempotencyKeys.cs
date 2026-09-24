using System.Globalization;

namespace Dominaite.MerchantSdk;

/// <summary>
/// Builds and checks idempotency keys. Every create-style call requires one: the SDK never makes
/// one up for you, because a key made up per call cannot recognise the same order coming back.
/// </summary>
/// <remarks>
/// Derive the key from the order, not from the request. The same order at the same amount then
/// produces the same key, so a page reload, a back button or a retried POST replays the session
/// that already exists instead of opening a second payment. A changed amount or currency produces
/// a different key, which is what the gateway wants: reusing a key with a different amount is
/// refused as <c>IDEMPOTENCY_KEY_REUSED</c>.
/// </remarks>
public static class IdempotencyKeys
{
    /// <summary>The longest key the gateway accepts.</summary>
    public const int MaxLength = 100;

    /// <summary>
    /// The order-derived key: <c>{scope}-{orderId}-{amountMinor}-{CURRENCY}</c>.
    /// </summary>
    /// <remarks>
    /// Use a different <paramref name="scope"/> per kind of call ("checkout" for a session,
    /// "charge" for a stored-card charge) so the two never collide for the same order. The key is
    /// checked against the same rules as a key you supply yourself, so a long order id fails here,
    /// before anything is sent.
    /// </remarks>
    /// <param name="scope">What the key is for, e.g. "checkout". Constant per call site.</param>
    /// <param name="orderId">Your own order id.</param>
    /// <param name="amountMinor">The amount in MINOR units, exactly as sent on the request.</param>
    /// <param name="currency">ISO 4217 currency; uppercased into the key.</param>
    /// <returns>The key, e.g. <c>checkout-order-1042-2500-EUR</c>.</returns>
    /// <exception cref="DominaiteValidationException">
    /// A blank part, a non-positive amount, a currency that is not three letters, or a key that
    /// breaks the key rules.
    /// </exception>
    public static string ForOrder(string scope, string orderId, long amountMinor, string currency)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new DominaiteValidationException("scope must not be empty");
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            throw new DominaiteValidationException("orderId must not be empty");
        }

        if (amountMinor <= 0)
        {
            throw new DominaiteValidationException(
                "amountMinor must be a positive integer in MINOR units (e.g. 2500 for 25.00 EUR)");
        }

        var code = currency?.Trim().ToUpperInvariant();
        if (code is not { Length: 3 } || !code.All(char.IsAsciiLetterUpper))
        {
            throw new DominaiteValidationException("currency must be a three-letter ISO 4217 code, e.g. EUR");
        }

        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{scope.Trim()}-{orderId.Trim()}-{amountMinor}-{code}");
        return Validate(key);
    }

    /// <summary>
    /// The key rules, applied to every key before it is signed: present, not blank, at most
    /// <see cref="MaxLength"/> characters, and no control characters (the key travels in an HTTP
    /// header). A valid key is returned unchanged, never rewritten.
    /// </summary>
    internal static string Validate(string? key)
    {
        if (key is null)
        {
            throw new DominaiteValidationException(
                "IdempotencyKey is required. Derive it from the order with IdempotencyKeys.ForOrder, "
                + "so a reload or a retry replays the same session instead of opening a second payment");
        }

        if (string.IsNullOrWhiteSpace(key))
        {
            throw new DominaiteValidationException("IdempotencyKey must not be empty");
        }

        if (key.Length > MaxLength)
        {
            throw new DominaiteValidationException($"IdempotencyKey must be at most {MaxLength} characters");
        }

        if (key.Any(char.IsControl))
        {
            throw new DominaiteValidationException("IdempotencyKey must not contain control characters");
        }

        return key;
    }
}
