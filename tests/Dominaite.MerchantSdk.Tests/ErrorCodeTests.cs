using System.Reflection;
using System.Text.Json;
using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The error-code constants: every code the gateway's integration contract names has one, and the
/// per-route lists hold the codes each route answers with.
/// </summary>
public class ErrorCodeTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PaymentMethodId = "pm_0123456789abcdef0123456789abcdef";

    private static JsonElement Wire()
        => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "merchant-api-wire-contract.json")))
            .RootElement.Clone();

    private static List<string> Codes(string group)
        => [.. Wire().GetProperty("errorCodes").GetProperty(group).EnumerateArray().Select(entry => entry.GetProperty("code").GetString()!)];

    /// <summary>Every public <c>const string</c> on a public static class of the SDK.</summary>
    private static HashSet<string> SdkConstants()
        => [.. typeof(DominaiteClient).Assembly.GetExportedTypes()
            .Where(type => type.IsAbstract && type.IsSealed)
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)];

    private static DominaiteClient ClientFor(MockServer server)
        => new(KeyId, Secret, new DominaiteClientOptions { BaseUrl = server.BaseUrl, Timeout = TimeSpan.FromSeconds(10), MaxRetries = 0 });

    private static ChargeRequest Charge() => new()
    {
        Amount = 2500,
        Currency = "EUR",
        OrderReference = "order-1043",
        IdempotencyKey = "charge-order-1043",
    };

    [Fact]
    public void EveryCodeTheContractNamesHasAConstant()
    {
        var constants = SdkConstants();
        var codes = Wire().GetProperty("errorCodes").EnumerateObject()
            .SelectMany(group => group.Value.EnumerateArray())
            .Select(entry => entry.GetProperty("code").GetString()!)
            .ToList();

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.Contains(code, constants));
    }

    [Fact]
    public void TheCodeGroupsMatchTheContract_InOrder()
    {
        Assert.Equal(Codes("auth"), AuthErrorCodes.All);
        Assert.Equal(Codes("idempotency"), ErrorCodes.Idempotency);
        Assert.Equal(Codes("transient"), ErrorCodes.Transient);
        Assert.Equal(Codes("validation"), ValidationErrorCodes.Fields);
    }

    [Fact]
    public void TheChargeCodesAreEveryCodeTheChargeRouteThrows()
    {
        Assert.Equal(
            new[]
            {
                "PAYMENT_METHOD_NOT_ACTIVE",
                "DUPLICATE_REQUEST",
                "IDEMPOTENCY_KEY_REUSED",
                "CHARGE_OUTCOME_UNKNOWN",
                "CHARGE_FAILED",
                "PAYMENT_METHOD_CHARGES_DISABLED",
                "PAYMENT_PROCESSING_UNAVAILABLE",
                "PAYMENT_METHOD_NOT_FOUND",
                "CHARGE_IN_PROGRESS",
                "CHARGE_CURRENCY_MISMATCH",
                "AGREEMENT_NOT_FOUND",
                "AGREEMENT_METHOD_MISMATCH",
                "AGREEMENT_CONSENT_MISSING",
                "AGREEMENT_CURRENCY_MISMATCH",
                "AGREEMENT_AMOUNT_MISMATCH",
                "CHARGE_AMOUNT_ABOVE_AGREEMENT",
                "AGREEMENT_PERIOD_ALREADY_CHARGED",
                "AGREEMENT_ENDED",
                "AGREEMENT_NOT_STARTED",
                "CHARGE_AMOUNT_ABOVE_CAP",
            },
            ChargeErrorCodes.All);
        Assert.DoesNotContain(ChargeErrorCodes.ChargeDeclined, ChargeErrorCodes.All);
    }

    [Fact]
    public void TheSessionRefusalsIncludeTheSubscriptionSessionCodes()
    {
        Assert.Equal(
            new[]
            {
                "PAYMENT_PROCESSING_UNAVAILABLE",
                "DUPLICATE_REQUEST",
                "ALREADY_PROCESSED",
                "IDEMPOTENCY_KEY_REUSED",
                "PRIOR_ATTEMPT_FAILED",
                "AGREEMENT_NOT_FOUND",
                "AGREEMENT_NOT_PENDING",
                "AGREEMENT_AMOUNT_MISMATCH",
                "AGREEMENT_CONSENT_REQUIRED",
                "AGREEMENT_SESSION_OPEN",
                "AGREEMENT_ALREADY_PAID",
                "AGREEMENT_VARIABLE_AMOUNT_UNSUPPORTED",
                "SUBSCRIPTIONS_NOT_ENABLED",
            },
            ErrorCodes.SessionRefusals);
        Assert.Empty(ErrorCodes.SessionRefusals.Intersect(ErrorCodes.Storefront));
    }

    [Fact]
    public void ThePlanAgreementAndWebhookCodesHaveConstants()
    {
        Assert.Equal(
            new[]
            {
                "PLAN_NOT_FOUND",
                "PLAN_NOT_ACTIVE",
                "AGREEMENT_NOT_FOUND",
                "AGREEMENT_ENDED",
                "PAYMENT_METHOD_NOT_FOUND",
                "PAYMENT_METHOD_NOT_ACTIVE",
                "PAYMENT_METHOD_CURRENCY_MISMATCH",
                "MAX_AMOUNT_BELOW_PLAN_AMOUNT",
                "AGREEMENT_CONSENT_REQUIRED",
                "PAYMENT_METHOD_CURRENCY_UNKNOWN",
                "IDEMPOTENCY_KEY_REUSED",
                "MERCHANT_API_UNAVAILABLE",
            },
            SubscriptionErrorCodes.All);
        Assert.Equal(
            new[]
            {
                "WEBHOOK_NOT_FOUND",
                "WEBHOOK_LIMIT_REACHED",
                "WEBHOOK_ALREADY_REGISTERED",
                "WEBHOOK_URL_REJECTED",
                "IDEMPOTENCY_KEY_REUSED",
                "DUPLICATE_REQUEST",
                "MERCHANT_API_UNAVAILABLE",
            },
            WebhookEndpointErrorCodes.All);
    }

    [Theory]
    [InlineData(409, "DUPLICATE_REQUEST", true)]
    [InlineData(503, "PAYMENT_PROCESSING_UNAVAILABLE", true)]
    [InlineData(503, "PAYMENT_METHOD_CHARGES_DISABLED", false)]
    [InlineData(409, "CHARGE_IN_PROGRESS", false)]
    [InlineData(422, "CHARGE_AMOUNT_ABOVE_CAP", false)]
    [InlineData(502, "CHARGE_OUTCOME_UNKNOWN", false)]
    public async Task AChargeErrorIsRetryableOnlyWhenTheSameKeyCanSucceed(int status, string code, bool retryable)
    {
        using var server = new MockServer(Reply.ErrorEnvelope(status, code, "refused"));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteChargeException>(
            () => client.ChargePaymentMethodAsync(PaymentMethodId, Charge()));

        Assert.Equal(code, error.Code);
        Assert.Equal(retryable, error.IsRetryable);
    }

    [Theory]
    [InlineData("AGREEMENT_SESSION_OPEN")]
    [InlineData("SUBSCRIPTIONS_NOT_ENABLED")]
    public async Task ASubscriptionSessionRefusalArrivesAsARefusalWithItsCode(string code)
    {
        using var server = new MockServer(Reply.Enveloped(
            $$"""{"success":false,"errorCode":"{{code}}","errorMessage":"refused","transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0"}"""));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteRefusalException>(() => client.CreateCheckoutSessionAsync(new CheckoutSessionRequest
        {
            Amount = 2500,
            Currency = "EUR",
            OrderReference = "order-1042",
            IdempotencyKey = "checkout-order-1042",
        }));

        Assert.Equal(code, error.Code);
        Assert.Equal("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0", error.TransactionId);
        Assert.False(error.IsRetryable);
    }
}
