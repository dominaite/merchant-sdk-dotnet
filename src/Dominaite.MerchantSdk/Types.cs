using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dominaite.MerchantSdk;

/// <summary>
/// Optional payer details. Prefilled fields are hidden from the payer in the widget, so the
/// checkout form stays short.
/// </summary>
public sealed class Customer
{
    /// <summary>Payer first name.</summary>
    public string? FirstName { get; set; }

    /// <summary>Payer last name.</summary>
    public string? LastName { get; set; }

    /// <summary>Payer email.</summary>
    public string? Email { get; set; }

    /// <summary>Payer phone.</summary>
    public string? Phone { get; set; }
}

/// <summary>
/// The parameters for <see cref="DominaiteClient.CreateCheckoutSessionAsync"/>.
/// </summary>
/// <remarks>
/// Property order here is the JSON order on the wire, and the body is serialized exactly once:
/// the bytes that are hashed for the signature are the bytes that are sent.
/// </remarks>
public sealed class CheckoutSessionRequest
{
    /// <summary>
    /// The amount in MINOR units: 2500 is 25.00 EUR. Integers only. Leave it at 0 on a
    /// verify-only session (<see cref="VerifyOnly"/>).
    /// </summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 currency, e.g. "EUR".</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Your own order id, at most 100 characters. It shows up in your dashboard.</summary>
    public string OrderReference { get; set; } = string.Empty;

    /// <summary>Optional payer details.</summary>
    public Customer? Customer { get; set; }

    /// <summary>ISO 3166-1 alpha-2 payer country.</summary>
    public string? Country { get; set; }

    /// <summary>ISO 639-1 widget UI language.</summary>
    public string? Language { get; set; }

    /// <summary>"light", "dark" or "bright".</summary>
    public string? Theme { get; set; }

    /// <summary>Free-text description stored on the transaction.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The storefront (one of your websites) this session belongs to. Optional. It must be one of
    /// your storefronts, and a key bound to one storefront refuses any other. Null is omitted from
    /// the body.
    /// </summary>
    public Guid? LocationId { get; set; }

    /// <summary>
    /// Ask the payer to save their card for later off-session charges. Once the session is
    /// paid, <see cref="CheckoutStatus.StoredPaymentMethod"/> carries the stored method to
    /// charge with <see cref="DominaiteClient.ChargePaymentMethodAsync"/>.
    /// </summary>
    /// <remarks>
    /// Null is omitted from the body, so a request that never sets it sends the exact bytes it
    /// always did.
    /// </remarks>
    public bool? SaveCard { get; set; }

    /// <summary>
    /// The <c>agr_</c> id of a pending agreement. The session then takes the agreement's first
    /// payment: the card is always saved, and the paid session activates the agreement. Null for
    /// a one-off checkout.
    /// </summary>
    /// <remarks>
    /// Amount and currency must be the agreement's own. On a plan with a trial, the amount is
    /// your card-verification charge in the agreement's currency. Send <see cref="Consent"/> with
    /// it, unless the agreement already has consent with a platform wording version. A refusal
    /// arrives as a
    /// <see cref="DominaiteRefusalException"/> with an <c>AGREEMENT_*</c> code.
    /// </remarks>
    public string? AgreementId { get; set; }

    /// <summary>
    /// The payer accepted the platform's recurring-payment wording: which version, when, and from
    /// which IP address. Only with <see cref="AgreementId"/>. Null is omitted from the body.
    /// </summary>
    public AgreementConsent? Consent { get; set; }

    /// <summary>
    /// How the payer enters the card: <see cref="CheckoutIntegrations.Widget"/> (the default
    /// when null) or <see cref="CheckoutIntegrations.Fields"/>, card fields in your own page.
    /// </summary>
    /// <remarks>
    /// Card fields are enabled per merchant on request; asking for them on an account without
    /// them is a 400 <c>INVALID_SELECTION</c>. The value is part of the idempotency identity:
    /// replaying a key with a different integration is <c>IDEMPOTENCY_KEY_REUSED</c>. Null is
    /// omitted from the body.
    /// </remarks>
    public string? Integration { get; set; }

    /// <summary>
    /// Save the payer's card without a payment. The checkout places a small authorization, saves
    /// the card and releases the authorization. Null is omitted from the body.
    /// </summary>
    /// <remarks>
    /// Leave <see cref="Amount"/> at 0: with <c>VerifyOnly = true</c> the SDK sends 0 and does not
    /// refuse it. <see cref="SaveCard"/> is implied (do not set it to false),
    /// <see cref="Integration"/> must be the widget or null, and <see cref="AgreementId"/> is
    /// refused. Verify-only is off until we switch it on for your
    /// account; until then the gateway answers HTTP 403 <c>VERIFY_ONLY_NOT_ENABLED</c>
    /// (<see cref="DominaitePermissionException"/>). The status read then has
    /// <see cref="CheckoutStatus.VerifyOnly"/> true, and a non-null
    /// <see cref="CheckoutStatus.StoredPaymentMethod"/> means the card is verified.
    /// </remarks>
    public bool? VerifyOnly { get; set; }

