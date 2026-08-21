using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dominaite.MerchantSdk;

/// <summary>
/// A server-side client for the Dominaite merchant API.
/// </summary>
/// <remarks>
/// One call from your backend opens a hosted checkout session; a two-line script tag renders the
/// payment widget on your page. Card details go straight from your customer's browser into the
/// widget, so they never touch your server, which keeps your PCI scope minimal (SAQ A).
/// Keep the <c>dms_</c> secret on the server: never ship it to a browser, never commit it, never
/// log it. One client per process is the normal shape - it owns an <see cref="HttpClient"/> and
/// its connection pool.
/// </remarks>
[DebuggerDisplay("DominaiteClient {BaseUrl,nq} (secret redacted)")]
public sealed class DominaiteClient : IDisposable
{
    /// <summary>The production merchant API.</summary>
    public const string DefaultBaseUrl = "https://api.dominaite.com/payments";

    /// <summary>
    /// The canonical path that gets signed. POST creates a session; GET
    /// <c>SessionsPath/{transactionId}</c> reads its status.
    /// </summary>
    public const string SessionsPath = "/merchant-api/checkout/sessions";

    /// <summary>The credentials-and-clock smoke test. Creates nothing.</summary>
    public const string PingPath = "/merchant-api/ping";

    /// <summary>This SDK's version, reported in the User-Agent.</summary>
    public const string Version = "0.2.0";

    private const string KeyIdPrefix = "dmk_";
    private const string SecretPrefix = "dms_";

    /// <summary>
    /// The wire encoding. camelCase to match the gateway, nulls omitted, and the relaxed encoder
    /// so non-ASCII payer names travel as UTF-8 rather than <c>\uXXXX</c> escapes - which is what
    /// every other Dominaite SDK puts on the wire.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _keyId;
    private readonly string _secret;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;

