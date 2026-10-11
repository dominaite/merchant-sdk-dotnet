using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dominaite.MerchantSdk;

/// <summary>
/// How a payment in the transaction list was taken: the wire values of
/// <see cref="TransactionItem.Type"/>, as constants plus the enumerable
/// <see cref="TransactionTypes.All"/>. Treat a value that is not listed as valid: the API can add
/// types later.
/// </summary>
public static class TransactionTypes
{
    /// <summary>The payer paid in a checkout session; no saved card was charged.</summary>
    public const string Payment = "payment";

    /// <summary>You, or the subscription schedule, charged a saved card.</summary>
    public const string Charge = "charge";

    /// <summary>
    /// A verify-only session saved a card. Not a purchase: the verification amount is released, or
    /// refunded when the provider captured it.
    /// </summary>
    public const string Verification = "verification";

    /// <summary>Every transaction type, in the gateway's order.</summary>
    public static IReadOnlyList<string> All { get; } = [Payment, Charge, Verification];
}

/// <summary>
/// The parameters for <see cref="DominaiteClient.ListTransactionsAsync"/>: a time window, and
/// optionally a status and a page.
/// </summary>
/// <remarks>
/// These travel as query parameters. The signature covers the path only, never the query.
/// </remarks>
public sealed class ListTransactionsRequest
{
    /// <summary>
    /// The start of the window, inclusive. Sent in UTC, whatever offset the value has.
    /// </summary>
    public DateTimeOffset From { get; set; }

    /// <summary>
    /// The end of the window, exclusive: later than <see cref="From"/> and at most 31 days after
    /// it. Sent in UTC, whatever offset the value has.
    /// </summary>
    public DateTimeOffset To { get; set; }

    /// <summary>
    /// Only the payments in this status: one of the <see cref="TransactionStatuses"/> values. Null
    /// for every status.
    /// </summary>
    public string? Status { get; set; }

    /// <summary>The page size, 1 to 100. Null for the default of 50.</summary>
    public int? Limit { get; set; }

    /// <summary>
    /// The <see cref="TransactionPage.NextCursor"/> of the previous page, unchanged. Keep
    /// <see cref="From"/>, <see cref="To"/> and <see cref="Status"/> the same as for that page.
    /// Null for the first page.
    /// </summary>
    public string? Cursor { get; set; }

    /// <summary>
    /// Extra query parameters this class does not model yet. Added after the modelled ones; a name
    /// that collides with a modelled parameter is ignored.
    /// </summary>
    public IDictionary<string, string> Extra { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// One page of <see cref="DominaiteClient.ListTransactionsAsync"/>: your online payments in the
/// window, oldest first.
/// </summary>
public sealed class TransactionPage
{
    /// <summary>The payments of this page, by creation time, oldest first. Empty when the window has none.</summary>
    public IReadOnlyList<TransactionItem> Items { get; set; } = [];

    /// <summary>
    /// The cursor of the next page: send it as <see cref="ListTransactionsRequest.Cursor"/>. Null
    /// on the last page of the window.
    /// </summary>
    public string? NextCursor { get; set; }

    /// <summary>The unwrapped page as the gateway sent it, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }
}

/// <summary>
/// One payment in the transaction list. Its fields and values are those of the status read, so a
/// row and <see cref="DominaiteClient.GetStatusAsync"/> of the same id agree. Fees are not in it.
/// </summary>
public sealed class TransactionItem
{
    /// <summary>The payment id: the transactionId the session or the charge returned.</summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>Your own order id from the create call. Null when you sent none.</summary>
    public string? OrderReference { get; set; }

    /// <summary>One of the <see cref="TransactionTypes"/> values, or a value added later.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>One of the <see cref="TransactionStatuses"/> values, or a value added later.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The amount in MINOR units of <see cref="Currency"/>.</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 currency.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>When the payment was created (UTC). The window and the order use this time.</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>
    /// When the payment first succeeded (UTC). Null when it never succeeded, and on some older
    /// payments.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The <c>pm_</c> id of the saved card a charge was placed on. Null on checkout payments, and on
    /// some older charges.
    /// </summary>
    public string? StoredPaymentMethodId { get; set; }

    /// <summary>The amount refunded so far in MINOR units. Null when nothing was refunded.</summary>
    public long? RefundedAmount { get; set; }

    /// <summary>The row as the gateway sent it, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }
}
