using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The API adds statuses, decline classes and event types over time. A value this SDK has never
/// seen is kept exactly as it came, and it never throws.
/// </summary>
public class UnknownValuesTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string WebhookSecret = "whsec_abababababababababababababababababababababababababababababababab";
    private const long Timestamp = 1758800000;
    private const string TransactionId = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string PaymentMethodId = "pm_0123456789abcdef0123456789abcdef";

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

    private static WebhookEvent ParseWebhook(string body)
    {
        var mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(WebhookSecret),
            Encoding.UTF8.GetBytes(Timestamp.ToString(CultureInfo.InvariantCulture) + "." + body));
        var header = $"t={Timestamp},v1={Convert.ToHexString(mac).ToLowerInvariant()}";
        return Webhooks.VerifyAndParse(body, header, WebhookSecret, Webhooks.DefaultToleranceSeconds, Timestamp);
    }

    [Fact]
    public async Task AStatusReadWithNeverSeenValuesKeepsThem()
    {
        using var server = new MockServer(Reply.Enveloped(
            """{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","status":"on_hold","amount":2500,"currency":"EUR","paymentMethod":"carrier_billing","walletType":"future_pay","declineClass":"soft_velocity","isRecurring":true,"agreement":{"id":"agr_0123456789abcdef0123456789abcdef","status":"paused","nextChargeAt":null},"sequence":3,"storedPaymentMethod":{"id":"pm_0123456789abcdef0123456789abcdef","brand":"visa","last4":"4242","expiryMonth":12,"expiryYear":2030,"status":"suspended","retiredReason":"issuer_recall"}}"""));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        Assert.Equal("on_hold", status.Status);
        Assert.False(status.IsPaid);
        Assert.False(status.IsTerminal);
        Assert.Equal("carrier_billing", status.PaymentMethod);
        Assert.Equal("future_pay", status.WalletType);
        Assert.Equal("soft_velocity", status.DeclineClass);
        Assert.Equal("paused", status.Agreement!.Status);
        Assert.Equal("suspended", status.StoredPaymentMethod!.Status);
        Assert.False(status.StoredPaymentMethod.IsChargeable);
        Assert.Equal("issuer_recall", status.StoredPaymentMethod.RetiredReason);
    }

    [Fact]
    public async Task AChargeWithNeverSeenValuesKeepsThem()
    {
        using var server = new MockServer(Reply.Raw(
            201,
            """{"success":true,"data":{"chargeId":"ch_33333333333343338333333333333333","status":"under_review","declineClass":"soft_velocity","declineCode":"N7","transactionId":"33333333-3333-4333-8333-333333333333","sequence":1}}"""));
        using var client = ClientFor(server);

        var charge = await client.ChargePaymentMethodAsync(PaymentMethodId, new ChargeRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1043",
            IdempotencyKey = "charge-order-1043",
        });

        Assert.Equal("under_review", charge.Status);
        Assert.False(charge.IsPaid);
        Assert.False(charge.IsTerminal);
        Assert.Equal("soft_velocity", charge.DeclineClass);
    }

    [Fact]
    public void AWebhookOfANeverSeenTypeKeepsItsTypeAndData()
    {
        var evt = ParseWebhook(
            """{"id":"a1b2c3d4-0000-4000-8000-000000000009","type":"payout.created","apiVersion":"2027-01-01","createdAt":"2027-01-02T10:00:00Z","data":{"payoutId":"po_1","amount":100}}""");

        Assert.Equal("payout.created", evt.Type);
        Assert.Equal("2027-01-01", evt.ApiVersion);
        Assert.Null(evt.Payment);
        Assert.Null(evt.Agreement);
        Assert.Null(evt.Charge);
        Assert.Equal("po_1", evt.Data.GetProperty("payoutId").GetString());
    }

    [Fact]
    public void KnownEventFamiliesWithNeverSeenValuesKeepThem()
    {
        var payment = ParseWebhook(
            """{"id":"a1b2c3d4-0000-4000-8000-00000000000a","type":"payment.on_hold","createdAt":"2027-01-02T10:00:00Z","data":{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","status":"on_hold","amount":2500,"currency":"EUR","walletType":"future_pay"}}""");
        var agreement = ParseWebhook(
            """{"id":"a1b2c3d4-0000-4000-8000-00000000000b","type":"agreement.paused","createdAt":"2027-01-02T10:00:00Z","data":{"id":"agr_0123456789abcdef0123456789abcdef","status":"paused","previousStatus":"active","amount":2500,"currency":"EUR","intervalUnit":"fortnight","sequence":9}}""");
        var charge = ParseWebhook(
            """{"id":"a1b2c3d4-0000-4000-8000-00000000000c","type":"charge.deferred","createdAt":"2027-01-02T10:00:00Z","data":{"chargeId":"ch_44444444444444448444444444444444","outcome":"deferred","amount":990,"currency":"EUR","declineClass":"soft_velocity","sequence":2}}""");

        Assert.Equal("on_hold", payment.Payment!.Status);
        Assert.Equal("future_pay", payment.Payment.WalletType);
        Assert.Equal("paused", agreement.Agreement!.Status);
        Assert.Equal("fortnight", agreement.Agreement.IntervalUnit);
        Assert.Equal("deferred", charge.Charge!.Outcome);
        Assert.Equal("soft_velocity", charge.Charge.DeclineClass);
    }
}
