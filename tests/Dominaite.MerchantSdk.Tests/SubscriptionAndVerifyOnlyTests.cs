using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The session fields for a verify-only card check and for an agreement's first payment: what is
/// sent and signed, and what the session and the status read give back.
/// </summary>
public class SubscriptionAndVerifyOnlyTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string TransactionId = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string AgreementId = "agr_0123456789abcdef0123456789abcdef";
    private const string VerifyKey = "verify-card-check-42";
    private const string SubscriptionKey = "subscription-sub-42-1";

    private const string VerifyOnlyPayload = """
        {"success":true,"checkout":{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","cashierKey":"ck_live_2f3a4d5e6f708192","cashierToken":"ctok_5e4f3a2b1c0d9e8f","amount":100,"currency":"EUR","verifyOnly":true}}
        """;

    private const string SubscriptionPayload = """
        {"success":true,"checkout":{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","cashierKey":"ck_live_2f3a4d5e6f708192","cashierToken":"ctok_5e4f3a2b1c0d9e8f","amount":2500,"currency":"EUR","agreementId":"agr_0123456789abcdef0123456789abcdef"}}
        """;

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

    private static void AssertSignatureMatches(RecordedRequest sent, string canonicalPath, string idempotencyKey)
    {
        var expected = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = sent.Header("X-Timestamp")!,
            Method = sent.Method,
            Path = canonicalPath,
            IdempotencyKey = idempotencyKey,
            Body = sent.Body,
        });

        Assert.Equal(expected, sent.Header("X-Signature"));
    }

    [Fact]
    public async Task AVerifyOnlySessionSendsAmountZeroAndSignsTheBytesItSends()
    {
        using var server = new MockServer(Reply.Enveloped(VerifyOnlyPayload));
        using var client = ClientFor(server);

        var session = await client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
        {
            Currency = "EUR",
            OrderReference = "card-check-42",
            VerifyOnly = true,
            IdempotencyKey = VerifyKey,
        });

        var sent = server.LastRequest;
        Assert.Equal("""{"amount":0,"currency":"EUR","orderReference":"card-check-42","verifyOnly":true}""", sent.Body);
        Assert.Equal(VerifyKey, sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, DominaiteClient.SessionsPath, VerifyKey);

        Assert.True(session.VerifyOnly);
        Assert.Equal(100, session.Amount);
        Assert.Null(session.AgreementId);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(false, 0)]
    [InlineData(null, -100)]
    [InlineData(false, -100)]
    public async Task EveryOtherSessionStillRefusesAnAmountBelowOneBeforeSending(bool? verifyOnly, long amount)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
            {
                Amount = amount,
                Currency = "EUR",
                OrderReference = "order-1042",
                VerifyOnly = verifyOnly,
                IdempotencyKey = "checkout-order-1042",
            }));

        Assert.Contains("Amount", error.Message, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    /// <summary>The gateway refuses a verify-only amount other than 0, so the SDK refuses it first.</summary>
    [Theory]
    [InlineData(100)]
    [InlineData(-100)]
    public async Task AVerifyOnlySessionRefusesAnyAmountButZeroBeforeSending(long amount)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteValidationException>(
            () => client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
            {
                Amount = amount,
                Currency = "EUR",
                OrderReference = "card-check-42",
                VerifyOnly = true,
                IdempotencyKey = VerifyKey,
            }));

        Assert.Contains("VerifyOnly", error.Message, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task VerifyOnlyNullOrFalseIsLeftOutOfTheBody()
    {
        using var server = new MockServer(Reply.Enveloped(VerifyOnlyPayload), Reply.Enveloped(VerifyOnlyPayload));
        using var client = ClientFor(server);

        await client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1042",
            IdempotencyKey = "checkout-order-1042",
        });
        await client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1042",
            VerifyOnly = false,
            IdempotencyKey = "checkout-order-1042",
        });

        Assert.Equal("""{"amount":2500,"currency":"EUR","orderReference":"order-1042"}""", server.Requests[0].Body);
        Assert.Equal("""{"amount":2500,"currency":"EUR","orderReference":"order-1042","verifyOnly":false}""", server.Requests[1].Body);
    }

    /// <summary>
    /// The agreement's first payment: every new field in the gateway's order, and the consent time
    /// written in UTC with a Z whatever offset the caller's value has.
    /// </summary>
    [Fact]
    public async Task ASubscriptionSessionSendsTheStorefrontAgreementAndConsentInWireOrder()
    {
        using var server = new MockServer(Reply.Enveloped(SubscriptionPayload));
        using var client = ClientFor(server);

        var session = await client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "sub-42-1",
            Description = "Monthly plan",
            LocationId = Guid.Parse("5e0b7c1a-2f3d-4e5f-8a9b-0c1d2e3f4a5b"),
            SaveCard = true,
            AgreementId = AgreementId,
            Consent = new AgreementConsent
            {
                TextVersion = "recurring-2026-09",
                AcceptedAt = new DateTimeOffset(2026, 10, 1, 11, 30, 0, TimeSpan.FromHours(2)),
                Ip = "203.0.113.7",
            },
            Integration = "widget",
            IdempotencyKey = SubscriptionKey,
        });

        var sent = server.LastRequest;
        Assert.Equal(
            """{"amount":2500,"currency":"EUR","orderReference":"sub-42-1","description":"Monthly plan","locationId":"5e0b7c1a-2f3d-4e5f-8a9b-0c1d2e3f4a5b","saveCard":true,"agreementId":"agr_0123456789abcdef0123456789abcdef","consent":{"textVersion":"recurring-2026-09","acceptedAt":"2026-10-01T09:30:00Z","ip":"203.0.113.7"},"integration":"widget"}""",
            sent.Body);
        AssertSignatureMatches(sent, DominaiteClient.SessionsPath, SubscriptionKey);

        Assert.Equal(AgreementId, session.AgreementId);
        Assert.Null(session.VerifyOnly);
    }

    [Fact]
    public async Task AStatusReadOfAFailedFirstPaymentShowsTheClassAndThePendingAgreement()
    {
        using var server = new MockServer(Reply.Enveloped(
            """{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","orderId":"dom_9a8b7c6d5e4f","status":"failed","amount":2500,"currency":"EUR","declineClass":"hard","isRecurring":false,"agreement":{"id":"agr_0123456789abcdef0123456789abcdef","status":"pending","nextChargeAt":null},"sequence":0}"""));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        Assert.Equal(TransactionStatuses.Failed, status.Status);
        Assert.Equal(DeclineClasses.Hard, status.DeclineClass);
        Assert.False(status.IsRecurring);
        Assert.Null(status.VerifyOnly);
        Assert.NotNull(status.Agreement);
        Assert.Equal(AgreementId, status.Agreement!.Id);
        Assert.Equal(AgreementStatuses.Pending, status.Agreement.Status);
        Assert.Null(status.Agreement.NextChargeAt);
        Assert.Equal(0, status.Sequence);
    }

    [Fact]
    public async Task AStatusReadOfARecurringChargeShowsTheAgreementAndTheSequence()
    {
        using var server = new MockServer(Reply.Enveloped(
            """{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","status":"succeeded","amount":2500,"currency":"EUR","declineClass":null,"isRecurring":true,"agreement":{"id":"agr_0123456789abcdef0123456789abcdef","status":"active","nextChargeAt":"2026-11-01T00:00:00Z"},"sequence":7}"""));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        Assert.True(status.IsPaid);
        Assert.Null(status.DeclineClass);
        Assert.True(status.IsRecurring);
        Assert.Equal(AgreementStatuses.Active, status.Agreement!.Status);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), status.Agreement.NextChargeAt);
        Assert.Equal(7, status.Sequence);
    }

    /// <summary>
    /// A verify-only check keeps the plain transaction status. The saved card is what says the card
    /// is verified.
    /// </summary>
    [Fact]
    public async Task AStatusReadOfAVerifyOnlyCheckShowsTheFlagAndTheSavedCard()
    {
        using var server = new MockServer(Reply.Enveloped(
            """{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","status":"cancelled","amount":100,"currency":"EUR","isRecurring":false,"verifyOnly":true,"agreement":null,"sequence":0,"storedPaymentMethod":{"id":"pm_0123456789abcdef0123456789abcdef","brand":"visa","last4":"4242","expiryMonth":12,"expiryYear":2030,"status":"active","retiredReason":null}}"""));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        Assert.True(status.VerifyOnly);
        Assert.Equal(TransactionStatuses.Cancelled, status.Status);
        Assert.False(status.IsPaid);
        Assert.True(status.StoredPaymentMethod!.IsChargeable);
        Assert.Null(status.Agreement);
    }

    [Fact]
    public async Task AStatusFromAServerWithoutTheNewFieldsReadsThemAsEmpty()
    {
        using var server = new MockServer(Reply.Enveloped(
            """{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","status":"succeeded","amount":2500,"currency":"EUR"}"""));
        using var client = ClientFor(server);

        var status = await client.GetStatusAsync(TransactionId);

        Assert.Null(status.DeclineClass);
        Assert.False(status.IsRecurring);
        Assert.Null(status.VerifyOnly);
        Assert.Null(status.Agreement);
        Assert.Equal(0, status.Sequence);
    }
}