    /// <summary>
    /// Initializes a new instance of the <see cref="DominaiteClient"/> class.
    /// </summary>
    /// <param name="keyId">Your API key id (<c>dmk_...</c>). Identifies you; not secret by itself.</param>
    /// <param name="secret">Your API secret (<c>dms_...</c>). Server-side only.</param>
    /// <param name="options">Base URL, timeout, User-Agent suffix, or your own HttpClient.</param>
    /// <exception cref="DominaiteValidationException">
    /// Either credential has the wrong prefix, which catches a swapped key id and secret before
    /// anything is sent.
    /// </exception>
    public DominaiteClient(string keyId, string secret, DominaiteClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(secret);

        if (!keyId.StartsWith(KeyIdPrefix, StringComparison.Ordinal))
        {
            throw new DominaiteValidationException("keyId must start with dmk_");
        }

        if (!secret.StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            throw new DominaiteValidationException("secret must start with dms_");
        }

        options ??= new DominaiteClientOptions();

        this._keyId = keyId;
        this._secret = secret;

        var baseUrl = options.BaseUrl?.Trim().TrimEnd('/');
        this.BaseUrl = string.IsNullOrEmpty(baseUrl) ? DefaultBaseUrl : baseUrl;

        this.UserAgent = string.IsNullOrWhiteSpace(options.UserAgentSuffix)
            ? $"dominaite-dotnet/{Version}"
            : $"dominaite-dotnet/{Version} {options.UserAgentSuffix.Trim()}";

        if (options.HttpClient is not null)
        {
            this._http = options.HttpClient;
            this._ownsHttpClient = false;
        }
        else
        {
            // The API never redirects. Following a 3xx would replay a signed request - headers
            // and all - at whatever host the redirect names, so the handler refuses and the
            // response layer treats any 3xx as a hard error.
            this._http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = options.Timeout,
            };
            this._ownsHttpClient = true;
        }
    }

    /// <summary>The base URL this client is pointed at.</summary>
    public string BaseUrl { get; }

    /// <summary>The User-Agent this client sends.</summary>
    public string UserAgent { get; }

    /// <summary>
    /// Mints a random v4 UUID for use as an idempotency key. Keys are per-payment, so a fresh one
    /// is generated for every call that does not supply its own.
    /// </summary>
    /// <returns>A lowercase dashed UUID.</returns>
    public static string NewIdempotencyKey() => Guid.NewGuid().ToString("D");

    /// <summary>
    /// Verifies your credentials, your signing, and your clock without creating anything. Make
    /// this your first live call: a 401 here means the key id, the secret, or the signing, and a
    /// 503 means retry later - never both at once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ping response. Check <see cref="PingResponse.ClockSkewSeconds"/>.</returns>
    public async Task<PingResponse> PingAsync(CancellationToken cancellationToken = default)
    {
        // GET signs an EMPTY idempotency key and an EMPTY body.
        var payload = await this.SendAsync(HttpMethod.Get, PingPath, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);

        var ping = Deserialize<PingResponse>(payload, "ping");
        ping.Raw = payload;
        return ping;
    }

    /// <summary>
    /// Opens a hosted checkout session for one payment.
    /// </summary>
    /// <param name="request">
    /// The session parameters. When <see cref="CheckoutSessionRequest.IdempotencyKey"/> is null,
    /// the client generates one and writes it back onto the request so you can log it and reuse
    /// it on a retry.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The created session. Hand its cashier values to the page that renders the widget.</returns>
    /// <exception cref="DominaiteValidationException">Bad arguments; nothing was sent.</exception>
    /// <exception cref="DominaiteRefusalException">The gateway refused the session; inspect Code.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaiteApiException">An unexpected or rejecting response.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry with the same key.</exception>
    public async Task<CheckoutSession> CreateCheckoutSessionAsync(
        CheckoutSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var idempotencyKey = ResolveIdempotencyKey(request);
        var body = SerializeBody(request);

        JsonElement payload;
        try
        {
            payload = await this.SendAsync(HttpMethod.Post, SessionsPath, body, idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DominaiteException error)
        {
            error.IdempotencyKey = idempotencyKey;
            throw;
        }

        // Create is the nested shape: an inner `success` next to `checkout`. The status and ping
        // reads are flat and have neither, which is why the branch lives here rather than in the
        // envelope unwrapper.
        var succeeded = payload.TryGetProperty("success", out var success)
            && success.ValueKind == JsonValueKind.True;
        var hasCheckout = payload.TryGetProperty("checkout", out var checkout)
            && checkout.ValueKind == JsonValueKind.Object;

        if (succeeded && hasCheckout)
        {
            var session = Deserialize<CheckoutSession>(checkout, "checkout object");
            session.Raw = checkout.Clone();
            return session;
        }

        // A replay refusal names the transaction the key collided with. Carry it so the caller
        // can reconcile with GetStatusAsync instead of minting a second payment for the same
        // order.
        throw new DominaiteRefusalException(
            StringField(payload, "errorCode") ?? "UNKNOWN",
            StringField(payload, "errorMessage") ?? "The checkout session was refused.",
            StringField(payload, "transactionId"),
            payload)
        {
            IdempotencyKey = idempotencyKey,
        };
    }

    /// <summary>
    /// Creates a session, retrying transport failures only, with THE SAME idempotency key across
    /// every attempt.
    /// </summary>
    /// <remarks>
    /// Reusing the key is what makes the retry safe: a transport failure leaves you not knowing
    /// whether the request landed, and a retried key never opens a second payment. If the first
    /// attempt did land, the retry comes back as a replay refusal
    /// (<c>DUPLICATE_REQUEST</c> / <c>ALREADY_PROCESSED</c>) naming that transaction, which you
    /// read back with <see cref="GetStatusAsync"/>. Generating a fresh key per attempt would be
    /// exactly the double-charge bug this method exists to prevent, so the key is pinned once
    /// before the first attempt and written onto the request.
    /// Refusals and authentication failures are thrown immediately: they will not change.
    /// </remarks>
    /// <param name="request">The session parameters.</param>
    /// <param name="options">Attempts and backoff. Defaults to 3 attempts, 500ms doubling.</param>
    /// <param name="cancellationToken">Cancels the call and the waits between attempts.</param>
    /// <returns>The created session.</returns>
    public async Task<CheckoutSession> CreateCheckoutSessionWithRetryAsync(
        CheckoutSessionRequest request,
        RetryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new RetryOptions();

        if (options.Attempts < 1)
        {
            throw new DominaiteValidationException("Attempts must be at least 1");
        }

        // Pinned once, up front, and written back onto the request so the caller can see it.
        request.IdempotencyKey = ResolveIdempotencyKey(request);

        DominaiteException? lastError = null;
        for (var attempt = 0; attempt < options.Attempts; attempt++)
        {
            try
            {
                return await this.CreateCheckoutSessionAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (DominaiteException error) when (error.IsRetryable)
            {
                lastError = error;
                if (attempt + 1 < options.Attempts)
                {
                    var delay = options.BaseDelay * Math.Pow(2, Math.Min(attempt, 16));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw lastError ?? new DominaiteTransportException("Retrying gave up.");
    }

    /// <summary>
    /// Reads the payment status of one of your checkout sessions.
    /// </summary>
    /// <remarks>
    /// Decide "paid" with <see cref="CheckoutStatus.IsPaid"/> - <c>succeeded</c> is the only value
    /// that means the customer paid. Poll after the payer returns to you, or on your order
    /// timeout, not in a tight loop: the endpoint is rate limited per key.
    /// </remarks>
    /// <param name="transactionId">The id <see cref="CreateCheckoutSessionAsync"/> returned.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The status projection.</returns>
    /// <exception cref="DominaiteApiException">HTTP 404 for an unknown or foreign transaction id.</exception>
    public async Task<CheckoutStatus> GetStatusAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactionId);

        var normalized = transactionId.Trim().ToLowerInvariant();
        if (!Guid.TryParseExact(normalized, "D", out _))
        {
            throw new DominaiteValidationException(
                "transactionId must be the UUID returned by CreateCheckoutSessionAsync");
        }

        // GET signs an EMPTY idempotency key and an EMPTY body, and sends no Idempotency-Key
        // header.
        var path = $"{SessionsPath}/{normalized}";
        var payload = await this.SendAsync(HttpMethod.Get, path, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);

        var status = Deserialize<CheckoutStatus>(payload, "status");
        status.Raw = payload;
        return status;
    }

    /// <summary>Releases the HttpClient this instance created for itself.</summary>
    public void Dispose()
    {
        if (this._ownsHttpClient)
        {
            this._http.Dispose();
        }
    }

    /// <summary>
    /// A description with the secret redacted. Never returns the secret, whatever it is called
    /// from.
    /// </summary>
    /// <returns>The redacted description.</returns>
    public override string ToString()
        => $"DominaiteClient {{ KeyId = {this._keyId}, BaseUrl = {this.BaseUrl}, Secret = [REDACTED] }}";

    /// <summary>
    /// Signs and sends one call, and maps the response onto the error taxonomy. The body and the
    /// idempotency key are both empty for GET.
    /// </summary>
    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        string body,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = RequestSigner.Sign(new SignatureInput
        {
            Secret = this._secret,
            Timestamp = timestamp,
            Method = method.Method,

            // The signed path is the canonical path only. The base URL's own prefix (dev's /api,
            // prod's /payments) is NOT part of it.
            Path = path,
            IdempotencyKey = idempotencyKey,
            Body = body,
        });

        using var message = new HttpRequestMessage(method, this.BaseUrl + path);

        // Some edges block requests without a real User-Agent, so always send one.
        message.Headers.TryAddWithoutValidation("User-Agent", this.UserAgent);
        message.Headers.TryAddWithoutValidation("X-Api-Key-Id", this._keyId);
        message.Headers.TryAddWithoutValidation("X-Timestamp", timestamp);
        message.Headers.TryAddWithoutValidation("X-Signature", signature);

        // No Idempotency-Key header on GET, matching the empty key it signed.
        if (idempotencyKey.Length > 0)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        if (method != HttpMethod.Get)
        {
            // The exact bytes that were hashed above.
            message.Content = new StringContent(body, new UTF8Encoding(false));
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        HttpResponseMessage response;
        try
        {
            response = await this._http.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException error)
        {
            // Not the caller's token, so this is the HttpClient timeout.
            throw new DominaiteTransportException(
                "The Dominaite API did not answer in time; retry with the same idempotency key.",
                innerException: error);
        }
        catch (HttpRequestException error)
        {
            throw new DominaiteTransportException(
                $"Could not reach the Dominaite API: {error.Message}",
                innerException: error);
        }

        using (response)
        {
            var status = (int)response.StatusCode;

            // A 3xx is not a protocol detail here, it is a red flag: the API never redirects, and
            // the handler did not follow it. Hard error, never retried.
            if (status is >= 300 and < 400)
            {
                throw new DominaiteApiException(
                    status,
                    null,
                    "The Dominaite API returned an unexpected redirect response; the Dominaite API never redirects.");
            }

            // Branch on the status BEFORE parsing: a 5xx from a proxy is often an HTML error page
            // or an empty body, and that is still a retryable transport failure rather than a
            // parse error.
            if (status >= 500)
            {
                throw new DominaiteTransportException(
                    $"The Dominaite API is unavailable (HTTP {status}); retry with the same idempotency key.",
                    status);
            }

            string raw;
            try
            {
                raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException error)
            {
                throw new DominaiteTransportException(
                    $"Could not read the Dominaite API response: {error.Message}",
                    innerException: error);
            }

            return UnwrapEnvelope(status, raw);
        }
    }

    /// <summary>
    /// Unwraps the gateway envelope and turns a non-2xx into the right error.
    /// </summary>
    /// <remarks>
    /// Three shapes reach here and only one of them is nested: create answers
    /// <c>{ success, data: { success, checkout } }</c>, while the status and ping reads answer
    /// <c>{ success, data: { ...fields } }</c> with no inner <c>success</c>. So this only ever
    /// unwraps <c>data</c>; branching on the inner <c>success</c> belongs to the caller that knows
    /// which shape it asked for. Treating a missing <c>success</c> as false would mark every paid
    /// order unpaid.
    /// </remarks>
    private static JsonElement UnwrapEnvelope(int httpStatus, string raw)
    {
        JsonElement envelope;
        try
        {
            using var document = JsonDocument.Parse(raw);
            envelope = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new DominaiteApiException(httpStatus, null, "The Dominaite API returned a non-JSON response.");
        }

        if (envelope.ValueKind != JsonValueKind.Object)
        {
            throw new DominaiteApiException(httpStatus, null, "The Dominaite API returned a non-JSON response.");
        }

        var payload = envelope.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : envelope;

        if (httpStatus < 400)
        {
            return payload;
        }

        var code = StringField(payload, "errorCode")
            ?? (envelope.TryGetProperty("error", out var error) ? StringField(error, "code") : null);
        var message = StringField(payload, "errorMessage")
            ?? (envelope.TryGetProperty("error", out var errorDetail) ? StringField(errorDetail, "message") : null);

        if (httpStatus is 401 or 403)
        {
            throw new DominaiteAuthException(
                code ?? "UNAUTHORIZED",
                message ?? "Authentication failed - check your key id, secret, and server clock.",
                httpStatus);
        }

        // The code is the whole point of a validation rejection (IDEMPOTENCY_KEY_REQUIRED on a
        // 400), so it travels with the error instead of being flattened into a status the caller
        // cannot branch on.
        throw new DominaiteApiException(httpStatus, code, message ?? "Request rejected");
    }

    /// <summary>
    /// Validates the request and returns the exact body bytes that get both signed and sent.
    /// Serializing once is the point: hashing a second serialization would let key ordering or
    /// escaping drift between the signature and the wire.
    /// </summary>
    private static string SerializeBody(CheckoutSessionRequest request)
    {
        if (request.Amount <= 0)
        {
            throw new DominaiteValidationException(
                "Amount must be a positive integer in MINOR units (e.g. 2500 for 25.00 EUR)");
        }

        if (string.IsNullOrWhiteSpace(request.Currency))
        {
            throw new DominaiteValidationException("Missing required parameter: Currency");
        }

        if (string.IsNullOrWhiteSpace(request.OrderReference))
        {
            throw new DominaiteValidationException("Missing required parameter: OrderReference");
        }

        if (request.OrderReference.Length > 100)
        {
            throw new DominaiteValidationException("OrderReference must be at most 100 characters");
        }

        var node = JsonSerializer.SerializeToNode(request, JsonOptions)!.AsObject();
        foreach (var extra in request.Extra)
        {
            if (!node.ContainsKey(extra.Key))
            {
                node[extra.Key] = JsonSerializer.SerializeToNode(extra.Value, JsonOptions);
            }
        }

        return node.ToJsonString(JsonOptions);
    }

    private static string ResolveIdempotencyKey(CheckoutSessionRequest request)
    {
        if (request.IdempotencyKey is null)
        {
            var generated = NewIdempotencyKey();
            request.IdempotencyKey = generated;
            return generated;
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new DominaiteValidationException("IdempotencyKey must not be empty");
        }

        if (request.IdempotencyKey.Length > 100)
        {
            throw new DominaiteValidationException("IdempotencyKey must be at most 100 characters");
        }

        return request.IdempotencyKey;
    }

    private static T Deserialize<T>(JsonElement payload, string what)
    {
        try
        {
            var parsed = payload.Deserialize<T>(JsonOptions);
            if (parsed is null)
            {
                throw new DominaiteApiException(200, null, $"The Dominaite API returned an unexpected {what} response.");
            }

            return parsed;
        }
        catch (JsonException)
        {
            throw new DominaiteApiException(200, null, $"The Dominaite API returned an unexpected {what} response.");
        }
    }

    private static string? StringField(JsonElement value, string field)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty(field, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = property.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
