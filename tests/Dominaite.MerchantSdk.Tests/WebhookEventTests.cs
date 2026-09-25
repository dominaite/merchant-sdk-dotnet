using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// <see cref="Webhooks.VerifyAndParse(string, string, string, int, long?)"/>: the envelope
/// (<c>apiVersion</c>, <c>createdAt</c>), the typed <c>agreement.*</c> and <c>charge.*</c> data
/// with their <c>sequence</c>, and payloads from servers that predate those fields.
/// </summary>
public class WebhookEventTests
{
    private const string Secret = "whsec_abababababababababababababababababababababababababababababababab";

    private const long Timestamp = 1758800000;

    // The canonical cross-SDK vector from WebhookTests, byte-identical. It predates apiVersion.
    private const string CanonicalBody = """{"id":"7f9c24e5-1d1f-4c0a-9b6c-2f3a4d5e6f70","type":"payment.succeeded","createdAt":"2026-08-20T14:00:00Z","data":{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","status":"succeeded","previousStatus":"pending","kind":"sale","amount":8440,"grossAmount":8701,"surchargeAmount":261,"currency":"EUR","originalTransactionId":null,"idempotencyKey":"order-123"}}""";

    private const string CanonicalHeader = "t=1755700000,v1=5305bcf1302fdaba8f8c19a20c899e916fb4d2a7d8d547c62529ff87c4697b72";

    private const string PaymentBody = """{"id":"7f9c24e5-1d1f-4c0a-9b6c-2f3a4d5e6f70","type":"payment.succeeded","apiVersion":"2026-09-25","createdAt":"2026-09-25T10:00:00Z","data":{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","status":"succeeded","amount":8440,"currency":"EUR"}}""";

    private const string AgreementBody = """{"id":"a1b2c3d4-0000-4000-8000-000000000001","type":"agreement.past_due","apiVersion":"2026-09-25","createdAt":"2026-09-25T10:00:00.1234567Z","data":{"id":"agr_0123456789abcdef0123456789abcdef","planId":"plan_0123456789abcdef0123456789abcdef","customerReference":"cust-42","storedPaymentMethodId":"pm_0123456789abcdef0123456789abcdef","status":"past_due","previousStatus":"active","amount":2500,"currency":"EUR","intervalUnit":"month","intervalCount":1,"periodCount":null,"trialDays":0,"nextChargeAt":"2026-10-01T00:00:00Z","activatedAt":"2026-09-01T00:00:00Z","cancelledAt":null,"version":1,"sequence":3}}""";

    private const string PlatformChargeBody = """{"id":"a1b2c3d4-0000-4000-8000-000000000002","type":"charge.retrying","apiVersion":"2026-09-25","createdAt":"2026-09-25T10:00:00Z","data":{"chargeId":"ch_33333333333343338333333333333333","transactionId":"33333333-3333-4333-8333-333333333333","storedPaymentMethodId":"pm_0123456789abcdef0123456789abcdef","agreementId":"agr_0123456789abcdef0123456789abcdef","customerReference":"cust-42","outcome":"retrying","periodNumber":2,"attemptNumber":1,"amount":2500,"currency":"EUR","paymentMethod":{"brand":"visa","last4":"4242"},"orderReference":"sub-42-2","description":null,"declineClass":"soft_funds","declineCode":"51","nextAttemptAt":"2026-09-26T10:00:00Z","nextChargeAt":null,"sequence":5}}""";

    private const string OneOffChargeBody = """{"id":"a1b2c3d4-0000-4000-8000-000000000003","type":"charge.succeeded","apiVersion":"2026-09-25","createdAt":"2026-09-25T10:00:00Z","data":{"chargeId":"ch_44444444444444448444444444444444","transactionId":"44444444-4444-4444-8444-444444444444","storedPaymentMethodId":"pm_0123456789abcdef0123456789abcdef","agreementId":null,"customerReference":null,"outcome":"succeeded","periodNumber":null,"attemptNumber":null,"amount":990,"currency":"EUR","paymentMethod":{"brand":"visa","last4":"4242"},"orderReference":"order-1043","description":null,"declineClass":null,"declineCode":null,"nextAttemptAt":null,"nextChargeAt":null,"sequence":1}}""";