    /// <summary>
    /// The idempotency key. Required. It travels in the header and in the signature, never in the
    /// body.
    /// </summary>
    /// <remarks>
    /// Derive it from the order with <see cref="IdempotencyKeys.ForOrder"/>: the same order at the
    /// same amount then replays the same session on a reload or a retry, and a changed amount gets
    /// a new key. Null or blank is rejected before anything is sent. Reusing a key never opens a
    /// second payment.
    /// </remarks>
    [JsonIgnore]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Extra body fields this class does not model yet. Merged into the JSON body at the top
    /// level; a key that collides with a modelled property is ignored.
    /// </summary>
    [JsonIgnore]
    public IDictionary<string, object?> Extra { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>
/// The payer's acceptance of the platform's recurring-payment wording, sent with a subscription
/// session (<see cref="CheckoutSessionRequest.Consent"/>) or an agreement.
/// </summary>
public sealed class AgreementConsent
{
    /// <summary>
    /// The version of the platform wording the payer accepted, at most 50 characters. Use a
    /// version the platform publishes: <c>recurring.consentTextVersions</c> in the integration
    /// contract, for example <c>recurring-2026-09</c>. A subscription session refuses any other
    /// version.
    /// </summary>
    public string TextVersion { get; set; } = string.Empty;

    /// <summary>When the payer accepted. Not in the future.</summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>The payer's IP address (IPv4 or IPv6) when they accepted.</summary>
    public string Ip { get; set; } = string.Empty;
}

/// <summary>
/// What <see cref="DominaiteClient.CreateCheckoutSessionAsync"/> returns.
/// </summary>
public sealed class CheckoutSession
{
    /// <summary>Dominaite's payment id. Store it against your order; poll status with it.</summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>The provider-facing correlation id (<c>dom_...</c>). You never need it.</summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Feeds the widget's <c>data-cashier-key</c>. A per-payment value, not a credential.</summary>
    public string CashierKey { get; set; } = string.Empty;

    /// <summary>
    /// Feeds the widget's <c>data-cashier-token</c>. A per-payment bearer value for the session:
    /// return it to the page that renders the widget, and never log it.
    /// </summary>
    public string CashierToken { get; set; } = string.Empty;

    /// <summary>The locked amount in MINOR units.</summary>
    public long Amount { get; set; }

    /// <summary>The locked ISO 4217 currency.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>When the session stops being usable. Sessions last about 2 hours.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// What the session was created for: <see cref="CheckoutIntegrations.Widget"/> or
    /// <see cref="CheckoutIntegrations.Fields"/>. For fields, <see cref="CashierKey"/> and
    /// <see cref="CashierToken"/> are the card fields key and session token.
    /// </summary>
    public string Integration { get; set; } = string.Empty;

    /// <summary>
    /// Set only for <see cref="CheckoutIntegrations.Fields"/>: the browser credential the
    /// checkout.js drop-in sends for this session. Opaque, at most 128 characters, the same on
    /// every replay. Hand it to the payer's page with the other session values; never log it or
    /// keep it past the session.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// True on a verify-only session, where <see cref="Amount"/> is the verification
    /// authorization. Null on every other session.
    /// </summary>
    public bool? VerifyOnly { get; set; }

    /// <summary>
    /// The <c>agr_</c> id when this session takes an agreement's first payment, else null.
    /// </summary>
    public string? AgreementId { get; set; }

    /// <summary>The unparsed payload, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }

    /// <summary>
    /// A description with the session token and client secret redacted, so a logged session
    /// object cannot leak a bearer credential.
    /// </summary>
    /// <returns>The redacted description.</returns>
    public override string ToString()
        => $"CheckoutSession {{ TransactionId = {this.TransactionId}, OrderId = {this.OrderId}, "
            + $"Amount = {this.Amount}, Currency = {this.Currency}, Integration = {this.Integration}, "
            + "CashierToken = [REDACTED], ClientSecret = [REDACTED] }";
}

/// <summary>
/// The checkout integration wire values, as constants plus the enumerable
/// <see cref="CheckoutIntegrations.All"/>.
/// </summary>
public static class CheckoutIntegrations
{
    /// <summary>The hosted cashier widget. The default.</summary>
    public const string Widget = "widget";

    /// <summary>
    /// Card fields rendered in your own page by the checkout.js drop-in. Enabled per merchant on
    /// request.
    /// </summary>
    public const string Fields = "fields";

    /// <summary>The whole vocabulary, in the order the canonical contract lists it.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Widget,
        Fields,
    ];
}

/// <summary>
/// The transaction status wire values, as constants plus the enumerable
/// <see cref="TransactionStatuses.All"/>.
/// </summary>
public static class TransactionStatuses
{
    /// <summary>The session exists and nobody has paid yet.</summary>
    public const string Pending = "pending";

