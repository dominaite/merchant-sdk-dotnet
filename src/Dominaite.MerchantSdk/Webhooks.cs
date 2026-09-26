using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dominaite.MerchantSdk;

/// <summary>
/// Verifying inbound webhook deliveries.
/// </summary>
/// <remarks>
/// Dominaite signs every webhook POST with the endpoint's <c>whsec_</c> secret. Verify the
/// signature BEFORE you parse the body or trust a single field in it: an unverified webhook is
/// just an unauthenticated stranger POSTing JSON at your server. Deliveries are AT-LEAST-ONCE,
/// so dedupe on the envelope's <c>id</c>, respond 2xx fast, and queue the real work instead of
/// doing it inline.
/// </remarks>
public static class Webhooks
{
    /// <summary>
    /// The default clock skew allowed between the signature's timestamp and your server's clock,
    /// in seconds. Matches the server's own tolerance.
    /// </summary>
    public const int DefaultToleranceSeconds = 300;

    private const int MacLengthBytes = 32;

    private static readonly JsonSerializerOptions EventJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Verifies one webhook delivery. Returning normally means the payload is authentic and
    /// fresh, and only then is it safe to parse.
    /// </summary>
    /// <remarks>
    /// The MAC comparison is constant-time, and the MAC is checked BEFORE the timestamp so an
    /// unsigned request can never learn anything about your tolerance window.
    /// </remarks>
    /// <param name="payload">
    /// The RAW request body, byte for byte as received. Do not pretty-print it, do not round-trip
    /// it through a JSON parser, do not let a framework re-serialize it. One changed byte is one
    /// failed signature.
    /// </param>
    /// <param name="signatureHeader">
    /// The <c>X-Webhook-Signature</c> value (<c>t={unix_seconds},v1={lowercase_hex}</c>).
    /// </param>
    /// <param name="secret">That endpoint's <c>whsec_...</c> secret, used as UTF-8 key bytes.</param>
    /// <param name="toleranceSeconds">
    /// Bounds <c>|now - t|</c>. Passing 0 accepts only the exact second, which is not what you
    /// want in production.
    /// </param>
    /// <param name="nowUnixSeconds">
    /// Unix seconds, for tests and pinned vectors. Null reads the system clock.
    /// </param>
    /// <exception cref="DominaiteWebhookException">The delivery did not verify.</exception>
    public static void Verify(
        byte[] payload,
        string signatureHeader,
        string secret,
        int toleranceSeconds = DefaultToleranceSeconds,
        long? nowUnixSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(secret);

        var (rawTimestamp, timestamp, mac) = ParseSignatureHeader(signatureHeader);

        // The signed message is "{t}.{raw_body}" - the timestamp EXACTLY as it appeared in the
        // header, a dot, then the body bytes untouched. Never a parsed-and-reformatted number:
        // the wire contract's header grammar pins the raw substring so every SDK accepts and
        // rejects the same bytes.
        var prefix = Encoding.UTF8.GetBytes(rawTimestamp + ".");
        var signed = new byte[prefix.Length + payload.Length];
        prefix.CopyTo(signed, 0);
        payload.CopyTo(signed, prefix.Length);

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), signed);
        if (!CryptographicOperations.FixedTimeEquals(expected, mac))
        {
            throw new DominaiteWebhookException(
                WebhookFailureReason.SignatureMismatch,
                "The webhook signature does not match the payload.");
        }

        // Only authentic deliveries get this far, so a tolerance failure is a replay rather than
        // a probe.
        var now = nowUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var drift = Math.Abs(now - timestamp);
        if (drift > toleranceSeconds)
        {
            throw new DominaiteWebhookException(
                WebhookFailureReason.TimestampOutOfTolerance,
                $"The webhook timestamp {timestamp} is outside the {toleranceSeconds}s tolerance around {now}.");
        }
    }

    /// <summary>
    /// Verifies one webhook delivery whose body you hold as a string. The string is encoded as
    /// UTF-8, which is what the delivery was.
    /// </summary>
    /// <param name="payload">The RAW request body, exactly as received.</param>
    /// <param name="signatureHeader">The <c>X-Webhook-Signature</c> value.</param>
    /// <param name="secret">That endpoint's <c>whsec_...</c> secret.</param>
    /// <param name="toleranceSeconds">Bounds <c>|now - t|</c>.</param>
    /// <param name="nowUnixSeconds">Unix seconds, for tests. Null reads the system clock.</param>
    /// <exception cref="DominaiteWebhookException">The delivery did not verify.</exception>
    public static void Verify(
        string payload,
        string signatureHeader,
        string secret,
        int toleranceSeconds = DefaultToleranceSeconds,
        long? nowUnixSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        Verify(Encoding.UTF8.GetBytes(payload), signatureHeader, secret, toleranceSeconds, nowUnixSeconds);
    }

    /// <summary>
    /// The non-throwing form, for handlers that would rather branch than catch. False means do
    /// not process the delivery.
    /// </summary>
    /// <param name="payload">The RAW request body, exactly as received.</param>
    /// <param name="signatureHeader">The <c>X-Webhook-Signature</c> value.</param>
    /// <param name="secret">That endpoint's <c>whsec_...</c> secret.</param>
    /// <param name="failure">Why verification failed, or null when it succeeded.</param>
    /// <param name="toleranceSeconds">Bounds <c>|now - t|</c>.</param>
    /// <param name="nowUnixSeconds">Unix seconds, for tests. Null reads the system clock.</param>
    /// <returns>True when the delivery is authentic and fresh.</returns>
    public static bool TryVerify(
        string payload,
        string signatureHeader,
        string secret,
        out DominaiteWebhookException? failure,
        int toleranceSeconds = DefaultToleranceSeconds,
        long? nowUnixSeconds = null)
    {
        try
        {
            Verify(payload, signatureHeader, secret, toleranceSeconds, nowUnixSeconds);
            failure = null;
            return true;
        }
        catch (DominaiteWebhookException error)
        {
            failure = error;
            return false;
        }
    }

    /// <summary>
    /// Verifies one webhook delivery, then parses it into a <see cref="WebhookEvent"/>. The
    /// signature is checked over the raw bytes first, exactly as <see cref="Verify(byte[], string, string, int, long?)"/>
    /// does; nothing is parsed unless it passes.
    /// </summary>
    /// <param name="payload">The RAW request body, byte for byte as received.</param>
    /// <param name="signatureHeader">The <c>X-Webhook-Signature</c> value.</param>
    /// <param name="secret">That endpoint's <c>whsec_...</c> secret.</param>
    /// <param name="toleranceSeconds">Bounds <c>|now - t|</c>.</param>
    /// <param name="nowUnixSeconds">Unix seconds, for tests. Null reads the system clock.</param>
    /// <returns>The verified event.</returns>
    /// <exception cref="DominaiteWebhookException">
    /// The delivery did not verify, or verified but is not a readable envelope
    /// (<see cref="WebhookFailureReason.MalformedPayload"/>).
    /// </exception>
    public static WebhookEvent VerifyAndParse(
        byte[] payload,
        string signatureHeader,
        string secret,
        int toleranceSeconds = DefaultToleranceSeconds,
        long? nowUnixSeconds = null)
    {
        Verify(payload, signatureHeader, secret, toleranceSeconds, nowUnixSeconds);
        return ParseEvent(payload);
    }

    /// <summary>
    /// Verifies one webhook delivery whose body you hold as a string, then parses it into a
    /// <see cref="WebhookEvent"/>.
    /// </summary>
    /// <param name="payload">The RAW request body, exactly as received.</param>
    /// <param name="signatureHeader">The <c>X-Webhook-Signature</c> value.</param>
    /// <param name="secret">That endpoint's <c>whsec_...</c> secret.</param>
    /// <param name="toleranceSeconds">Bounds <c>|now - t|</c>.</param>
    /// <param name="nowUnixSeconds">Unix seconds, for tests. Null reads the system clock.</param>
    /// <returns>The verified event.</returns>
    /// <exception cref="DominaiteWebhookException">
    /// The delivery did not verify, or verified but is not a readable envelope.
    /// </exception>
    public static WebhookEvent VerifyAndParse(
        string payload,
        string signatureHeader,
        string secret,
        int toleranceSeconds = DefaultToleranceSeconds,
        long? nowUnixSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return VerifyAndParse(Encoding.UTF8.GetBytes(payload), signatureHeader, secret, toleranceSeconds, nowUnixSeconds);
    }

    /// <summary>
    /// Reads an already-verified body. Unknown fields are ignored and every field added after the
    /// first release (apiVersion, sequence) is optional, so an older payload still parses.
    /// </summary>
    private static WebhookEvent ParseEvent(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw MalformedPayload("the body is not a JSON object");
            }

            var evt = new WebhookEvent
            {
                Id = RequiredString(root, "id"),
                Type = RequiredString(root, "type"),
                ApiVersion = OptionalString(root, "apiVersion"),
                CreatedAt = OptionalInstant(root, "createdAt"),
                Raw = root.Clone(),
            };

            if (root.TryGetProperty("data", out var data))
            {
                evt.Data = data.Clone();
            }

            if (evt.Data.ValueKind == JsonValueKind.Object)
            {
                if (evt.Type.StartsWith("payment.", StringComparison.Ordinal))
                {
                    evt.Payment = evt.Data.Deserialize<PaymentEventData>(EventJsonOptions)!;
                    evt.Payment.Raw = evt.Data;
                }
                else if (evt.Type.StartsWith("agreement.", StringComparison.Ordinal))
                {
                    evt.Agreement = evt.Data.Deserialize<AgreementEventData>(EventJsonOptions)!;
                    evt.Agreement.Raw = evt.Data;
                }
                else if (evt.Type.StartsWith("charge.", StringComparison.Ordinal))
                {
                    evt.Charge = evt.Data.Deserialize<ChargeEventData>(EventJsonOptions)!;
                    evt.Charge.Raw = evt.Data;
                }
            }

            return evt;
        }
        catch (JsonException error)
        {
            throw MalformedPayload($"the body is not a readable envelope ({error.Message})");
        }
    }

    private static string RequiredString(JsonElement root, string name)
        => OptionalString(root, name) is { Length: > 0 } value
            ? value
            : throw MalformedPayload($"no \"{name}\" string");

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw MalformedPayload($"\"{name}\" is not a string");
    }

    private static DateTimeOffset? OptionalInstant(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var instant)
            ? instant
            : throw MalformedPayload($"\"{name}\" is not an ISO 8601 instant");
    }

    private static DominaiteWebhookException MalformedPayload(string detail)
        => new(WebhookFailureReason.MalformedPayload, $"Malformed webhook payload: {detail}.");

    /// <summary>
    /// Splits <c>t={unix_seconds},v1={hex}</c> into its parts, enforcing the wire contract's
    /// header grammar (WEBHOOKS-CONTRACT.md, normative 2026-08-21).
    /// </summary>
    /// <remarks>
    /// Fields are matched by name rather than by position, so a future scheme that appends
    /// another field (or reorders these two) still verifies. An unknown field is ignored; a
    /// missing or repeated <c>t</c>/<c>v1</c> is not. The grammar is strict on purpose: no
    /// whitespace anywhere, <c>t</c> is raw ASCII digits (kept verbatim for the MAC input),
    /// <c>v1</c> is exactly 64 LOWERCASE hex characters - the platform never emits anything
    /// wider, and a wider accept set only helps forgers probe.
    /// </remarks>
    private static (string RawTimestamp, long Timestamp, byte[] Mac) ParseSignatureHeader(string header)
    {
        if (string.IsNullOrEmpty(header))
        {
            throw Malformed("the signature header is empty");
        }

        string? rawTimestamp = null;
        long timestamp = 0;
        byte[]? mac = null;

        foreach (var field in header.Split(','))
        {
            var separator = field.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw Malformed($"field \"{Truncate(field)}\" is not name=value");
            }

            var name = field[..separator];
            var value = field[(separator + 1)..];

            switch (name)
            {
                case "t":
                    if (rawTimestamp is not null)
                    {
                        throw Malformed("repeated t field");
                    }

                    if (value.Length == 0 || !value.All(char.IsAsciiDigit)
                        || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp))
                    {
                        throw Malformed($"timestamp \"{Truncate(value)}\" is not unix seconds");
                    }

                    rawTimestamp = value;
                    break;

                case "v1":
                    if (mac is not null)
                    {
                        throw Malformed("repeated v1 field");
                    }

                    if (value.Length != MacLengthBytes * 2 || !value.All(IsLowercaseHex))
                    {
                        throw Malformed("the v1 signature is not 64 lowercase hex characters");
                    }

                    mac = Convert.FromHexString(value);
                    break;

                default:
                    // A scheme version we do not know about yet. Ignoring it lets v2 roll out
                    // alongside v1 without breaking this verifier.
                    break;
            }
        }

        if (rawTimestamp is null && mac is null)
        {
            throw Malformed("no t or v1 field");
        }

        if (rawTimestamp is null)
        {
            throw Malformed("no t field");
        }

        if (mac is null)
        {
            throw Malformed("no v1 field");
        }

        return (rawTimestamp, timestamp, mac);
    }

    private static bool IsLowercaseHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f');

    private static DominaiteWebhookException Malformed(string detail)
        => new(WebhookFailureReason.MalformedSignature, $"Malformed webhook signature: {detail}.");

    /// <summary>Keeps an attacker-controlled header out of your logs at full length.</summary>
    private static string Truncate(string value)
    {
        const int Limit = 32;
        return value.Length <= Limit ? value : value[..Limit] + "...";
    }
}
