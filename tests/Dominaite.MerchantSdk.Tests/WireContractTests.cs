using System.Text.Json;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// Pins this SDK's hardcoded enumerations against the gateway's live contract.
/// </summary>
/// <remarks>
/// <c>merchant-api-wire-contract.json</c> next to this file is the machine-relevant projection
/// of the gateway's <c>GET /merchant-api/integration/contract</c>, refreshed by
/// <c>.github/workflows/contract-drift.yml</c>. When one of these fails the gateway moved:
/// fix the SDK and release, never the fixture.
/// </remarks>
public class WireContractTests
{
    private static JsonElement Wire()
        => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "merchant-api-wire-contract.json")))
            .RootElement.Clone();

    private static List<string> Strings(JsonElement array)
        => [.. array.EnumerateArray().Select(item => item.GetString()!)];

    [Fact]
    public void StatusVocabulary_MatchesTheGateway_InOrder()
    {
        Assert.Equal(Strings(Wire().GetProperty("statuses")), TransactionStatuses.All);
    }

    [Fact]
    public void StorefrontCodes_MatchTheGateway_InOrder_AndNoneIsRetryable()
    {
        var storefront = Wire().GetProperty("errorCodes").GetProperty("storefront").EnumerateArray().ToList();

        Assert.Equal(ErrorCodes.Storefront, storefront.Select(entry => entry.GetProperty("code").GetString()));
        Assert.Equal(
            new Dictionary<string, int>
            {
                [ErrorCodes.StorefrontMismatch] = 400,
                [ErrorCodes.StorefrontInactive] = 409,
                [ErrorCodes.StorefrontNotWhitelisted] = 409,
            },
            storefront.ToDictionary(entry => entry.GetProperty("code").GetString()!, entry => entry.GetProperty("httpStatus").GetInt32()));
        Assert.All(storefront, entry => Assert.Equal(JsonValueKind.False, entry.GetProperty("retry").ValueKind));
    }

    [Fact]
    public void ValidationResponses_AreHttp400()
    {
        Assert.Equal(400, Wire().GetProperty("validationHttpStatus").GetInt32());
    }
}
