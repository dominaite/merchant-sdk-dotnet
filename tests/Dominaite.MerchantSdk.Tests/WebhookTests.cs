using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The inbound direction, pinned to the canonical cross-SDK webhook vector. That vector is
/// byte-identical in every Dominaite SDK suite (WEBHOOKS-CONTRACT.md), so a failure here means
/// this build disagrees with the gateway about the scheme itself.
/// </summary>
public class WebhookTests
{
    private const string Secret = "whsec_abababababababababababababababababababababababababababababababab";

    /// <summary>Do NOT reformat: reindenting the body changes the signature.</summary>
    private const string Body = """{"id":"7f9c24e5-1d1f-4c0a-9b6c-2f3a4d5e6f70","type":"payment.succeeded","createdAt":"2026-08-20T14:00:00Z","data":{"transactionId":"0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0","status":"succeeded","previousStatus":"pending","kind":"sale","amount":8440,"grossAmount":8701,"surchargeAmount":261,"currency":"EUR","originalTransactionId":null,"idempotencyKey":"order-123"}}""";

    private const string Header = "t=1755700000,v1=5305bcf1302fdaba8f8c19a20c899e916fb4d2a7d8d547c62529ff87c4697b72";

    private const long Timestamp = 1755700000;

    [Fact]
    public void TheCanonicalVectorVerifies()
    {
        Webhooks.Verify(Body, Header, Secret, Webhooks.DefaultToleranceSeconds, Timestamp + 10);
    }

    [Fact]
    public void ASingleByteBodyTamperFails()
    {
        var tampered = Body.Replace("\"amount\":8440", "\"amount\":8441", StringComparison.Ordinal);
        Assert.NotEqual(Body, tampered);

        var error = Assert.Throws<DominaiteWebhookException>(
            () => Webhooks.Verify(tampered, Header, Secret, Webhooks.DefaultToleranceSeconds, Timestamp + 10));

        Assert.Equal(WebhookFailureReason.SignatureMismatch, error.Reason);
    }

    [Fact]
    public void TheWrongSecretFails()
    {
        var error = Assert.Throws<DominaiteWebhookException>(() => Webhooks.Verify(
            Body,
            Header,
            "whsec_cdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcdcd",
            Webhooks.DefaultToleranceSeconds,
            Timestamp + 10));

        Assert.Equal(WebhookFailureReason.SignatureMismatch, error.Reason);
    }

    [Fact]
    public void AStaleTimestampFailsEvenWithAValidMac()
    {
        var error = Assert.Throws<DominaiteWebhookException>(() => Webhooks.Verify(
            Body,
            Header,
            Secret,
            Webhooks.DefaultToleranceSeconds,
            Timestamp + Webhooks.DefaultToleranceSeconds + 1));

        Assert.Equal(WebhookFailureReason.TimestampOutOfTolerance, error.Reason);
    }

    /// <summary>Tolerance is symmetric: a delivery from the future is just as stale.</summary>
    [Fact]
    public void ATimestampFromTheFutureFailsToo()
    {
        var error = Assert.Throws<DominaiteWebhookException>(() => Webhooks.Verify(
            Body,
            Header,
            Secret,
            Webhooks.DefaultToleranceSeconds,
            Timestamp - Webhooks.DefaultToleranceSeconds - 1));

        Assert.Equal(WebhookFailureReason.TimestampOutOfTolerance, error.Reason);
    }

    [Fact]
    public void TheEdgeOfTheToleranceStillVerifies()
    {
        Webhooks.Verify(Body, Header, Secret, Webhooks.DefaultToleranceSeconds, Timestamp + Webhooks.DefaultToleranceSeconds);
        Webhooks.Verify(Body, Header, Secret, Webhooks.DefaultToleranceSeconds, Timestamp - Webhooks.DefaultToleranceSeconds);
    }