    /// <summary>The payment is in flight.</summary>
    public const string Processing = "processing";

    /// <summary>The customer paid. The ONLY value that means paid.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>The payment failed.</summary>
    public const string Failed = "failed";

    /// <summary>Paid and then fully returned.</summary>
    public const string Refunded = "refunded";

    /// <summary>Paid and then partly returned.</summary>
    public const string PartiallyRefunded = "partially_refunded";

    /// <summary>The payment was cancelled before completion.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>
    /// The payment is disputed (a chargeback is open). Not terminal: a dispute resolves later.
    /// </summary>
    public const string Disputed = "disputed";

    /// <summary>Authorized, awaiting capture. The payer HAS paid.</summary>
    public const string RequiresCapture = "requires_capture";

    /// <summary>The payer never paid and the session aged out.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>
    /// The whole vocabulary, in the order the canonical contract lists it. The contract test
    /// pins this against the vendored <c>merchant-api-contract.json</c>, so a status the API
    /// adds cannot land in one SDK and be missed here.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Pending,
        Processing,
        Succeeded,
        Failed,
        Refunded,
        PartiallyRefunded,
        Cancelled,
        Disputed,
        RequiresCapture,
        Abandoned,
    ];
}

/// <summary>
/// The payment method categories the merchant API reports in
/// <see cref="CheckoutStatus.PaymentMethod"/>, as constants plus the enumerable
/// <see cref="PaymentMethodCategories.All"/>.
/// </summary>
/// <remarks>
/// Reporting data, not a money-flow switch: a wallet payment refunds, captures and disputes
/// exactly like a card payment.
/// </remarks>
public static class PaymentMethodCategories
{
    /// <summary>A card payment.</summary>
    public const string Card = "card";

    /// <summary>A wallet payment; <see cref="CheckoutStatus.WalletType"/> says which wallet.</summary>
    public const string Wallet = "wallet";

    /// <summary>A bank transfer.</summary>
    public const string BankTransfer = "bank_transfer";

    /// <summary>A SEPA payment.</summary>
    public const string Sepa = "sepa";

    /// <summary>The whole vocabulary, in the gateway's order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Card,
        Wallet,
        BankTransfer,
        Sepa,
    ];
}

/// <summary>
/// The wallets the gateway names in <see cref="CheckoutStatus.WalletType"/>, as constants plus
/// the enumerable <see cref="WalletTypes.All"/>.
/// </summary>
/// <remarks>
/// The field can carry a lower-cased identifier for a wallet not in this list yet. Treat an
/// unknown value as a valid wallet, not an error.
/// </remarks>
public static class WalletTypes
{
    /// <summary>Apple Pay.</summary>
    public const string ApplePay = "apple_pay";

    /// <summary>Google Pay.</summary>
    public const string GooglePay = "google_pay";

    /// <summary>Samsung Pay.</summary>
    public const string SamsungPay = "samsung_pay";

    /// <summary>
    /// The whole vocabulary, in the gateway's order. The wire contract test pins this against
    /// the vendored <c>merchant-api-wire-contract.json</c>.
    /// </summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ApplePay,
        GooglePay,
        SamsungPay,
    ];
}

/// <summary>
/// What <see cref="DominaiteClient.GetStatusAsync"/> returns.
/// </summary>
public sealed class CheckoutStatus
{
    /// <summary>Dominaite's payment id.</summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>The provider-facing correlation id.</summary>
    public string OrderId { get; set; } = string.Empty;

    /// <summary>Your own order id, echoed back.</summary>
    public string? OrderReference { get; set; }

    /// <summary>
    /// The payment processor's reference for the transaction. Null until known, and on a sale
    /// settled by reconciliation without a processor webhook. Refund and cancel events carry the
    /// original sale's reference. Absent on gateways that predate the field.
    /// </summary>
    public string? PspReference { get; set; }

    /// <summary>
    /// One of the <see cref="TransactionStatuses"/> values. Ask <see cref="IsPaid"/> rather than
    /// comparing by hand.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>The amount in MINOR units, as locked at session mint.</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 currency.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// How much of the amount has been returned, in MINOR units. Null when nothing was refunded,
    /// which is not the same as zero.
    /// </summary>
    public long? RefundedAmount { get; set; }

