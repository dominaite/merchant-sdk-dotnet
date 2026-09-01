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
    public void ValidationResponses_AreHttp400()
    {
        Assert.Equal(400, Wire().GetProperty("validationHttpStatus").GetInt32());
    }

    [Fact]
    public void WalletTypes_MatchTheGateway_InOrder()
    {
        Assert.Equal(Strings(Wire().GetProperty("wallets").GetProperty("walletTypes")), WalletTypes.All);
    }

    [Fact]
    public void WalletReportingFields_ArePaymentMethodAndWalletType_BothOptional()
    {
        var fields = Wire().GetProperty("wallets").GetProperty("reportingFields").EnumerateArray().ToList();

        Assert.Equal(
            new[] { "paymentMethod", "walletType" },
            fields.Select(field => field.GetProperty("path").GetString()!).ToList());
        Assert.All(fields, field => Assert.False(field.GetProperty("required").GetBoolean()));
    }
}
