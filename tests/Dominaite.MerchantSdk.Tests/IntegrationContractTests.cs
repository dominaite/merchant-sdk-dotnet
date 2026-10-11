using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The integration contract read against the loopback server. The sample is a cut of the live
/// contract: the modelled values, plus fields the SDK leaves on Raw.
/// </summary>
public class IntegrationContractTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string Contract = """
        {"success":true,"data":{"apiBase":"https://api.dominaite.com/payments","sessionsPath":"/merchant-api/checkout/sessions","requestSigning":{"algorithm":"HMAC-SHA256"},"webhookEventCatalog":["payment.succeeded","payment.failed"],"statuses":["pending","processing","succeeded"],"recurring":{"consentTextVersions":["recurring-2026-09"],"intervalUnits":["day","week","month","year"],"planStatuses":["active","archived"],"agreementStatuses":["pending","active","past_due","cancelled","completed","failed"],"notes":["Charges are in the plan currency."]},"verification":{"webhookEvents":["verification.succeeded","verification.failed"]},"version":"2026-09-16"}}
        """;

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            Timeout = TimeSpan.FromSeconds(10),
        });

    [Fact]
    public async Task TheContractIsASignedGetAndKeepsEveryFieldOnRaw()
    {
        using var server = new MockServer(Reply.Raw(200, Contract));
        using var client = ClientFor(server);

        var contract = await client.GetIntegrationContractAsync();

        var sent = server.LastRequest;
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api" + DominaiteClient.IntegrationContractPath, sent.Path);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        var expected = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = sent.Header("X-Timestamp")!,
            Method = "GET",
            Path = DominaiteClient.IntegrationContractPath,
            IdempotencyKey = string.Empty,
            Body = string.Empty,
        });
        Assert.Equal(expected, sent.Header("X-Signature"));

        Assert.Equal("2026-09-16", contract.Version);
        Assert.Equal("https://api.dominaite.com/payments", contract.ApiBase);
        Assert.Equal(new[] { "pending", "processing", "succeeded" }, contract.Statuses);
        Assert.Equal(new[] { WebhookEventTypes.PaymentSucceeded, WebhookEventTypes.PaymentFailed }, contract.WebhookEventCatalog);

        var recurring = Assert.IsType<IntegrationContractRecurring>(contract.Recurring);
        Assert.Equal(new[] { "recurring-2026-09" }, recurring.ConsentTextVersions);
        Assert.Equal(IntervalUnits.All, recurring.IntervalUnits);
        Assert.Equal(PlanStatuses.All, recurring.PlanStatuses);
        Assert.Equal(AgreementStatuses.All, recurring.AgreementStatuses);

        Assert.Equal("HMAC-SHA256", contract.Raw.GetProperty("requestSigning").GetProperty("algorithm").GetString());
        Assert.Equal(
            "verification.failed",
            contract.Raw.GetProperty("verification").GetProperty("webhookEvents")[1].GetString());
    }

    [Fact]
    public async Task AContractWithoutSubscriptionsHasNoRecurringPart()
    {
        using var server = new MockServer(Reply.Raw(
            200,
            """{"success":true,"data":{"apiBase":"https://api.dominaite.com/payments","statuses":["pending"],"webhookEventCatalog":[],"version":"2026-06-01"}}"""));
        using var client = ClientFor(server);

        var contract = await client.GetIntegrationContractAsync();

        Assert.Equal("2026-06-01", contract.Version);
        Assert.Null(contract.Recurring);
        Assert.Empty(contract.WebhookEventCatalog);
    }
}