    /// <summary>
    /// The MAC is checked BEFORE the timestamp, so an unsigned request learns nothing about the
    /// tolerance window. A stale delivery with a bad MAC is a mismatch, not a stale timestamp.
    /// </summary>
    [Fact]
    public void TheMacIsCheckedBeforeTheTimestamp()
    {
        var badMac = "t=1755700000,v1=" + new string('0', 64);

        var error = Assert.Throws<DominaiteWebhookException>(() => Webhooks.Verify(
            Body,
            badMac,
            Secret,
            Webhooks.DefaultToleranceSeconds,
            Timestamp + 100_000));

        Assert.Equal(WebhookFailureReason.SignatureMismatch, error.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("v1=5305bcf1302fdaba8f8c19a20c899e916fb4d2a7d8d547c62529ff87c4697b72")]
    [InlineData("t=1755700000")]
    [InlineData("t=not-a-number,v1=5305bcf1302fdaba8f8c19a20c899e916fb4d2a7d8d547c62529ff87c4697b72")]
    [InlineData("t=1755700000,v1=nothex-nothex-nothex-nothex-nothex-nothex-nothex-nothex-notheX")]
    [InlineData("t=1755700000,v1=abcd")]
    [InlineData("t=1755700000,t=1755700001,v1=5305bcf1302fdaba8f8c19a20c899e916fb4d2a7d8d547c62529ff87c4697b72")]
    [InlineData("t=-1755700000,v1=5305bcf1302fdaba8f8c19a20c899e916fb4d2a7d8d547c62529ff87c4697b72")]
    public void MalformedHeadersFailAsMalformed(string header)
    {
        var error = Assert.Throws<DominaiteWebhookException>(
            () => Webhooks.Verify(Body, header, Secret, Webhooks.DefaultToleranceSeconds, Timestamp));

        Assert.Equal(WebhookFailureReason.MalformedSignature, error.Reason);
    }

    private const string Mac = "5305bcf1302fdaba8f8c19a20c899e916fb4d2a7d8d547c62529ff87c4697b72";

    /// <summary>
    /// The ten shared header-grammar vectors from WEBHOOKS-CONTRACT.md (normative 2026-08-21),
    /// byte-identical across every SDK suite. Vectors 1-9 must fail as malformed; vector 10 is
    /// the unknown-field case below.
    /// </summary>
    [Theory]
    [InlineData("t=1755700000")] // 1: missing v1
    [InlineData("v1=" + Mac)] // 2: missing t
    [InlineData("t=1755700000,v1=5305BCF1302FDABA8F8C19A20C899E916FB4D2A7D8D547C62529FF87C4697B72")] // 3: uppercase hex
    [InlineData("t=1755700000,v1=" + Mac + ",v1=" + Mac)] // 4: repeated v1
    [InlineData("t=1755700000,t=1755700000,v1=" + Mac)] // 5: repeated t
    [InlineData("t=,v1=garbage,v1=" + Mac)] // 6: the audit exploit shape - empty t + repeat
    [InlineData("t=1755700000, v1=" + Mac)] // 7: whitespace after the comma
    [InlineData("t=+1755700000,v1=" + Mac)] // 8: non-digit in t
    [InlineData("garbage")] // 9: element without =
    public void TheContractGrammarVectorsFailAsMalformed(string header)
    {
        var error = Assert.Throws<DominaiteWebhookException>(
            () => Webhooks.Verify(Body, header, Secret, Webhooks.DefaultToleranceSeconds, Timestamp));

        Assert.Equal(WebhookFailureReason.MalformedSignature, error.Reason);
    }

    /// <summary>Contract grammar vector 10: an unknown field is ignored, the rest verifies.</summary>
    [Fact]
    public void TheContractUnknownFieldVectorVerifies()
    {
        Webhooks.Verify(Body, Header + ",v9=deadbeef", Secret, Webhooks.DefaultToleranceSeconds, Timestamp);
    }

    /// <summary>
    /// The grammar pins the RAW digits into the MAC input: a leading-zero timestamp whose MAC was
    /// computed over the STRIPPED number must fail, or this verifier accepts headers no other SDK
    /// (or the gateway) would ever produce or accept.
    /// </summary>
    [Fact]
    public void ALeadingZeroTimestampIsNeverReformattedIntoTheMac()
    {
        // The canonical MAC is over "1755700000.{body}". Presenting it under t=01755700000 must
        // fail: raw-substring signing makes the MAC input "01755700000.{body}".
        var error = Assert.Throws<DominaiteWebhookException>(() => Webhooks.Verify(
            Body,
            "t=01755700000,v1=" + Mac,
            Secret,
            Webhooks.DefaultToleranceSeconds,
            Timestamp));

        Assert.Equal(WebhookFailureReason.SignatureMismatch, error.Reason);
    }

    /// <summary>
    /// Fields are matched by name, not position, so a future scheme that adds one still verifies.
    /// </summary>
    [Fact]
    public void AnUnknownFieldIsIgnored()
    {
        Webhooks.Verify(
            Body,
            "v2=whatever," + Header,
            Secret,
            Webhooks.DefaultToleranceSeconds,
            Timestamp);
    }

    [Fact]
    public void TryVerifyBranchesInsteadOfThrowing()
    {
        Assert.True(Webhooks.TryVerify(Body, Header, Secret, out var none, Webhooks.DefaultToleranceSeconds, Timestamp));
        Assert.Null(none);

        Assert.False(Webhooks.TryVerify(Body, "garbage", Secret, out var failure, Webhooks.DefaultToleranceSeconds, Timestamp));
        Assert.NotNull(failure);
        Assert.Equal(WebhookFailureReason.MalformedSignature, failure.Reason);
    }

    /// <summary>
    /// The byte[] overload is what a handler that reads the raw request stream actually holds, and
    /// it must agree with the string one down to the byte.
    /// </summary>
    [Fact]
    public void TheByteOverloadAgreesWithTheStringOne()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Body);
        Webhooks.Verify(bytes, Header, Secret, Webhooks.DefaultToleranceSeconds, Timestamp);
    }
}