    /// <summary>
    /// How the payer paid, one of the <see cref="PaymentMethodCategories"/> values. Null while the
    /// payment is still open (no method chosen yet) and on older transactions.
    /// </summary>
    /// <remarks>
    /// Reporting data only: a wallet payment refunds, captures and disputes like a card payment,
    /// so do not branch payment handling on it.
    /// </remarks>
    public string? PaymentMethod { get; set; }

    /// <summary>
    /// Which wallet, when <see cref="PaymentMethod"/> is <c>wallet</c>: one of the
    /// <see cref="WalletTypes"/> values, or a lower-cased identifier for a wallet not in that list
    /// yet. Treat an unknown value as a valid wallet, not an error. Null for non-wallet payments.
    /// </summary>
    public string? WalletType { get; set; }

    /// <summary>When the session was created (UTC).</summary>
    public DateTimeOffset? CreatedAt { get; set; }

    /// <summary>When the status last changed (UTC). Null when it never changed.</summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// The payer-checkout deadline: present while the payer still has an action to take, null
    /// once the payer's part is over. Null is NOT the same as terminal - branch on
    /// <see cref="Status"/> for liveness.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>
    /// The card kept on file for this payment. Present once a session created with
    /// <see cref="CheckoutSessionRequest.SaveCard"/> has been approved, and it stays present
    /// after a revoke with status <c>revoked</c>; null (absent on the wire) until then, for
    /// sessions without SaveCard, and for declined or abandoned ones. Persist
    /// <see cref="StoredPaymentMethod.Id"/> against your customer; it is what
    /// <see cref="DominaiteClient.ChargePaymentMethodAsync"/> takes.
    /// </summary>
    /// <remarks>
    /// Not to be confused with <see cref="PaymentMethod"/>, which is the string category of how
    /// the payer paid (<c>card</c>, <c>wallet</c>, ...).
    /// </remarks>
    public StoredPaymentMethod? StoredPaymentMethod { get; set; }

    /// <summary>
    /// What to do with a <c>failed</c> payment: one of the <see cref="DeclineClasses"/> values, or
    /// a value added later. Null while the payment is open, on every status other than
    /// <c>failed</c>, and on a failure that has no class yet.
    /// </summary>
    public string? DeclineClass { get; set; }

    /// <summary>
    /// True when you started this payment on a stored card (a charge), false for a payer-present
    /// checkout session.
    /// </summary>
    public bool IsRecurring { get; set; }

    /// <summary>
    /// True when the session was a verify-only card check, not a payment. Null on every other
    /// session.
    /// </summary>
    /// <remarks>
    /// <see cref="Status"/> stays the plain transaction status: a checked card reads
    /// <c>requires_capture</c>, then <c>cancelled</c> once the authorization is released. A
    /// non-null <see cref="StoredPaymentMethod"/> is what says the card is verified.
    /// </remarks>
    public bool? VerifyOnly { get; set; }

    /// <summary>
    /// The agreement this payment belongs to: the agreement whose first payment the session took,
    /// or the agreement a charge named. Null on a one-off checkout or charge.
    /// </summary>
    /// <remarks>
    /// Read <see cref="CheckoutStatusAgreement.Status"/> to know whether the subscription started:
    /// it stays <c>pending</c> after a decline or an abandoned session.
    /// </remarks>
    public CheckoutStatusAgreement? Agreement { get; set; }

    /// <summary>
    /// The <c>data.sequence</c> of the latest <c>charge.*</c> event about this transaction, to order
    /// this read against those events. 0 on a checkout payment and before the first
    /// <c>charge.*</c> event.
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>The unparsed payload, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }

    /// <summary>
    /// True only for <c>succeeded</c>. <c>refunded</c> and <c>partially_refunded</c> mean the
    /// customer paid and was then (partly) returned, which is a different question.
    /// </summary>
    /// <remarks>
    /// <c>requires_capture</c> is false here too, but it is NOT "unpaid": the payer has paid and
    /// the funds are held awaiting capture. Keep polling it rather than treating it as an
    /// abandoned order.
    /// </remarks>
    [JsonIgnore]
    public bool IsPaid => string.Equals(this.Status, TransactionStatuses.Succeeded, StringComparison.Ordinal);

    /// <summary>
    /// False while the payment can still change, true once it cannot.
    /// </summary>
    /// <remarks>
    /// <c>pending</c>, <c>processing</c>, <c>requires_capture</c> and <c>disputed</c> are not
    /// terminal. A dispute resolves later, so keep watching it; <see cref="IsPaid"/> is false for
    /// it too, although the money did move. An unrecognised status is also reported as NOT
    /// terminal, so a status the API adds later makes you keep polling rather than silently close
    /// an order that is still open.
    /// </remarks>
    [JsonIgnore]
    public bool IsTerminal => this.Status switch
    {
        TransactionStatuses.Succeeded => true,
        TransactionStatuses.Failed => true,
        TransactionStatuses.Refunded => true,
        TransactionStatuses.PartiallyRefunded => true,
        TransactionStatuses.Cancelled => true,
        TransactionStatuses.Abandoned => true,
        _ => false,
    };
}

