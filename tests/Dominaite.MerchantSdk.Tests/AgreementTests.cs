using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The agreement routes against the loopback server: what each call sends and signs, and what it
/// reads back.
/// </summary>
public class AgreementTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string AgreementId = "agr_0123456789abcdef0123456789abcdef";
    private const string PlanId = "plan_0123456789abcdef0123456789abcdef";
    private const string PaymentMethodId = "pm_0123456789abcdef0123456789abcdef";
    private const string AgreementKey = "subscription-sub-42";

    private const string PendingAgreement = """
        {"id":"agr_0123456789abcdef0123456789abcdef","planId":"plan_0123456789abcdef0123456789abcdef","customerReference":"cust-42","storedPaymentMethodId":"pm_0123456789abcdef0123456789abcdef","status":"pending","amount":2500,"maxAmountPerPeriod":5000,"currency":"EUR","intervalUnit":"month","intervalCount":1,"trialDays":14,"consent":{"textVersion":"terms-2026-09","acceptedAt":"2026-10-01T09:30:00Z","ip":"203.0.113.7","receivedAt":"2026-10-01T09:30:02Z"},"version":1,"sequence":0,"createdAt":"2026-10-01T09:30:02Z"}
        """;

    private const string PastDueAgreement = """
        {"id":"agr_0123456789abcdef0123456789abcdef","planId":"plan_0123456789abcdef0123456789abcdef","customerReference":"cust-42","storedPaymentMethodId":"pm_0123456789abcdef0123456789abcdef","status":"past_due","amount":2500,"maxAmountPerPeriod":null,"currency":"EUR","intervalUnit":"month","intervalCount":1,"periodCount":12,"trialDays":0,"nextChargeAt":null,"pastDueReason":"sca_required","pastDueAt":"2026-11-01T06:00:00Z","consent":null,"version":2,"sequence":5,"createdAt":"2026-09-01T09:30:02Z","activatedAt":"2026-09-01T09:31:00Z","cancelledAt":null}
        """;

    private static string AgreementPath => $"{DominaiteClient.AgreementsPath}/{AgreementId}";

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

    private static Reply Data(int status, string data) => Reply.Raw(status, $$"""{"success":true,"data":{{data}}}""");

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

    /// <summary>
    /// Every field in the gateway's order, the consent time in UTC with a Z whatever offset the
    /// caller's value has, and extra fields after the modelled ones.
    /// </summary>
    [Fact]
    public async Task CreateSendsTheAgreementInWireOrderAndSignsItsKey()
    {
        using var server = new MockServer(Data(201, PendingAgreement));
        using var client = ClientFor(server);

        var request = new CreateAgreementRequest
        {
            PlanId = PlanId,
            CustomerReference = "cust-42",
            StoredPaymentMethodId = PaymentMethodId,
            Consent = new AgreementConsent
            {
                TextVersion = "terms-2026-09",
                AcceptedAt = new DateTimeOffset(2026, 10, 1, 11, 30, 0, TimeSpan.FromHours(2)),
                Ip = "203.0.113.7",
            },
            MaxAmountPerPeriod = 5000,
            IdempotencyKey = AgreementKey,
        };
        request.Extra["futureField"] = true;

        var agreement = await client.CreateAgreementAsync(request);

        var sent = server.LastRequest;
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/api" + DominaiteClient.AgreementsPath, sent.Path);
        Assert.Equal(
            """{"planId":"plan_0123456789abcdef0123456789abcdef","customerReference":"cust-42","storedPaymentMethodId":"pm_0123456789abcdef0123456789abcdef","consent":{"textVersion":"terms-2026-09","acceptedAt":"2026-10-01T09:30:00Z","ip":"203.0.113.7"},"maxAmountPerPeriod":5000,"futureField":true}""",
            sent.Body);
        Assert.Equal(AgreementKey, sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, DominaiteClient.AgreementsPath, AgreementKey);

        Assert.Equal(AgreementId, agreement.Id);
        Assert.Equal(AgreementStatuses.Pending, agreement.Status);
        Assert.Equal(5000, agreement.MaxAmountPerPeriod);
        Assert.Null(agreement.NextChargeAt);
        Assert.Null(agreement.PastDueReason);
        Assert.Equal("terms-2026-09", agreement.Consent!.TextVersion);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 9, 30, 2, TimeSpan.Zero), agreement.Consent.ReceivedAt);
        Assert.Null(agreement.Consent.ReceivedFromIp);
        Assert.Equal(1, agreement.Version);
        Assert.Equal(0, agreement.Sequence);
        Assert.Equal("cust-42", agreement.Raw.GetProperty("customerReference").GetString());
    }

    [Fact]
    public async Task ASessionLedAgreementSendsOnlyThePlanAndTheCustomer()
    {
        using var server = new MockServer(Data(200, PendingAgreement));
        using var client = ClientFor(server);

        await client.CreateAgreementAsync(new CreateAgreementRequest
        {
            PlanId = PlanId,
            CustomerReference = "cust-42",
            IdempotencyKey = AgreementKey,
        });

        Assert.Equal(
            """{"planId":"plan_0123456789abcdef0123456789abcdef","customerReference":"cust-42"}""",
            server.LastRequest.Body);
    }

    [Fact]
    public async Task CreateRefusesAMissingKeyBeforeSending()
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.CreateAgreementAsync(new CreateAgreementRequest
        {
            PlanId = PlanId,
            CustomerReference = "cust-42",
        }));

        Assert.Empty(server.Requests);
    }

    [Theory]
    [InlineData(409, "PLAN_NOT_ACTIVE")]
    [InlineData(422, "AGREEMENT_CONSENT_REQUIRED")]
    [InlineData(404, "PAYMENT_METHOD_NOT_FOUND")]
    public async Task ARefusedAgreementIsAnApiErrorThatCarriesTheKey(int status, string code)
    {
        using var server = new MockServer(Reply.ErrorEnvelope(status, code, "Refused."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(() => client.CreateAgreementAsync(new CreateAgreementRequest
        {
            PlanId = PlanId,
            CustomerReference = "cust-42",
            IdempotencyKey = AgreementKey,
        }));

        Assert.Equal(code, error.Code);
        Assert.Equal(status, error.HttpStatus);
        Assert.Equal(AgreementKey, error.IdempotencyKey);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task ReadSignsAGetAndShowsWhyTheAgreementIsPastDue()
    {
        using var server = new MockServer(Data(200, PastDueAgreement));
        using var client = ClientFor(server);

        var agreement = await client.GetAgreementAsync(AgreementId);

        var sent = server.LastRequest;
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api" + AgreementPath, sent.Path);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, AgreementPath, string.Empty);

        Assert.Equal(AgreementStatuses.PastDue, agreement.Status);
        Assert.Equal(PastDueReasons.ScaRequired, agreement.PastDueReason);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 6, 0, 0, TimeSpan.Zero), agreement.PastDueAt);
        Assert.Null(agreement.MaxAmountPerPeriod);
        Assert.Null(agreement.Consent);
        Assert.Equal(12, agreement.PeriodCount);
        Assert.Equal(2, agreement.Version);
        Assert.Equal(5, agreement.Sequence);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 9, 31, 0, TimeSpan.Zero), agreement.ActivatedAt);
        Assert.Null(agreement.CancelledAt);
    }

    [Fact]
    public async Task AnUnknownAgreementIsAnApiErrorWithItsCode()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(404, "AGREEMENT_NOT_FOUND", "No agreement with this id."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(() => client.GetAgreementAsync(AgreementId));

        Assert.Equal("AGREEMENT_NOT_FOUND", error.Code);
        Assert.Equal(404, error.HttpStatus);
    }

    [Fact]
    public async Task CancelSendsASignedDeleteAndResolvesOnNoContentEveryTime()
    {
        using var server = new MockServer(Reply.Raw(204, string.Empty), Reply.Raw(204, string.Empty));
        using var client = ClientFor(server);

        await client.CancelAgreementAsync(AgreementId);
        await client.CancelAgreementAsync(AgreementId);

        Assert.Equal(2, server.Requests.Count);
        var sent = server.LastRequest;
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal("/api" + AgreementPath, sent.Path);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, AgreementPath, string.Empty);
    }

    [Fact]
    public async Task CancellingAnEndedAgreementIsAConflict()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(409, "AGREEMENT_ENDED", "This agreement has already ended and cannot be cancelled."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(() => client.CancelAgreementAsync(AgreementId));

        Assert.Equal("AGREEMENT_ENDED", error.Code);
        Assert.Equal(409, error.HttpStatus);
    }

    [Theory]
    [InlineData("agr_1/../../plans")]
    [InlineData("agr 1")]
    [InlineData("")]
    public async Task AnIdThatIsNotOnePathSegmentIsRefusedBeforeSending(string agreementId)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.GetAgreementAsync(agreementId));
        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.CancelAgreementAsync(agreementId));

        Assert.Empty(server.Requests);
    }
}
