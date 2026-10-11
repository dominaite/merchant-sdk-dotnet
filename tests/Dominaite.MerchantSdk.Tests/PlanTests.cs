using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The plan routes against the loopback server: what each call sends and signs, and what it reads
/// back.
/// </summary>
public class PlanTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PlanId = "plan_0123456789abcdef0123456789abcdef";
    private const string PlanKey = "plan-pro-monthly";

    private const string ActivePlan = """
        {"id":"plan_0123456789abcdef0123456789abcdef","name":"Pro monthly","amount":2500,"currency":"EUR","intervalUnit":"month","intervalCount":1,"periodCount":null,"trialDays":14,"status":"active","cancelUrl":"https://shop.example.com/account/cancel","createdAt":"2026-10-01T08:00:00Z","archivedAt":null}
        """;

    private const string ArchivedPlan = """
        {"id":"plan_fedcba9876543210fedcba9876543210","name":"Starter","amount":900,"currency":"EUR","intervalUnit":"fortnight","intervalCount":1,"periodCount":12,"trialDays":0,"status":"archived","createdAt":"2026-09-01T08:00:00Z","archivedAt":"2026-09-20T12:00:00Z"}
        """;

    private static string PlanPath => $"{DominaiteClient.PlansPath}/{PlanId}";

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

    [Fact]
    public async Task CreateSendsThePlanInWireOrderWithExtraFieldsAndSignsItsKey()
    {
        using var server = new MockServer(Data(201, ActivePlan));
        using var client = ClientFor(server);

        var request = new CreatePlanRequest
        {
            Name = "Pro monthly",
            Amount = 2500,
            Currency = "EUR",
            IntervalUnit = IntervalUnits.Month,
            TrialDays = 14,
            CancelUrl = "https://shop.example.com/account/cancel",
            IdempotencyKey = PlanKey,
        };
        request.Extra["futureField"] = "x";
        request.Extra["name"] = "never sent";

        var plan = await client.CreatePlanAsync(request);

        var sent = server.LastRequest;
        Assert.Equal("POST", sent.Method);
        Assert.Equal("/api" + DominaiteClient.PlansPath, sent.Path);
        Assert.Equal(
            """{"name":"Pro monthly","amount":2500,"currency":"EUR","intervalUnit":"month","intervalCount":1,"trialDays":14,"cancelUrl":"https://shop.example.com/account/cancel","futureField":"x"}""",
            sent.Body);
        Assert.Equal(PlanKey, sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, DominaiteClient.PlansPath, PlanKey);

        Assert.Equal(PlanId, plan.Id);
        Assert.Equal(2500, plan.Amount);
        Assert.Equal(IntervalUnits.Month, plan.IntervalUnit);
        Assert.Null(plan.PeriodCount);
        Assert.Equal(14, plan.TrialDays);
        Assert.True(plan.IsActive);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), plan.CreatedAt);
        Assert.Null(plan.ArchivedAt);
        Assert.Equal("Pro monthly", plan.Raw.GetProperty("name").GetString());
    }

    [Fact]
    public async Task AReplayOfTheSameKeyReadsThePlanMadeTheFirstTime()
    {
        using var server = new MockServer(Data(200, ActivePlan));
        using var client = ClientFor(server);

        var plan = await client.CreatePlanAsync(new CreatePlanRequest
        {
            Name = "Pro monthly",
            Amount = 2500,
            Currency = "EUR",
            IntervalUnit = IntervalUnits.Month,
            IdempotencyKey = PlanKey,
        });

        Assert.Equal(PlanId, plan.Id);
    }

    [Fact]
    public async Task CreateRefusesAMissingKeyBeforeSending()
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.CreatePlanAsync(new CreatePlanRequest
        {
            Name = "Pro monthly",
            Amount = 2500,
            Currency = "EUR",
            IntervalUnit = IntervalUnits.Month,
        }));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task AKeyFirstUsedForAnotherPlanIsAnApiErrorThatCarriesTheKey()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(422, "IDEMPOTENCY_KEY_REUSED", "This Idempotency-Key was first used for a different plan; use a fresh key."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(() => client.CreatePlanAsync(new CreatePlanRequest
        {
            Name = "Pro yearly",
            Amount = 25000,
            Currency = "EUR",
            IntervalUnit = IntervalUnits.Year,
            IdempotencyKey = PlanKey,
        }));

        Assert.Equal("IDEMPOTENCY_KEY_REUSED", error.Code);
        Assert.Equal(422, error.HttpStatus);
        Assert.Equal(PlanKey, error.IdempotencyKey);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task AnUnavailableGatewayIsRetryableWithTheSameKey()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(503, "MERCHANT_API_UNAVAILABLE", "Plans cannot be created right now. Retry later with the same Idempotency-Key."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteTransportException>(() => client.CreatePlanAsync(new CreatePlanRequest
        {
            Name = "Pro monthly",
            Amount = 2500,
            Currency = "EUR",
            IntervalUnit = IntervalUnits.Month,
            IdempotencyKey = PlanKey,
        }));

        Assert.True(error.IsRetryable);
        Assert.Equal(PlanKey, error.IdempotencyKey);
    }

    [Fact]
    public async Task TheListSignsAGetWithNoKeyAndNoBodyAndKeepsEveryPlan()
    {
        using var server = new MockServer(Data(200, $"[{ActivePlan.Trim()},{ArchivedPlan.Trim()}]"));
        using var client = ClientFor(server);

        var plans = await client.ListPlansAsync();

        var sent = server.LastRequest;
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api" + DominaiteClient.PlansPath, sent.Path);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, DominaiteClient.PlansPath, string.Empty);

        Assert.Equal(2, plans.Count);
        Assert.True(plans[0].IsActive);
        Assert.Equal(PlanStatuses.Archived, plans[1].Status);
        Assert.False(plans[1].IsActive);
        Assert.Equal("fortnight", plans[1].IntervalUnit);
        Assert.Equal(12, plans[1].PeriodCount);
        Assert.Null(plans[1].CancelUrl);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero), plans[1].ArchivedAt);
        Assert.Equal("plan_fedcba9876543210fedcba9876543210", plans[1].Raw.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AnAccountWithoutPlansGivesAnEmptyList()
    {
        using var server = new MockServer(Data(200, "[]"));
        using var client = ClientFor(server);

        Assert.Empty(await client.ListPlansAsync());
    }

    [Fact]
    public async Task ReadSignsThePathWithTheIdAsGiven()
    {
        using var server = new MockServer(Data(200, ActivePlan));
        using var client = ClientFor(server);

        var plan = await client.GetPlanAsync($" {PlanId} ");

        var sent = server.LastRequest;
        Assert.Equal("GET", sent.Method);
        Assert.Equal("/api" + PlanPath, sent.Path);
        AssertSignatureMatches(sent, PlanPath, string.Empty);
        Assert.Equal("Pro monthly", plan.Name);
    }

    [Fact]
    public async Task AnUnknownPlanIsAnApiErrorWithItsCode()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(404, "PLAN_NOT_FOUND", "No plan with this id."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(() => client.GetPlanAsync(PlanId));

        Assert.Equal("PLAN_NOT_FOUND", error.Code);
        Assert.Equal(404, error.HttpStatus);
        Assert.Equal("PLAN_NOT_FOUND", error.RawResult.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// The PATCH sends only what changes, an empty cancel page included (it removes the page), and
    /// takes no idempotency key: none is sent and an empty one is signed.
    /// </summary>
    [Fact]
    public async Task UpdateSendsOnlyTheChangedFieldsWithNoKey()
    {
        using var server = new MockServer(Data(200, ActivePlan));
        using var client = ClientFor(server);

        var request = new UpdatePlanRequest { TrialDays = 0, CancelUrl = string.Empty };
        request.Extra["futureField"] = 1;
        var plan = await client.UpdatePlanAsync(PlanId, request);

        var sent = server.LastRequest;
        Assert.Equal("PATCH", sent.Method);
        Assert.Equal("/api" + PlanPath, sent.Path);
        Assert.Equal("""{"trialDays":0,"cancelUrl":"","futureField":1}""", sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, PlanPath, string.Empty);
        Assert.Equal(PlanId, plan.Id);
    }

    [Fact]
    public async Task AnUpdateThatChangesNothingIsRefusedBeforeSending()
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.UpdatePlanAsync(PlanId, new UpdatePlanRequest()));

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task DeleteArchivesWithASignedDeleteAndResolvesOnNoContent()
    {
        using var server = new MockServer(Reply.Raw(204, string.Empty), Reply.Raw(204, string.Empty));
        using var client = ClientFor(server);

        await client.DeletePlanAsync(PlanId);
        await client.DeletePlanAsync(PlanId);

        Assert.Equal(2, server.Requests.Count);
        var sent = server.LastRequest;
        Assert.Equal("DELETE", sent.Method);
        Assert.Equal("/api" + PlanPath, sent.Path);
        Assert.Equal(string.Empty, sent.Body);
        Assert.Null(sent.Header("Idempotency-Key"));
        AssertSignatureMatches(sent, PlanPath, string.Empty);
    }

    [Fact]
    public async Task DeleteOfAnUnknownPlanIsAnApiError()
    {
        using var server = new MockServer(Reply.ErrorEnvelope(404, "PLAN_NOT_FOUND", "No plan with this id."));
        using var client = ClientFor(server);

        var error = await Assert.ThrowsAsync<DominaiteApiException>(() => client.DeletePlanAsync(PlanId));

        Assert.Equal("PLAN_NOT_FOUND", error.Code);
    }

    [Theory]
    [InlineData("plan_1/../../payments")]
    [InlineData("plan_1?x=1")]
    [InlineData("")]
    public async Task AnIdThatIsNotOnePathSegmentIsRefusedBeforeSending(string planId)
    {
        using var server = new MockServer();
        using var client = ClientFor(server);

        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.GetPlanAsync(planId));
        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.UpdatePlanAsync(planId, new UpdatePlanRequest { Name = "x" }));
        await Assert.ThrowsAsync<DominaiteValidationException>(() => client.DeletePlanAsync(planId));

        Assert.Empty(server.Requests);
    }
}