/// <summary>
/// The agreement on a status read: <see cref="CheckoutStatus.Agreement"/>.
/// </summary>
public sealed class CheckoutStatusAgreement
{
    /// <summary>The <c>agr_</c> id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The agreement's status now: one of the <see cref="AgreementStatuses"/> values, or a value
    /// added later. <c>pending</c> until the payment is approved, then <c>active</c>, or
    /// <c>completed</c> for a one-period plan.
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>When the next charge is due (UTC). Null while pending and once the agreement ended.</summary>
    public DateTimeOffset? NextChargeAt { get; set; }
}

/// <summary>
/// The agreement status wire values, as constants plus the enumerable
/// <see cref="AgreementStatuses.All"/>. Treat a value that is not listed as valid: the API can
/// add statuses later.
/// </summary>
public static class AgreementStatuses
{
    /// <summary>Created, with no card on file and nothing charged yet. Its subscription session activates it.</summary>
    public const string Pending = "pending";

    /// <summary>The first period is paid and the card is on file. The platform charges each period.</summary>
    public const string Active = "active";

    /// <summary>
    /// A period's charge failed for good (the agreement's past-due reason says why). Still alive:
    /// a new card that pays the period makes it active again. Soft-decline retries do not put it
    /// here.
    /// </summary>
    public const string PastDue = "past_due";

    /// <summary>You ended it. No more charges, ever.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>Every period of a plan with a fixed number of periods was charged.</summary>
    public const string Completed = "completed";

    /// <summary>A final failure. Reserved: the platform does not set it today.</summary>
    public const string Failed = "failed";

    /// <summary>Every agreement status, in the gateway's order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Pending,
        Active,
        PastDue,
        Cancelled,
        Completed,
        Failed,
    ];
}

/// <summary>
/// The stored payment method status wire values, as constants plus the enumerable
/// <see cref="StoredPaymentMethodStatuses.All"/>.
/// </summary>
public static class StoredPaymentMethodStatuses
{
    /// <summary>The card can be charged. The ONLY value that means chargeable.</summary>
    public const string Active = "active";

    /// <summary>
    /// What <see cref="DominaiteClient.RevokePaymentMethodAsync"/> leaves behind; a charge on it
    /// is refused with <c>PAYMENT_METHOD_NOT_ACTIVE</c>.
    /// </summary>
    public const string Revoked = "revoked";

    /// <summary>The card's expiry date has passed; a charge on it is refused.</summary>
    public const string Expired = "expired";

    /// <summary>
    /// The platform stopped the card on its own; <see cref="StoredPaymentMethod.RetiredReason"/>
    /// says why. It never becomes active again, so ask the customer to save a card again. A charge
    /// on it is refused with <c>PAYMENT_METHOD_NOT_ACTIVE</c>; revoking it still works.
    /// </summary>
    public const string Retired = "retired";

    /// <summary>The whole vocabulary, in the order the canonical contract lists it.</summary>
    public static IReadOnlyList<string> All { get; } = [Active, Revoked, Expired, Retired];
}

/// <summary>
/// Why the platform retired a stored payment method, as constants plus the enumerable
/// <see cref="StoredPaymentMethodRetiredReasons.All"/>. Treat a value not listed here as retired
/// for an unknown reason.
/// </summary>
public static class StoredPaymentMethodRetiredReasons
{
    /// <summary>A charge on the card was declined as final.</summary>
    public const string HardDecline = "hard_decline";

    /// <summary>A charge on the card was disputed.</summary>
    public const string Chargeback = "chargeback";

    /// <summary>The payment that saved the card was fully refunded or disputed.</summary>
    public const string SourceSaleReversed = "source_sale_reversed";

    /// <summary>The whole vocabulary, in the order the canonical contract lists it.</summary>
    public static IReadOnlyList<string> All { get; } = [HardDecline, Chargeback, SourceSaleReversed];
}

/// <summary>
/// A card kept on file by a session that asked for <see cref="CheckoutSessionRequest.SaveCard"/>.
/// Display fields only: the card number never reaches this SDK, and the provider token behind
/// the id never leaves the gateway. <see cref="Brand"/>, <see cref="Last4"/> and the expiry are
/// null when the provider did not report them (the gateway omits null fields on the wire; the
/// SDK reads absent as null).
/// </summary>
public sealed class StoredPaymentMethod
{
    /// <summary>
    /// Opaque id: <c>pm_</c> followed by 32 hex characters, case-sensitive. The handle you charge
    /// and revoke with; persist it against your customer.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The card brand as the gateway reports it, e.g. "visa", "mastercard".</summary>
    public string? Brand { get; set; }

