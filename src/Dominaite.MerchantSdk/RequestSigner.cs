using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Dominaite.MerchantSdk;

/// <summary>
/// Everything that goes into one request signature.
/// </summary>
/// <remarks>
/// The secret is here only long enough to be hashed. <see cref="ToString"/> is overridden and
/// the debugger display is redacted so it cannot leak through a log line, an exception dump,
/// or a watch window.
/// </remarks>
[DebuggerDisplay("SignatureInput {Method,nq} {Path,nq} (secret redacted)")]
public sealed class SignatureInput
{
    /// <summary>Your API secret (<c>dms_...</c>).</summary>
    public required string Secret { get; init; }

    /// <summary>Unix SECONDS, exactly as sent in <c>X-Timestamp</c>.</summary>
    public required string Timestamp { get; init; }

    /// <summary>The HTTP method. Uppercased before signing.</summary>
    public required string Method { get; init; }

    /// <summary>
    /// The canonical path only: no host, no query string, and never the base URL's own prefix
    /// (dev's <c>/api</c> and prod's <c>/payments</c> are not part of the signed path).
    /// </summary>
    public required string Path { get; init; }

    /// <summary>The <c>Idempotency-Key</c> header value. Empty string for GET.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>The exact request body that will be sent. Empty string for GET.</summary>
    public required string Body { get; init; }

    /// <summary>
    /// A description with the secret redacted. Never returns the secret, whatever it is called
    /// from.
    /// </summary>
    /// <returns>The redacted description.</returns>
    public override string ToString()
        => $"SignatureInput {{ Method = {this.Method}, Path = {this.Path}, Secret = [REDACTED] }}";
}

/// <summary>
/// The request signing recipe, public so you can pin it against the offline vectors and debug
/// an <c>INVALID_SIGNATURE</c> without reading the client's source.
/// </summary>
public static class RequestSigner
{
    /// <summary>
    /// Builds the <c>X-Signature</c> value for one request: lowercase hex HMAC-SHA256 over
    /// <c>"{timestamp}\n{METHOD}\n{path}\n{idempotencyKey}\n{sha256hex(body)}"</c>, UTF-8
    /// throughout.
    /// </summary>
    /// <remarks>
    /// The idempotency key sits INSIDE the signature, so a captured request cannot be replayed
    /// with a different key to mint extra sessions. The payload is five lines even on a GET,
    /// where the key and the body are both empty; dropping a separator there is the classic
    /// bug. The server rejects timestamps more than 5 minutes off, so keep the server clock on
    /// NTP.
    /// </remarks>
    /// <param name="input">What to sign.</param>
    /// <returns>The lowercase hex signature.</returns>
    public static string Sign(SignatureInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var payload = string.Concat(
            input.Timestamp,
            "\n",
            input.Method.ToUpperInvariant(),
            "\n",
            input.Path,
            "\n",
            input.IdempotencyKey,
            "\n",
            Sha256Hex(input.Body));

        var mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(input.Secret),
            Encoding.UTF8.GetBytes(payload));

        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    /// <summary>
    /// Lowercase hex SHA-256 of a body. The empty string hashes to the well-known empty digest,
    /// which is what a GET signs.
    /// </summary>
    /// <param name="body">The exact body bytes, as a UTF-8 string.</param>
    /// <returns>The lowercase hex digest.</returns>
    public static string Sha256Hex(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }
}
