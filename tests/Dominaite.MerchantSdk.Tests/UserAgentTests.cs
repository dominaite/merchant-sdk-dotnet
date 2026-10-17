using System.Text.RegularExpressions;
using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The User-Agent the API sees: the SDK, its version and the runtime, then the caller's suffix.
/// </summary>
public class UserAgentTests
{
    private const string KeyId = "dmk_0123456789abcdef0123456789abcdef";
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static async Task<string?> SentUserAgent(string? suffix)
    {
        using var server = new MockServer(Reply.Enveloped("""{"pong":true,"merchantId":"m_1","clockSkewSeconds":0}"""));
        using var client = new DominaiteClient(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = server.BaseUrl,
            UserAgentSuffix = suffix,
        });
        await client.PingAsync();
        return server.LastRequest.Header("User-Agent");
    }

    [Fact]
    public async Task WithoutASuffixItIsTheSdkVersionAndTheRuntime()
    {
        var sent = await SentUserAgent(null);

        Assert.Matches(
            new Regex(@"^dominaite-dotnet/" + Regex.Escape(DominaiteClient.Version) + @" \(dotnet [^ ()]+\)$"),
            sent);
        Assert.Equal($"dominaite-dotnet/{DominaiteClient.Version} (dotnet {Environment.Version})", sent);
    }

    [Fact]
    public async Task TheSuffixFollowsAfterOneSpace()
    {
        var sent = await SentUserAgent("  shop/1.2  ");

        Assert.Equal($"dominaite-dotnet/{DominaiteClient.Version} (dotnet {Environment.Version}) shop/1.2", sent);
    }

    [Fact]
    public async Task ABlankSuffixIsNone()
    {
        var sent = await SentUserAgent("   ");

        Assert.Equal($"dominaite-dotnet/{DominaiteClient.Version} (dotnet {Environment.Version})", sent);
    }

    [Theory]
    [InlineData("shop/1.2\r\nX-Injected: 1")]
    [InlineData("shop\n1.2")]
    [InlineData("shop\u007f")]
    [InlineData("shop\t1.2")]
    public void ASuffixWithAControlCharacterIsRefused(string suffix)
    {
        var error = Assert.Throws<DominaiteValidationException>(() => new DominaiteClient(KeyId, Secret, new DominaiteClientOptions
        {
            BaseUrl = DominaiteClient.SandboxBaseUrl,
            UserAgentSuffix = suffix,
        }));

        Assert.Contains("UserAgentSuffix", error.Message, StringComparison.Ordinal);
    }
}
