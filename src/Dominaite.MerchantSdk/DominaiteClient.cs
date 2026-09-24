using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// The stored payment methods path. POST <c>PaymentMethodsPath/{id}/charges</c> charges a
    /// stored card; DELETE <c>PaymentMethodsPath/{id}</c> revokes it.
    /// </summary>
    public const string PaymentMethodsPath = "/merchant-api/payment-methods";

    /// <summary>This SDK's version, reported in the User-Agent.</summary>
    public const string Version = "0.3.0";

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

    /// <summary>
    /// A payment method id is one path segment and nothing else: it is interpolated into the
    /// signed path, so anything that could split, escape or extend that path is refused before
    /// signing.
    /// </summary>
    private static readonly Regex PaymentMethodIdPattern = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);

    /// <summary>What a 204 unwraps to: a payload with nothing in it.</summary>
    private static readonly JsonElement EmptyPayload = ParseEmptyObject();

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
    /// Verifies your credentials, your signing, and your clock without creating anything. Make
    /// this your first live call: a 401 here means the key id, the secret, or the signing, and a
    /// 503 means retry later - never both at once.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The ping response. Check <see cref="PingResponse.ClockSkewSeconds"/>.</returns>
    public async Task<PingResponse> PingAsync(CancellationToken cancellationToken = default)
    {
        // GET signs an EMPTY idempotency key and an EMPTY body.
        var payload = await this.RequestAsync(HttpMethod.Get, PingPath, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);

        var ping = Deserialize<PingResponse>(payload, "ping");
        ping.Raw = payload;
        return ping;
    }

    /// <summary>
    /// Opens a hosted checkout session for one payment.
    /// </summary>
    /// <param name="request">
    /// The session parameters. <see cref="CheckoutSessionRequest.IdempotencyKey"/> is required:
    /// derive it from the order with <see cref="IdempotencyKeys.ForOrder"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The created session. Hand its cashier values to the page that renders the widget.</returns>
    /// <exception cref="DominaiteValidationException">Bad arguments or a missing idempotency key; nothing was sent.</exception>
    /// <exception cref="DominaiteRefusalException">The gateway refused the session; inspect Code.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaiteApiException">An unexpected or rejecting response.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry with the same key.</exception>
    public async Task<CheckoutSession> CreateCheckoutSessionAsync(
        CheckoutSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var idempotencyKey = IdempotencyKeys.Validate(request.IdempotencyKey);
        var body = SerializeBody(request);

        JsonElement payload;
        try
        {
            payload = await this.RequestAsync(HttpMethod.Post, SessionsPath, body, idempotencyKey, cancellationToken)
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
    /// read back with <see cref="GetStatusAsync"/>. A fresh key per attempt would be exactly the
    /// double-charge bug this method exists to prevent, so every attempt sends the request's own
    /// key, which is required.
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

        // Checked once, up front, so a missing key fails before the first attempt.
        IdempotencyKeys.Validate(request.IdempotencyKey);

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
    /// <remarks>
    /// <see cref="CheckoutStatus.StoredPaymentMethod"/> is the card kept on file when the session
    /// asked for one with <see cref="CheckoutSessionRequest.SaveCard"/> and the payment was
    /// approved; null otherwise.
    /// </remarks>
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
        var payload = await this.RequestAsync(HttpMethod.Get, path, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);

        var status = Deserialize<CheckoutStatus>(payload, "status");
        status.Raw = payload;
        return status;
    }

    /// <summary>
    /// Charges a stored card off-session: no widget, no payer present.
    /// </summary>
    /// <remarks>
    /// Returns the charge on HTTP 201 (200 on a durable replay of the same key) and on HTTP 402
    /// alike. A decline is a result, not an exception: the 402 charge has
    /// <see cref="ChargeStatuses.Failed"/> and a <see cref="PaymentMethodCharge.DeclineClass"/> to
    /// branch on. <see cref="ChargeStatuses.Pending"/> is not terminal: poll
    /// <see cref="GetStatusAsync"/> with the charge's transaction id. What throws
    /// <see cref="DominaiteChargeException"/> is the gateway answering with a code instead of a
    /// charge (409, 422, 502, 503); <c>CHARGE_OUTCOME_UNKNOWN</c> carries the charge row to poll
    /// and must never be retried under a new key.
    /// </remarks>
    /// <param name="paymentMethodId">The <see cref="StoredPaymentMethod.Id"/> read off a paid session's status.</param>
    /// <param name="request">
    /// The charge parameters. <see cref="ChargeRequest.IdempotencyKey"/> is required: derive it
    /// from the order with <see cref="IdempotencyKeys.ForOrder"/>.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The charge result. Check <see cref="PaymentMethodCharge.IsPaid"/>.</returns>
    /// <exception cref="DominaiteValidationException">Bad arguments or a missing idempotency key; nothing was sent.</exception>
    /// <exception cref="DominaiteChargeException">The gateway answered with an error code; inspect Code.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaiteApiException">404 (PAYMENT_METHOD_NOT_FOUND) for a method that is not yours, a 400 validation rejection, or an unexpected response.</exception>
    /// <exception cref="DominaiteTransportException">Network failure, or a 5xx without a gateway code; retry with the same key.</exception>
    public async Task<PaymentMethodCharge> ChargePaymentMethodAsync(
        string paymentMethodId,
        ChargeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentMethodId);
        ArgumentNullException.ThrowIfNull(request);

        var id = NormalizePaymentMethodId(paymentMethodId);
        var idempotencyKey = IdempotencyKeys.Validate(request.IdempotencyKey);
        var body = SerializeBody(request);
        var path = $"{PaymentMethodsPath}/{id}/charges";

        ApiReply reply;
        try
        {
            reply = await this.SendAsync(HttpMethod.Post, path, body, idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DominaiteException error)
        {
            error.IdempotencyKey = idempotencyKey;
            throw;
        }

        // 201 (200 on a durable replay): the charge was placed, whatever its status. 402: the
        // provider declined; the envelope says success=false but the charge is right there,
        // status failed with its decline class, so it is a result, not an exception.
        var charge = reply.Charge();
        if (charge is not null && (reply.Success || reply.Status == 402))
        {
            return charge;
        }

        if (reply.Status >= 400)
        {
            var code = reply.ErrorCode;
            if (code is not null && !IsGenericFailureStatus(reply.Status))
            {
                throw new DominaiteChargeException(
                    reply.Status,
                    code,
                    reply.ErrorMessage ?? "The charge was refused.",
                    charge,
                    reply.Envelope)
                {
                    IdempotencyKey = idempotencyKey,
                };
            }

            var rejection = reply.Rejection();
            rejection.IdempotencyKey = idempotencyKey;
            throw rejection;
        }

        throw new DominaiteApiException(reply.Status, null, "The Dominaite API answered the charge without a charge body.")
        {
            IdempotencyKey = idempotencyKey,
        };
    }

    /// <summary>
    /// Revokes a stored card: the saved credential is deleted at the payment provider and the
    /// method's status becomes <see cref="StoredPaymentMethodStatuses.Revoked"/>. Any later charge
    /// on it is refused with <c>PAYMENT_METHOD_NOT_ACTIVE</c>.
    /// </summary>
    /// <remarks>
    /// A signed DELETE with an empty idempotency key and an empty body, the same recipe as GET.
    /// Resolves on HTTP 204, and again on an already revoked method, so retrying a timed-out
    /// revoke is safe. When the gateway refuses, nothing changed and the
    /// <see cref="DominaiteRevokeException"/> says why.
    /// </remarks>
    /// <param name="paymentMethodId">The <see cref="StoredPaymentMethod.Id"/> to revoke.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once the method is revoked.</returns>
    /// <exception cref="DominaiteValidationException">The id is not one path segment; nothing was sent.</exception>
    /// <exception cref="DominaiteRevokeException">502 UPSTREAM_CONTRACT_ERROR or 503 MERCHANT_API_UNAVAILABLE; nothing changed.</exception>
    /// <exception cref="DominaiteApiException">HTTP 404 (VALIDATION_ERROR) for an unknown or foreign payment method id.</exception>
    /// <exception cref="DominaiteTransportException">Network failure, or a 5xx without a gateway code.</exception>
    public async Task RevokePaymentMethodAsync(
        string paymentMethodId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paymentMethodId);

        var id = NormalizePaymentMethodId(paymentMethodId);
        var path = $"{PaymentMethodsPath}/{id}";

        // DELETE signs an EMPTY idempotency key and an EMPTY body, and sends no Idempotency-Key
        // header, exactly like GET.
        var reply = await this.SendAsync(HttpMethod.Delete, path, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        if (reply.Status < 400)
        {
            return;
        }

        var code = reply.ErrorCode;
        if (code is not null && !IsGenericFailureStatus(reply.Status))
        {
            throw new DominaiteRevokeException(
                reply.Status,
                code,
                reply.ErrorMessage ?? "The revoke was refused.",
                reply.Envelope);
        }

        throw reply.Rejection();
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
    /// Signs and sends one call, and maps the response onto the error taxonomy:
    /// <see cref="SendAsync"/> plus the generic rejection for any 4xx or 5xx. The unwrapped
    /// <c>data</c> comes back on success (an empty object for a 204). The body and the idempotency
    /// key are both empty for GET and DELETE.
    /// </summary>
    private async Task<JsonElement> RequestAsync(
        HttpMethod method,
        string path,
        string body,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var reply = await this.SendAsync(method, path, body, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (reply.Status >= 400)
        {
            throw reply.Rejection();
        }

        return reply.Payload;
    }

    /// <summary>
    /// Signs and sends one call, and parses whatever came back into an <see cref="ApiReply"/>.
    /// </summary>
    /// <remarks>
    /// Only what no route can use is thrown here: transport failures, a redirect, a 401/403, and
    /// a body that is not a JSON object (a 5xx of that kind is a retryable transport failure;
    /// anything else is an API error). Every other status comes back as a reply, so the payment
    /// method routes can read a 402 decline or a coded 502 as the typed answers they are, while
    /// <see cref="RequestAsync"/> rejects them generically.
    /// </remarks>
    private async Task<ApiReply> SendAsync(
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

        // No Idempotency-Key header on GET or DELETE, matching the empty key they signed.
        if (idempotencyKey.Length > 0)
        {
            message.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        // GET and DELETE signed an empty body and send none.
        if (body.Length > 0)
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

            // A revoke answers 204 with no body, and no body is not a parse failure.
            if (status == (int)HttpStatusCode.NoContent)
            {
                return new ApiReply(status, EmptyPayload);
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

            var reply = new ApiReply(status, ParseEnvelope(status, raw));

            // Credentials are refused the same way on every route.
            if (status is 401 or 403)
            {
                throw reply.Rejection();
            }

            return reply;
        }
    }

    /// <summary>
    /// Parses the body into the envelope object, classifying a body that is not one on the STATUS.
    /// </summary>
    /// <remarks>
    /// A 502/503/504 of that kind comes from a load balancer or a cold function host, not from the
    /// API, so the body is an HTML error page or empty; reading it as "a non-JSON response" would
    /// make a non-retryable API error out of what is plainly a retryable outage. A JSON 5xx is
    /// different: that is the gateway itself talking, and the payment method routes need its code.
    /// </remarks>
    private static JsonElement ParseEnvelope(int httpStatus, string raw)
    {
        JsonElement envelope = default;
        var isObject = false;
        try
        {
            using var document = JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                envelope = document.RootElement.Clone();
                isObject = true;
            }
        }
        catch (JsonException)
        {
            // Classified below on the status, like a body of the wrong kind.
        }

        if (isObject)
        {
            return envelope;
        }

        if (httpStatus >= 500)
        {
            throw new DominaiteTransportException(
                $"The Dominaite API is unavailable (HTTP {httpStatus}); retry with the same idempotency key.",
                httpStatus);
        }

        throw new DominaiteApiException(httpStatus, null, "The Dominaite API returned a non-JSON response.");
    }

    /// <summary>
    /// The statuses that stay generic on every route: input validation, credentials, an id that
    /// is not yours, and rate limiting. A coded answer outside this set is the gateway describing
    /// a payment method outcome, which the charge and revoke routes surface as
    /// <see cref="DominaiteChargeException"/> and <see cref="DominaiteRevokeException"/>.
    /// </summary>
    private static bool IsGenericFailureStatus(int status) => status is 400 or 401 or 403 or 404 or 429;

    /// <summary>
    /// One parsed gateway answer: the HTTP status and the envelope as sent.
    /// </summary>
    /// <remarks>
    /// Three shapes arrive and only one of them is nested: create answers
    /// <c>{ success, data: { success, checkout } }</c>, while the status and ping reads answer
    /// <c>{ success, data: { ...fields } }</c> with no inner <c>success</c>. So <see cref="Payload"/>
    /// only ever unwraps <c>data</c>; branching on the inner <c>success</c> belongs to the caller
    /// that knows which shape it asked for. Treating a missing <c>success</c> as false would mark
    /// every paid order unpaid.
    /// </remarks>
    private readonly struct ApiReply
    {
        public ApiReply(int status, JsonElement envelope)
        {
            this.Status = status;
            this.Envelope = envelope;
        }

        public int Status { get; }

        /// <summary>The whole body: <c>{ success, data?, error?, metadata }</c>.</summary>
        public JsonElement Envelope { get; }

        /// <summary>The envelope's own success flag, true only when it is literally true.</summary>
        public bool Success
            => this.Envelope.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True;

        /// <summary>
        /// <c>data</c> when it is an object, else null: the gateway omits null fields on the wire,
        /// so a bodiless error has no <c>data</c> at all.
        /// </summary>
        public JsonElement? Data
            => this.Envelope.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
                ? data
                : null;

        /// <summary>The unwrapped <c>data</c>, or the envelope itself when there is none.</summary>
        public JsonElement Payload => this.Data ?? this.Envelope;

        /// <summary>
        /// The gateway's machine-readable code: <c>error.code</c> on the standard envelope, or
        /// <c>errorCode</c> inside <c>data</c> on the create route's refusals.
        /// </summary>
        public string? ErrorCode
            => (this.Envelope.TryGetProperty("error", out var error) ? StringField(error, "code") : null)
                ?? StringField(this.Payload, "errorCode");

        public string? ErrorMessage
            => (this.Envelope.TryGetProperty("error", out var error) ? StringField(error, "message") : null)
                ?? StringField(this.Payload, "errorMessage");

        /// <summary>
        /// The charge row in <c>data</c>, on a 201, a 402 and the coded 502s that attach one. Null
        /// when <c>data</c> is missing or carries no charge id.
        /// </summary>
        public PaymentMethodCharge? Charge()
        {
            if (this.Data is not { } data || StringField(data, "chargeId") is null)
            {
                return null;
            }

            var charge = Deserialize<PaymentMethodCharge>(data, "charge");
            charge.Raw = data;
            return charge;
        }

        /// <summary>
        /// The generic error for a 4xx or 5xx: a 5xx is a retryable transport failure, a 401/403
        /// an auth failure, and any other 4xx a <see cref="DominaiteApiException"/> that keeps the
        /// code (IDEMPOTENCY_KEY_REQUIRED on a 400 is the whole point of the rejection).
        /// </summary>
        public DominaiteException Rejection()
        {
            if (this.Status >= 500)
            {
                return new DominaiteTransportException(
                    $"The Dominaite API is unavailable (HTTP {this.Status}); retry with the same idempotency key.",
                    this.Status);
            }

            var code = this.ErrorCode;
            var message = this.ErrorMessage;
            if (this.Status is 401 or 403)
            {
                return new DominaiteAuthException(
                    code ?? "UNAUTHORIZED",
                    message ?? "Authentication failed - check your key id, secret, and server clock.",
                    this.Status);
            }

            return new DominaiteApiException(this.Status, code, message ?? "Request rejected");
        }
    }

    /// <summary>
    /// Validates the request and returns the exact body bytes that get both signed and sent.
    /// Serializing once is the point: hashing a second serialization would let key ordering or
    /// escaping drift between the signature and the wire.
    /// </summary>
    private static string SerializeBody(CheckoutSessionRequest request)
    {
        ValidateMoneyParams(request.Amount, request.Currency, request.OrderReference);

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

    /// <summary>
    /// Validates a charge and returns the exact body bytes that get both signed and sent, in
    /// contract order: amount, currency, orderReference, then description when there is one.
    /// </summary>
    private static string SerializeBody(ChargeRequest request)
    {
        ValidateMoneyParams(request.Amount, request.Currency, request.OrderReference);
        return JsonSerializer.Serialize(request, JsonOptions);
    }

    private static void ValidateMoneyParams(long amount, string? currency, string? orderReference)
    {
        if (amount <= 0)
        {
            throw new DominaiteValidationException(
                "Amount must be a positive integer in MINOR units (e.g. 2500 for 25.00 EUR)");
        }

        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new DominaiteValidationException("Missing required parameter: Currency");
        }

        if (string.IsNullOrWhiteSpace(orderReference))
        {
            throw new DominaiteValidationException("Missing required parameter: OrderReference");
        }

        if (orderReference.Length > 100)
        {
            throw new DominaiteValidationException("OrderReference must be at most 100 characters");
        }
    }

    private static string NormalizePaymentMethodId(string paymentMethodId)
    {
        var trimmed = paymentMethodId.Trim();
        if (!PaymentMethodIdPattern.IsMatch(trimmed))
        {
            throw new DominaiteValidationException(
                "paymentMethodId must be the StoredPaymentMethod id from GetStatusAsync (letters, digits, _ or -, at most 100 characters)");
        }

        return trimmed;
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

    private static JsonElement ParseEmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
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
