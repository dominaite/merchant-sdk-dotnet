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

    /// <summary>The stored payment method id every SDK's charge and revoke vectors use.</summary>
    private const string PaymentMethodId = "pm_0123456789abcdef0123456789abcdef";

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

        Assert.Equal("8f5fba0b29a8eea81b76a0e6d7119e79ec68f586910f77713b045652e5ce9b74", signature);
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

        Assert.Equal("70002896ec8411efb7754de6c49c2fd6f35bb2d001966978a2f573de1914e68d", signature);
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

        Assert.Equal("dd809cb0b902326704a380110c29d9f789cc355864e1ed1de663157342834010", signature);
    }

    /// <summary>
    /// The stored-payment-method charge vector: the only POST besides sessions, and the only
    /// vector whose canonical path carries a resource id. Shared byte-for-byte with the gateway's
    /// MerchantApiRequestAuthenticator tests.
    /// </summary>
    [Fact]
    public void TheChargeVectorReproducesByteForByte()
    {
        var body = """{"amount":2500,"currency":"EUR","orderReference":"order-1043"}""";

        Assert.Equal(
            "641a0d2b08f88ebc458dca49410dede0a166359a5030bff5c977e507f13ab828",
            RequestSigner.Sha256Hex(body));

        var signature = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "POST",
            Path = $"{DominaiteClient.PaymentMethodsPath}/{PaymentMethodId}/charges",
            IdempotencyKey = "00000000-0000-4000-8000-000000000003",
            Body = body,
        });

        Assert.Equal("9ce9f54efa2533a46aa4493b97b56aeb657f41d6a18f1c008c7fd412029aebf9", signature);
    }

    /// <summary>
    /// The revoke vector: DELETE signs an empty idempotency key and an empty body, exactly like
    /// GET. Only the method differs from a GET on the same path, and the signature must move.
    /// </summary>
    [Fact]
    public void TheRevokeVectorReproducesByteForByte()
    {
        var path = $"{DominaiteClient.PaymentMethodsPath}/{PaymentMethodId}";

        var signature = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "DELETE",
            Path = path,
            IdempotencyKey = string.Empty,
            Body = string.Empty,
        });

        Assert.Equal("9330100343c4b820504890a09829a193d5815ca39e92160fdfc13d320a802a02", signature);

        var asGet = RequestSigner.Sign(new SignatureInput
        {
            Secret = Secret,
            Timestamp = "1755302400",
            Method = "GET",
            Path = path,
            IdempotencyKey = string.Empty,
            Body = string.Empty,
        });

        Assert.NotEqual(signature, asGet);
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