    /// <summary>The last four digits of the card number, for display only.</summary>
    public string? Last4 { get; set; }

    /// <summary>Expiry month, 1 to 12.</summary>
    public int? ExpiryMonth { get; set; }

    /// <summary>Expiry year, four digits, e.g. 2029.</summary>
    public int? ExpiryYear { get; set; }

    /// <summary>One of the <see cref="StoredPaymentMethodStatuses"/> values.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// One of the <see cref="StoredPaymentMethodRetiredReasons"/> values when <see cref="Status"/> is
    /// <c>retired</c>, and kept if the card is revoked afterwards; null (absent on the wire) on every
    /// other card.
    /// </summary>
    public string? RetiredReason { get; set; }

    /// <summary>
    /// True only for <c>active</c>. A status this SDK has never heard of reads as not chargeable,
    /// so a value the API adds later cannot make you charge a card you should not.
    /// </summary>
    [JsonIgnore]
    public bool IsChargeable => string.Equals(this.Status, StoredPaymentMethodStatuses.Active, StringComparison.Ordinal);
}

/// <summary>
/// The parameters for <see cref="DominaiteClient.ChargePaymentMethodAsync"/>: the same money
/// fields as a session, charged off-session against a stored card.
/// </summary>
/// <remarks>
/// Property order here is the JSON order on the wire, and the body is serialized exactly once:
/// the bytes that are hashed for the signature are the bytes that are sent.
/// </remarks>
public sealed class ChargeRequest
{
    /// <summary>The amount in MINOR units: 2500 is 25.00 EUR. Integers only.</summary>
    public long Amount { get; set; }

    /// <summary>ISO 4217 currency, e.g. "EUR".</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Your own order id for this charge, at most 100 characters.</summary>
    public string OrderReference { get; set; } = string.Empty;

    /// <summary>Free-text description stored on the transaction, at most 500 characters.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// The <c>agr_</c> id of the agreement this charge is made under. Optional. When set, the
    /// agreement must be yours and linked to this payment method, and the charge must be on its
    /// terms (currency and amount). Null is omitted from the body.
    /// </summary>
    public string? AgreementId { get; set; }

