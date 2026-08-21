using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The known-answer vectors shared with the gateway, the dashboard and every other Dominaite SDK.
/// If any of these fails, nothing else in the suite matters: every live call would come back
/// INVALID_SIGNATURE.
/// </summary>
public class SigningVectorTests
{
    /// <summary>A dummy secret that authenticates nothing. Same value in every SDK's suite.</summary>
    private const string Secret = "dms_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void ThePostVectorReproducesByteForByte()
    {
        var body = """{"amount":2500,"currency":"EUR","orderReference":"order-1042"}""";

        Assert.Equal(
            "aa3edd72cd1829f4e053abb048b08c1ae91c2d67b08955997c4b6c4dab4f98ff",
            RequestSigner.Sha256Hex(body));

        var signature = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "POST",
            Path = DominaiteClient.SessionsPath,
            IdempotencyKey = "00000000-0000-4000-8000-000000000001",
            Body = body,
        });

        Assert.Equal("95759958a0a0a9bd3e6e37101c01e8e7fee1166406e4ac2ff488764f5f742cbf", signature);
    }

    /// <summary>
    /// GET signs an empty idempotency key AND an empty body. The payload is still five lines, and
    /// a dropped separator here is the classic bug.
    /// </summary>
    [Fact]
    public void TheGetVectorReproducesByteForByte()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            RequestSigner.Sha256Hex(string.Empty));

        var signature = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "GET",
            Path = $"{DominaiteClient.SessionsPath}/00000000-0000-4000-8000-000000000002",
            IdempotencyKey = string.Empty,
            Body = string.Empty,
        });

        Assert.Equal("010635e61caabdb82a031a51fa56999b670b61d57239e5fa3db71a43c731f93d", signature);
    }

    /// <summary>
    /// The non-ASCII vector: it catches an implementation that hashes different bytes than it
    /// transmits. The exact body string below is what gets hashed and sent.
    /// </summary>
    [Fact]
    public void TheNonAsciiVectorReproducesByteForByte()
    {
        var body = """{"amount":2500,"currency":"EUR","orderReference":"order-1042","customer":{"firstName":"Анна","lastName":"Müller"}}""";

        Assert.Equal(
            "baf00d6116d9f2eec6c3a422af0bc2c342717f669aa2350ef6ed556f57ac34b5",
            RequestSigner.Sha256Hex(body));

        var signature = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "POST",
            Path = DominaiteClient.SessionsPath,
            IdempotencyKey = "00000000-0000-4000-8000-000000000003",
            Body = body,
        });

        Assert.Equal("460659cb1218d97bf2e86c1c09c60f0db87197c499d8296dd5d07a614e17257c", signature);
    }

    [Fact]
    public void TheMethodIsUppercasedBeforeSigning()
    {
        var lower = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "get",
            Path = DominaiteClient.PingPath,
            IdempotencyKey = string.Empty,
            Body = string.Empty,
        });

        var upper = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "GET",
            Path = DominaiteClient.PingPath,
            IdempotencyKey = string.Empty,
            Body = string.Empty,
        });

        Assert.Equal(upper, lower);
    }

    /// <summary>
    /// The idempotency key is inside the signature, which is what makes a captured request
    /// unusable with a different key.
    /// </summary>
    [Fact]
    public void ChangingTheIdempotencyKeyChangesTheSignature()
    {
        var body = """{"amount":2500,"currency":"EUR","orderReference":"order-1042"}""";

        var first = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "POST",
            Path = DominaiteClient.SessionsPath,
            IdempotencyKey = "00000000-0000-4000-8000-000000000001",
            Body = body,
        });

        var second = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "POST",
            Path = DominaiteClient.SessionsPath,
            IdempotencyKey = "00000000-0000-4000-8000-000000000009",
            Body = body,
        });

        Assert.NotEqual(first, second);
    }

    /// <summary>The secret must not leak through a log line or a debugger watch window.</summary>
    [Fact]
    public void SignatureInputToStringRedactsTheSecret()
    {
        var input = new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "POST",
            Path = DominaiteClient.SessionsPath,
            IdempotencyKey = "00000000-0000-4000-8000-000000000001",
            Body = string.Empty,
        };

        var text = input.ToString();

        Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("dms_", text, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
    }
}