    // Shaped as a server before apiVersion and sequence: same events, neither field.
    private const string OldAgreementBody = """{"id":"a1b2c3d4-0000-4000-8000-000000000004","type":"agreement.activated","createdAt":"2026-09-20T10:00:00Z","data":{"id":"agr_0123456789abcdef0123456789abcdef","planId":"plan_0123456789abcdef0123456789abcdef","customerReference":"cust-42","storedPaymentMethodId":null,"status":"active","previousStatus":"pending","amount":2500,"currency":"EUR","intervalUnit":"month","intervalCount":1,"periodCount":12,"trialDays":0,"nextChargeAt":"2026-10-20T10:00:00Z","activatedAt":"2026-09-20T10:00:00Z","cancelledAt":null,"version":1}}""";

    private const string OldChargeBody = """{"id":"a1b2c3d4-0000-4000-8000-000000000005","type":"charge.failed","createdAt":"2026-09-20T10:00:00Z","data":{"chargeId":"ch_44444444444444448444444444444444","transactionId":"44444444-4444-4444-8444-444444444444","storedPaymentMethodId":"pm_0123456789abcdef0123456789abcdef","agreementId":null,"customerReference":null,"outcome":"failed","periodNumber":null,"attemptNumber":null,"amount":990,"currency":"EUR","paymentMethod":{"brand":"visa","last4":"4242"},"orderReference":"order-1043","description":null,"declineClass":"hard","declineCode":"05","nextAttemptAt":null,"nextChargeAt":null}}""";