    /// <summary>
    /// The idempotency key. Required, and signed exactly like a session create: it travels in the
    /// header and in the signature, never in the body.
    /// </summary>
    /// <remarks>
    /// Derive it from the order with <see cref="IdempotencyKeys.ForOrder"/> (scope "charge"), and
    /// send the same key when you retry: a fresh key on a retry is the double-charge bug. Null or
    /// blank is rejected before anything is sent.
    /// </remarks>
    [JsonIgnore]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Extra body fields this class does not model yet. Merged into the JSON body at the top
    /// level; a key that collides with a modelled property is ignored.
    /// </summary>
    [JsonIgnore]
    public IDictionary<string, object?> Extra { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>
/// The charge status wire values, as constants plus the enumerable
/// <see cref="ChargeStatuses.All"/>.
/// </summary>
public static class ChargeStatuses
{
    /// <summary>The card was charged. The ONLY value that means paid.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>
    /// The issuer declined (HTTP 402); see <see cref="PaymentMethodCharge.DeclineClass"/>.
    /// </summary>
    public const string Failed = "failed";

    /// <summary>
    /// Not decided yet. Not terminal: poll <see cref="DominaiteClient.GetStatusAsync"/> with the
    /// charge's transaction id.
    /// </summary>
    public const string Pending = "pending";

    /// <summary>An authorization voided before capture; no money moved.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>The whole vocabulary, in the order the canonical contract lists it.</summary>
    public static IReadOnlyList<string> All { get; } = [Succeeded, Failed, Pending, Cancelled];
}

/// <summary>
/// Decline class wire values, on <see cref="PaymentMethodCharge.DeclineClass"/> and
/// <see cref="CheckoutStatus.DeclineClass"/>. Coarse buckets that tell you what to do next; the
/// provider's own reason is in <see cref="PaymentMethodCharge.DeclineCode"/>. A decline can have
/// no class yet, and the API can add classes later.
/// </summary>
public static class DeclineClasses
{
    /// <summary>Do not retry this card: stolen, closed, or the issuer said never.</summary>
    public const string Hard = "hard";

    /// <summary>Insufficient funds; retry in a few days.</summary>
    public const string SoftFunds = "soft_funds";

    /// <summary>
    /// The issuer wants the payer present. Bring them back through a checkout session; an
    /// off-session retry will fail the same way.
    /// </summary>
    public const string SoftScaRequired = "soft_sca_required";

    /// <summary>A transient decline; retry later.</summary>
    public const string SoftOther = "soft_other";

    /// <summary>The whole vocabulary, in the order the canonical contract lists it.</summary>
    public static IReadOnlyList<string> All { get; } = [Hard, SoftFunds, SoftScaRequired, SoftOther];
}

/// <summary>
/// What <see cref="DominaiteClient.ChargePaymentMethodAsync"/> returns, for a placed charge
/// (HTTP 201) and for a provider decline (HTTP 402, status <c>failed</c>) alike. A decline is a
/// result, not an exception: check <see cref="IsPaid"/>, then branch on <see cref="DeclineClass"/>
/// when it is set.
/// Also carried on <see cref="DominaiteChargeException.Charge"/> when the gateway attached the
/// charge row to its answer.
/// </summary>
public sealed class PaymentMethodCharge
{
    /// <summary>
    /// <c>ch_</c> followed by 32 hex characters. Store it against the order; it is what support
    /// asks for.
    /// </summary>
    public string ChargeId { get; set; } = string.Empty;

    /// <summary>One of the <see cref="ChargeStatuses"/> values.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// What to do with a declined charge: one of the <see cref="DeclineClasses"/> values, or a
    /// value added later. Null on every status other than <c>failed</c>, and also on many 402
    /// declines.
    /// </summary>
    /// <remarks>
    /// On a 402 the class is often still null: only the SCA decline codes classify on their own.
    /// The payment provider's decline notice sets the class a moment later. From then on
    /// <see cref="DominaiteClient.GetStatusAsync"/> with <see cref="TransactionId"/> carries it.
    /// </remarks>
    public string? DeclineClass { get; set; }

    /// <summary>
    /// The provider's raw decline code, for your logs; branch on <see cref="DeclineClass"/>
    /// instead. Null on every status other than <c>failed</c>.
    /// </summary>
    public string? DeclineCode { get; set; }

    /// <summary>
    /// Dominaite's payment id for this charge. Store it against your order; poll status with it.
    /// </summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>The <c>agr_</c> id the charge was made under, when the request named one.</summary>
    public string? AgreementId { get; set; }

    /// <summary>
    /// The charge's outcome counter in this answer: the <c>data.sequence</c> of its
    /// <c>charge.succeeded</c> or <c>charge.failed</c> event when the charge was decided at once,
    /// 0 while pending.
    /// </summary>
    /// <remarks>
    /// A replay of the same key returns the first answer unchanged, so this number can be lower
    /// than a later event's. <see cref="CheckoutStatus.Sequence"/> on the status read is the live
    /// value.
    /// </remarks>
    public long Sequence { get; set; }

    /// <summary>
    /// The unwrapped charge object as the gateway sent it, for fields this class does not model
    /// yet.
    /// </summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }

    /// <summary>True only for <c>succeeded</c>.</summary>
    [JsonIgnore]
    public bool IsPaid => string.Equals(this.Status, ChargeStatuses.Succeeded, StringComparison.Ordinal);

    /// <summary>
    /// False while the charge can still change, true once it cannot. An unrecognised status is
    /// reported as NOT terminal, so a status the API adds later keeps you polling.
    /// </summary>
    [JsonIgnore]
    public bool IsTerminal => this.Status switch
    {
        ChargeStatuses.Succeeded => true,
        ChargeStatuses.Failed => true,
        ChargeStatuses.Cancelled => true,
        _ => false,
    };
}

/// <summary>
/// The parameters for <see cref="DominaiteClient.CreateRefundAsync"/>. Leave
/// <see cref="Amount"/> null to refund everything still refundable.
/// </summary>
/// <remarks>
/// Property order here is the JSON order on the wire, and the body is serialized exactly once:
/// the bytes that are hashed for the signature are the bytes that are sent. Null members are
/// omitted, so a full refund sends no <c>amount</c> key at all.
/// </remarks>
public sealed class RefundRequest
{
    /// <summary>
    /// The amount to refund in MINOR units of the payment's currency (2500 is 25.00 EUR, 1500 is
    /// 1,500 HUF), at least 1. Null refunds everything still refundable. Partial refunds add up:
    /// the amount may not exceed what is left after earlier refunds and refunds in progress.
    /// </summary>
    public long? Amount { get; set; }

    /// <summary>Free text stored with the refund, at most 500 characters.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// The idempotency key. Required, and signed exactly like a charge: it travels in the header
    /// and in the signature, never in the body.
    /// </summary>
    /// <remarks>
    /// Derive it from YOUR refund (the return or credit-note id), for example with
    /// <see cref="IdempotencyKeys.ForOrder"/> and scope "refund", and send the same key when you
    /// retry: the same key names the same refund and never refunds twice. A refund that ended
    /// <c>failed</c> keeps its key, so a new attempt needs a new one. Null or blank is rejected
    /// before anything is sent.
    /// </remarks>
    [JsonIgnore]
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// Extra body fields this class does not model yet. Merged into the JSON body at the top
    /// level; a key that collides with a modelled property is ignored.
    /// </summary>
    [JsonIgnore]
    public IDictionary<string, object?> Extra { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}

/// <summary>
/// The refund status wire values, as constants plus the enumerable
/// <see cref="RefundStatuses.All"/>.
/// </summary>
public static class RefundStatuses
{
    /// <summary>Accepted and queued, behind another refund of the same payment or not yet picked up.</summary>
    public const string Pending = "pending";

    /// <summary>With the payment provider now.</summary>
    public const string Processing = "processing";

    /// <summary>The money was returned to the payer. Final.</summary>
    public const string Succeeded = "succeeded";

    /// <summary>
    /// The refund did not happen; read <see cref="Refund.FailureCode"/>. Final for this key: a new
    /// attempt needs a new idempotency key.
    /// </summary>
    public const string Failed = "failed";

    /// <summary>The whole vocabulary, in the order the canonical contract lists it.</summary>
    public static IReadOnlyList<string> All { get; } = [Pending, Processing, Succeeded, Failed];
}

/// <summary>
/// What <see cref="DominaiteClient.CreateRefundAsync"/> and
/// <see cref="DominaiteClient.GetRefundAsync"/> return. The create call answers once the refund
/// is queued, not done: read it back with GetRefundAsync, or wait for the
/// <c>payment.refunded</c> webhook. A failed refund sends no webhook, so poll if you need to know
/// about failures.
/// </summary>
/// <remarks>
/// The gateway omits null fields on the wire; the SDK reads absent as null.
/// </remarks>
public sealed class Refund
{
    /// <summary>
    /// <c>re_</c> followed by 32 hex characters. The same key on the same payment always names the
    /// same refund.
    /// </summary>
    public string RefundId { get; set; } = string.Empty;

    /// <summary>The payment being refunded.</summary>
    public string TransactionId { get; set; } = string.Empty;

    /// <summary>One of the <see cref="RefundStatuses"/> values.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// MINOR units. On <c>pending</c>, the amount requested (null for a full refund); on
    /// <c>processing</c>, the amount being refunded (null until a full refund has been sized);
    /// on <c>succeeded</c>, the amount actually refunded; always null on <c>failed</c>.
    /// </summary>
    public long? Amount { get; set; }

    /// <summary>ISO 4217 code of the payment.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// On <c>failed</c> only: one of the <see cref="RefundFailureCodes"/> values. Treat a value
    /// not listed there as <c>REFUND_FAILED</c>.
    /// </summary>
    public string? FailureCode { get; set; }

    /// <summary>On <c>failed</c> only: a fixed English explanation of <see cref="FailureCode"/>.</summary>
    public string? FailureMessage { get; set; }

    /// <summary>When the refund reached <c>succeeded</c> or <c>failed</c> (UTC); null before that.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The unwrapped refund object as the gateway sent it, for fields this class does not model
    /// yet.
    /// </summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }

    /// <summary>True only for <c>succeeded</c>: the money went back to the payer.</summary>
    [JsonIgnore]
    public bool IsSucceeded => string.Equals(this.Status, RefundStatuses.Succeeded, StringComparison.Ordinal);

    /// <summary>
    /// False while the refund can still change, true once it cannot. An unrecognised status is
    /// reported as NOT terminal, so a status the API adds later keeps you polling.
    /// </summary>
    [JsonIgnore]
    public bool IsTerminal => this.Status switch
    {
        RefundStatuses.Succeeded => true,
        RefundStatuses.Failed => true,
        _ => false,
    };
}

/// <summary>
/// What <see cref="DominaiteClient.PingAsync"/> returns: proof that your key, secret, signing and
/// clock are all good, without creating anything.
/// </summary>
public sealed class PingResponse
{
    /// <summary>Always true on a 200.</summary>
    public bool Pong { get; set; }

    /// <summary>The merchant id your key authenticated as.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Server time (UTC).</summary>
    public DateTimeOffset? ServerTime { get; set; }

    /// <summary>Server time in unix seconds.</summary>
    public long? ServerUnixTime { get; set; }

    /// <summary>
    /// Server time minus your <c>X-Timestamp</c>. If its absolute value creeps toward 300, fix
    /// NTP now - requests start failing at 300.
    /// </summary>
    public long ClockSkewSeconds { get; set; }

    /// <summary>The unparsed payload, for fields this class does not model yet.</summary>
    [JsonIgnore]
    public JsonElement Raw { get; set; }
}