    private static string Sign(string body, long timestamp = Timestamp)
    {
        var mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret),
            Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + body));
        return $"t={timestamp},v1={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    private static WebhookEvent Parse(string body)
        => Webhooks.VerifyAndParse(body, Sign(body), Secret, Webhooks.DefaultToleranceSeconds, Timestamp);

    [Fact]
    public void TheEnvelopeCarriesApiVersionAndCreatedAt()
    {
        var evt = Parse(PaymentBody);

        Assert.Equal("7f9c24e5-1d1f-4c0a-9b6c-2f3a4d5e6f70", evt.Id);
        Assert.Equal(WebhookEventTypes.PaymentSucceeded, evt.Type);
        Assert.Equal("2026-09-25", evt.ApiVersion);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero), evt.CreatedAt);
        Assert.Equal(8440, evt.Data.GetProperty("amount").GetInt64());
        Assert.Null(evt.Agreement);
        Assert.Null(evt.Charge);
    }

    [Fact]
    public void TheCanonicalVectorParses_WithoutApiVersion()
    {
        var evt = Webhooks.VerifyAndParse(
            CanonicalBody, CanonicalHeader, Secret, Webhooks.DefaultToleranceSeconds, 1755700000 + 10);

        Assert.Equal(WebhookEventTypes.PaymentSucceeded, evt.Type);
        Assert.Null(evt.ApiVersion);
        Assert.Equal(new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.Zero), evt.CreatedAt);
        Assert.Equal("order-123", evt.Data.GetProperty("idempotencyKey").GetString());
    }

    [Fact]
    public void AnAgreementEventCarriesSequenceAndCreatedAt()
    {
        var evt = Parse(AgreementBody);

        Assert.Null(evt.Charge);
        var agreement = Assert.IsType<AgreementEventData>(evt.Agreement);
        Assert.Equal(3, agreement.Sequence);
        Assert.Equal("agr_0123456789abcdef0123456789abcdef", agreement.OrderingKey);
        Assert.Equal("past_due", agreement.Status);
        Assert.Equal("active", agreement.PreviousStatus);
        Assert.Equal(2500, agreement.Amount);
        Assert.Null(agreement.PeriodCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), agreement.NextChargeAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T10:00:00.1234567Z", CultureInfo.InvariantCulture), evt.CreatedAt);
        Assert.Equal("2026-09-25", evt.ApiVersion);
    }

    [Fact]
    public void APlatformChargeEventIsOrderedPerAgreementPeriod()
    {
        var evt = Parse(PlatformChargeBody);

        Assert.Null(evt.Agreement);
        var charge = Assert.IsType<ChargeEventData>(evt.Charge);
        Assert.Equal(5, charge.Sequence);
        Assert.Equal("agr_0123456789abcdef0123456789abcdef/2", charge.OrderingKey);
        Assert.Equal("retrying", charge.Outcome);
        Assert.Equal(DeclineClasses.SoftFunds, charge.DeclineClass);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 10, 0, 0, TimeSpan.Zero), charge.NextAttemptAt);
        Assert.Equal("4242", charge.Raw.GetProperty("paymentMethod").GetProperty("last4").GetString());
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 10, 0, 0, TimeSpan.Zero), evt.CreatedAt);
    }

    [Fact]
    public void AOneOffChargeEventIsOrderedPerChargeId()
    {
        var charge = Parse(OneOffChargeBody).Charge!;

        Assert.Equal(1, charge.Sequence);
        Assert.Null(charge.AgreementId);
        Assert.Equal("ch_44444444444444448444444444444444", charge.OrderingKey);
    }

    [Fact]
    public void ASequenceOfZeroIsKeptAsZero()
    {
        var body = AgreementBody.Replace("\"sequence\":3", "\"sequence\":0", StringComparison.Ordinal);
        Assert.NotEqual(AgreementBody, body);

        Assert.Equal(0, Parse(body).Agreement!.Sequence);
    }

    [Fact]
    public void AnOldAgreementPayloadWithoutTheNewFieldsStillParses()
    {
        var evt = Parse(OldAgreementBody);

        Assert.Null(evt.ApiVersion);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero), evt.CreatedAt);
        var agreement = evt.Agreement!;
        Assert.Null(agreement.Sequence);
        Assert.Equal("active", agreement.Status);
        Assert.Equal(12, agreement.PeriodCount);
    }

    [Fact]
    public void AnOldChargePayloadWithoutTheNewFieldsStillParses()
    {
        var evt = Parse(OldChargeBody);

        Assert.Null(evt.ApiVersion);
        var charge = evt.Charge!;
        Assert.Null(charge.Sequence);
        Assert.Equal(DeclineClasses.Hard, charge.DeclineClass);
        Assert.Equal("ch_44444444444444448444444444444444", charge.OrderingKey);
    }

    [Fact]
    public void AnUnknownFieldIsIgnored()
    {
        var body = PaymentBody.Replace("\"apiVersion\"", "\"futureField\":{\"x\":1},\"apiVersion\"", StringComparison.Ordinal);

        Assert.Equal("2026-09-25", Parse(body).ApiVersion);
    }

    /// <summary>The signature is checked before a single byte is parsed.</summary>
    [Fact]
    public void ABadSignatureFailsBeforeTheBodyIsRead()
    {
        const string NotJson = "not json at all";
        var header = Sign("something else");

        var error = Assert.Throws<DominaiteWebhookException>(
            () => Webhooks.VerifyAndParse(NotJson, header, Secret, Webhooks.DefaultToleranceSeconds, Timestamp));

        Assert.Equal(WebhookFailureReason.SignatureMismatch, error.Reason);
    }

    [Fact]
    public void AStaleDeliveryFailsBeforeTheBodyIsRead()
    {
        var error = Assert.Throws<DominaiteWebhookException>(() => Webhooks.VerifyAndParse(
            PaymentBody, Sign(PaymentBody), Secret, Webhooks.DefaultToleranceSeconds, Timestamp + 301));

        Assert.Equal(WebhookFailureReason.TimestampOutOfTolerance, error.Reason);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"type":"payment.succeeded","data":{}}""")]
    [InlineData("""{"id":"x","type":"payment.succeeded","createdAt":"yesterday","data":{}}""")]
    [InlineData("""{"id":"x","type":"charge.succeeded","data":{"chargeId":"ch_1","sequence":"three"}}""")]
    public void ASignedBodyThatIsNotAnEnvelopeIsMalformedPayload(string body)
    {
        var error = Assert.Throws<DominaiteWebhookException>(() => Parse(body));

        Assert.Equal(WebhookFailureReason.MalformedPayload, error.Reason);
    }

    [Fact]
    public void TheByteOverloadReadsTheSameEvent()
    {
        var evt = Webhooks.VerifyAndParse(
            Encoding.UTF8.GetBytes(AgreementBody), Sign(AgreementBody), Secret, Webhooks.DefaultToleranceSeconds, Timestamp);

        Assert.Equal(3, evt.Agreement!.Sequence);
    }
}
