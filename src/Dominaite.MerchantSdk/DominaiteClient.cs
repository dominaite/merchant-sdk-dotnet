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

    /// <summary>The public integration contract. The API answers it without a signature.</summary>
    public const string IntegrationContractPath = "/merchant-api/integration/contract";

    /// <summary>
    /// The stored payment methods path. POST <c>PaymentMethodsPath/{id}/charges</c> charges a
    /// stored card; DELETE <c>PaymentMethodsPath/{id}</c> revokes it.
    /// </summary>
    public const string PaymentMethodsPath = "/merchant-api/payment-methods";

    /// <summary>
    /// The payments path. POST <c>PaymentsPath/{transactionId}/refunds</c> refunds a payment; GET
    /// <c>PaymentsPath/{transactionId}/refunds</c> lists its refunds, and GET
    /// <c>PaymentsPath/{transactionId}/refunds/{refundId}</c> reads one refund.
    /// </summary>
    public const string PaymentsPath = "/merchant-api/payments";

    /// <summary>
    /// The subscription plans path. POST creates a plan and GET lists them; GET, PATCH and DELETE
    /// <c>PlansPath/{planId}</c> read, update and archive one plan.
    /// </summary>
    public const string PlansPath = "/merchant-api/plans";

    /// <summary>
    /// The subscription agreements path. POST creates an agreement; GET and DELETE
    /// <c>AgreementsPath/{agreementId}</c> read and cancel one.
    /// </summary>
    public const string AgreementsPath = "/merchant-api/agreements";

    /// <summary>
    /// The webhook endpoints path. POST registers an endpoint and GET lists them; DELETE
    /// <c>WebhooksPath/{webhookId}</c> removes one.
    /// </summary>
    public const string WebhooksPath = "/merchant-api/webhooks";

    /// <summary>
    /// The transaction list path. GET lists your payments in a time window. The query parameters
    /// are not part of the signed path.
    /// </summary>
    public const string TransactionsPath = "/merchant-api/transactions";

    /// <summary>This SDK's version, reported in the User-Agent.</summary>
    public const string Version = "0.5.0";

    private const string KeyIdPrefix = "dmk_";
    private const string SecretPrefix = "dms_";

    /// <summary>
    /// The wire encoding. camelCase to match the gateway, nulls omitted, and the relaxed encoder
    /// so non-ASCII payer names travel as UTF-8 rather than <c>\uXXXX</c> escapes - which is what
    /// every other Dominaite SDK puts on the wire. Dates are written in UTC with a <c>Z</c>.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new UtcDateConverter() },
    };

    /// <summary>
    /// A payment method, refund, plan or agreement id is one path segment and nothing else: it is
    /// interpolated into the signed path, so anything that could split, escape or extend that path
    /// is refused before signing.
    /// </summary>
    private static readonly Regex IdSegmentPattern = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant);

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
    /// Reads the public integration contract of the API this client calls: its version, the status
    /// and event vocabularies, the consent wording versions, and everything else in
    /// <see cref="IntegrationContract.Raw"/>.
    /// </summary>
    /// <remarks>
    /// The route needs no credentials, so any key works. The SDK signs the call like every other
    /// one, and the API does not check the signature on this route.
    /// </remarks>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The contract.</returns>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">An unexpected response.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task<IntegrationContract> GetIntegrationContractAsync(CancellationToken cancellationToken = default)
    {
        // GET signs an EMPTY idempotency key and an EMPTY body.
        var reply = await this.SendAsync(HttpMethod.Get, IntegrationContractPath, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        var data = reply.ReadObject("integration contract");
        var contract = Deserialize<IntegrationContract>(data, "integration contract");
        contract.Raw = data;
        return contract;
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
    /// <exception cref="DominaiteStorefrontException">The storefront is not whitelisted, inactive, or not the key's; inspect Code.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the payments scope, or verify-only sessions are not on for your account.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then retry with the same idempotency key.</exception>
    /// <exception cref="DominaiteApiException">An unexpected or rejecting response.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry with the same key.</exception>
    public async Task<CheckoutSession> CreateCheckoutSessionAsync(
        CheckoutSessionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var idempotencyKey = IdempotencyKeys.Validate(request.IdempotencyKey);
        var body = SerializeBody(request);

        ApiReply reply;
        try
        {
            reply = await this.SendAsync(HttpMethod.Post, SessionsPath, body, idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DominaiteException error)
        {
            error.IdempotencyKey = idempotencyKey;
            throw;
        }

        // The storefront codes arrive as a real HTTP 409 or 400, and a 400 would otherwise read as
        // one more validation error. They are configuration, not input, so they get their own type.
        if (reply.Status >= 400)
        {
            var rejection = reply.Status < 500 && reply.StorefrontRefusal() is { } storefront
                ? storefront
                : reply.Rejection();
            rejection.IdempotencyKey = idempotencyKey;
            throw rejection;
        }

        var payload = reply.Payload;

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

        // A storefront mismatch on an idempotent replay comes back as a 200 refusal; it is still
        // the same storefront problem, so it is still the same exception.
        if (reply.StorefrontRefusal() is { } replayStorefront)
        {
            replayStorefront.IdempotencyKey = idempotencyKey;
            throw replayStorefront;
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
    /// Creates a session, retrying transport failures and <c>PAYMENT_PROCESSING_UNAVAILABLE</c>,
    /// with THE SAME idempotency key across every attempt.
    /// </summary>
    /// <remarks>
    /// Reusing the key is what makes the retry safe: a transport failure leaves you not knowing
    /// whether the request landed, and a retried key never opens a second payment. If the first
    /// attempt did land and its session is still open, the retry returns that ORIGINAL session.
    /// If it has moved on, the retry comes back as a replay refusal (<c>ALREADY_PROCESSED</c>,
    /// <c>PRIOR_ATTEMPT_FAILED</c>, <c>DUPLICATE_REQUEST</c>) naming that transaction, which you
    /// read back with <see cref="GetStatusAsync"/>. A fresh key per attempt would be exactly the
    /// double-charge bug this method exists to prevent, so every attempt sends the request's own
    /// key, which is required.
    /// <c>PAYMENT_PROCESSING_UNAVAILABLE</c> is retried too: it arrives as a refusal, but card
    /// payments come back on their own and nothing was created. Every other refusal, storefront
    /// refusals, and authentication and permission failures are thrown immediately: they will not
    /// change. A rate limit (HTTP 429) is thrown immediately too: wait
    /// <see cref="DominaiteRateLimitException.RetryAfter"/>, then call again with the same key.
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

        var normalized = NormalizeTransactionId(transactionId);

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
    /// <see cref="ChargeStatuses.Failed"/>. Its <see cref="PaymentMethodCharge.DeclineClass"/> can
    /// be null, for example when nobody declined the charge; the status read carries the current
    /// class.
    /// <see cref="ChargeStatuses.Pending"/> is not terminal: poll
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
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the scope this route needs.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then retry with the same idempotency key.</exception>
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
        // status failed (its decline class can be null), so it is a result, not an exception.
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

    /// <summary>
    /// Refunds a payment, in full or in part. The refund runs asynchronously: this returns once it
    /// is queued (HTTP 202), usually with status <c>pending</c>.
    /// </summary>
    /// <remarks>
    /// Read the outcome with <see cref="GetRefundAsync"/>, or wait for the <c>payment.refunded</c>
    /// webhook, which fires when the money has moved and carries this payment as
    /// <c>originalTransactionId</c>. A failed refund sends no webhook: poll if you need to know about
    /// failures. A replay of the same key returns the refund as it stands now, so it doubles as a
    /// status read. What throws <see cref="DominaiteRefundException"/> is the gateway answering with
    /// a code instead of a refund; <c>PAYMENT_NOT_REFUNDABLE</c> and <c>REFUND_AMOUNT_EXCEEDED</c>
    /// queue nothing and leave the key unused.
    /// </remarks>
    /// <param name="transactionId">The payment to refund: the id a checkout session or a charge returned.</param>
    /// <param name="request">
    /// The refund parameters. <see cref="RefundRequest.IdempotencyKey"/> is required: derive it from
    /// your own refund (return or credit-note id).
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The refund as queued.</returns>
    /// <exception cref="DominaiteValidationException">Bad arguments or a missing idempotency key; nothing was sent.</exception>
    /// <exception cref="DominaiteRefundException">The gateway answered with an error code; inspect Code.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the scope this route needs.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then retry with the same idempotency key.</exception>
    /// <exception cref="DominaiteApiException">An unexpected response.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; nothing was queued, retry with the same key.</exception>
    public async Task<Refund> CreateRefundAsync(
        string transactionId,
        RefundRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactionId);
        ArgumentNullException.ThrowIfNull(request);

        var id = NormalizeTransactionId(transactionId);
        var idempotencyKey = IdempotencyKeys.Validate(request.IdempotencyKey);
        var body = SerializeBody(request);
        var path = $"{PaymentsPath}/{id}/refunds";

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

        try
        {
            return reply.ReadRefund();
        }
        catch (DominaiteException error)
        {
            error.IdempotencyKey = idempotencyKey;
            throw;
        }
    }

    /// <summary>
    /// Reads one refund of a payment.
    /// </summary>
    /// <remarks>
    /// Poll it after <see cref="CreateRefundAsync"/> until <see cref="Refund.IsTerminal"/>. Right
    /// after the create call the refund may not have been picked up yet: a
    /// <see cref="DominaiteRefundException"/> with code <c>REFUND_NOT_FOUND</c> is retryable for up
    /// to 60 seconds, after that the id is unknown.
    /// </remarks>
    /// <param name="transactionId">The payment the refund belongs to.</param>
    /// <param name="refundId">The <see cref="Refund.RefundId"/> the create call returned.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The refund as it stands now.</returns>
    /// <exception cref="DominaiteValidationException">Bad arguments; nothing was sent.</exception>
    /// <exception cref="DominaiteRefundException">The gateway answered with an error code; inspect Code.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the scope this route needs.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then retry with the same idempotency key.</exception>
    /// <exception cref="DominaiteApiException">An unexpected response.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task<Refund> GetRefundAsync(
        string transactionId,
        string refundId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactionId);
        ArgumentNullException.ThrowIfNull(refundId);

        var path = $"{PaymentsPath}/{NormalizeTransactionId(transactionId)}/refunds/{NormalizeRefundId(refundId)}";

        // GET signs an EMPTY idempotency key and an EMPTY body, and sends no Idempotency-Key
        // header.
        var reply = await this.SendAsync(HttpMethod.Get, path, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return reply.ReadRefund();
    }

    /// <summary>
    /// Lists the refunds of one payment, newest first, with the amount refunded so far and the
    /// amount you can still refund.
    /// </summary>
    /// <remarks>
    /// Read <see cref="RefundList.RefundableAmount"/> before a partial refund. A payment without
    /// refunds gives an empty list.
    /// </remarks>
    /// <param name="transactionId">The payment: the id a checkout session or a charge returned.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The refunds and the two totals.</returns>
    /// <exception cref="DominaiteValidationException">Bad arguments; nothing was sent.</exception>
    /// <exception cref="DominaiteRefundException">The gateway answered with an error code, e.g. <c>PAYMENT_NOT_FOUND</c> for an unknown or foreign payment; inspect Code.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the scope this route needs.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">An unexpected response.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task<RefundList> ListRefundsAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactionId);

        var path = $"{PaymentsPath}/{NormalizeTransactionId(transactionId)}/refunds";

        // GET signs an EMPTY idempotency key and an EMPTY body, and sends no Idempotency-Key
        // header.
        var reply = await this.SendAsync(HttpMethod.Get, path, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return reply.ReadRefundList();
    }

    /// <summary>
    /// Creates a subscription plan: a price you bill every period. Subscribe a customer to it with
    /// an agreement.
    /// </summary>
    /// <remarks>
    /// Amount, currency and interval cannot change later: a new price is a new plan. A retry with
    /// the same key and the same body returns the plan made the first time.
    /// </remarks>
    /// <param name="request">
    /// The plan. <see cref="CreatePlanRequest.IdempotencyKey"/> is required: derive it from your own
    /// plan or product id.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan, with the <c>plan_</c> id the other plan routes take.</returns>
    /// <exception cref="DominaiteValidationException">Bad arguments or a missing idempotency key; nothing was sent.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>payments</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then retry with the same idempotency key.</exception>
    /// <exception cref="DominaiteApiException">HTTP 400 for a field the gateway refused (the field errors are in RawResult), or 422 <c>IDEMPOTENCY_KEY_REUSED</c> for a key first used for another plan.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry with the same key.</exception>
    public async Task<Plan> CreatePlanAsync(
        CreatePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var idempotencyKey = IdempotencyKeys.Validate(request.IdempotencyKey);
        var body = SerializeWithExtra(request, request.Extra);
        var data = await this.CreateAsync(PlansPath, body, idempotencyKey, "plan", cancellationToken)
            .ConfigureAwait(false);
        return ToPlan(data);
    }

    /// <summary>
    /// Lists your plans: the active ones first, then the archived ones, each group newest first.
    /// </summary>
    /// <remarks>The list has no pages. It holds at most 200 plans.</remarks>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plans. Empty when you have none.</returns>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>payments</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">An unexpected response.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task<IReadOnlyList<Plan>> ListPlansAsync(CancellationToken cancellationToken = default)
    {
        var reply = await this.SendAsync(HttpMethod.Get, PlansPath, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return [.. reply.ReadArray("plan list").EnumerateArray().Select(ToPlan)];
    }

    /// <summary>Reads one plan.</summary>
    /// <param name="planId">The <see cref="Plan.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan as it is now.</returns>
    /// <exception cref="DominaiteValidationException">The id is not one path segment; nothing was sent.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>payments</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">HTTP 404 <c>PLAN_NOT_FOUND</c> for an id that is not one of your plans, or 400 for an id that is not a plan id.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task<Plan> GetPlanAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planId);

        var path = $"{PlansPath}/{NormalizeHandle(planId, "planId must be the Plan.Id from CreatePlanAsync")}";
        var reply = await this.SendAsync(HttpMethod.Get, path, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return ToPlan(reply.ReadObject("plan"));
    }

    /// <summary>
    /// Changes the name, the trial or the cancel page of a plan. The price, the currency and the
    /// interval cannot change.
    /// </summary>
    /// <remarks>
    /// The PATCH sends only the fields you set and no idempotency key: sending the same change
    /// again gives the same plan. A new trial applies to agreements created from now on. A new
    /// cancel page shows on the customers' next emails.
    /// </remarks>
    /// <param name="planId">The <see cref="Plan.Id"/>.</param>
    /// <param name="request">The changes. Set at least one field.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The plan after the change.</returns>
    /// <exception cref="DominaiteValidationException">The id is not one path segment, or the request changes nothing; nothing was sent.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>payments</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">HTTP 404 <c>PLAN_NOT_FOUND</c>, or 400 for a field the gateway refused (the field errors are in RawResult).</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task<Plan> UpdatePlanAsync(
        string planId,
        UpdatePlanRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planId);
        ArgumentNullException.ThrowIfNull(request);

        var path = $"{PlansPath}/{NormalizeHandle(planId, "planId must be the Plan.Id from CreatePlanAsync")}";
        var body = SerializeWithExtra(request, request.Extra);
        if (body == "{}")
        {
            throw new DominaiteValidationException("Set at least one of Name, TrialDays or CancelUrl");
        }

        // The gateway takes no Idempotency-Key on PATCH, so the SDK signs an empty one and sends
        // no header.
        var reply = await this.SendAsync(HttpMethod.Patch, path, body, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return ToPlan(reply.ReadObject("plan"));
    }

    /// <summary>
    /// Archives a plan: it takes no new agreements. The agreements on it continue at their own
    /// price.
    /// </summary>
    /// <remarks>
    /// Resolves on HTTP 204, and again on a plan that is already archived, so a retry is safe. An
    /// archived plan stays readable with <see cref="GetPlanAsync"/>.
    /// </remarks>
    /// <param name="planId">The <see cref="Plan.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once the plan is archived.</returns>
    /// <exception cref="DominaiteValidationException">The id is not one path segment; nothing was sent.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>payments</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">HTTP 404 <c>PLAN_NOT_FOUND</c> for an id that is not one of your plans.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task DeletePlanAsync(
        string planId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(planId);

        var path = $"{PlansPath}/{NormalizeHandle(planId, "planId must be the Plan.Id from CreatePlanAsync")}";
        var reply = await this.SendAsync(HttpMethod.Delete, path, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        reply.ReadNothing();
    }

    /// <summary>
    /// Subscribes one of your customers to a plan. The agreement starts <c>pending</c> and nothing
    /// is charged.
    /// </summary>
    /// <remarks>
    /// Then create a checkout session with <see cref="CheckoutSessionRequest.AgreementId"/>: the
    /// paid session saves the card and activates the agreement. With scheduled charges on, the
    /// platform then charges each period. Follow the agreement with the <c>agreement.*</c> and
    /// <c>charge.*</c> webhooks.
    /// </remarks>
    /// <param name="request">
    /// The agreement. <see cref="CreateAgreementRequest.IdempotencyKey"/> is required: derive it from
    /// your own subscription id.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agreement, with the <c>agr_</c> id the session and the other agreement routes take.</returns>
    /// <exception cref="DominaiteValidationException">Bad arguments or a missing idempotency key; nothing was sent.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>payments</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then retry with the same idempotency key.</exception>
    /// <exception cref="DominaiteApiException">
    /// The gateway refused the agreement; inspect Code. HTTP 404 <c>PLAN_NOT_FOUND</c> or
    /// <c>PAYMENT_METHOD_NOT_FOUND</c>; 409 <c>PLAN_NOT_ACTIVE</c> or <c>PAYMENT_METHOD_NOT_ACTIVE</c>;
    /// 422 <c>AGREEMENT_CONSENT_REQUIRED</c>, <c>PAYMENT_METHOD_CURRENCY_MISMATCH</c>,
    /// <c>PAYMENT_METHOD_CURRENCY_UNKNOWN</c>, <c>MAX_AMOUNT_BELOW_PLAN_AMOUNT</c> or
    /// <c>IDEMPOTENCY_KEY_REUSED</c>; 400 for a field the gateway refused (the field errors are in
    /// RawResult).
    /// </exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry with the same key.</exception>
    public async Task<Agreement> CreateAgreementAsync(
        CreateAgreementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var idempotencyKey = IdempotencyKeys.Validate(request.IdempotencyKey);
        var body = SerializeWithExtra(request, request.Extra);
        var data = await this.CreateAsync(AgreementsPath, body, idempotencyKey, "agreement", cancellationToken)
            .ConfigureAwait(false);
        return ToAgreement(data);
    }

    /// <summary>Reads one agreement: its status, its next charge and why it is past due.</summary>
    /// <param name="agreementId">The <see cref="Agreement.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The agreement as it is now.</returns>
    /// <exception cref="DominaiteValidationException">The id is not one path segment; nothing was sent.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>payments</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">HTTP 404 <c>AGREEMENT_NOT_FOUND</c> for an id that is not one of your agreements, or 400 for an id that is not an agreement id.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task<Agreement> GetAgreementAsync(
        string agreementId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agreementId);

        var path = $"{AgreementsPath}/{NormalizeHandle(agreementId, "agreementId must be the Agreement.Id from CreateAgreementAsync")}";
        var reply = await this.SendAsync(HttpMethod.Get, path, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return ToAgreement(reply.ReadObject("agreement"));
    }

    /// <summary>Cancels an agreement: the platform charges it no more.</summary>
    /// <remarks>
    /// Resolves on HTTP 204, and again on an agreement that is already cancelled, so a retry is
    /// safe. An agreement that completed or failed cannot be cancelled:
    /// <c>AGREEMENT_ENDED</c>.
    /// </remarks>
    /// <param name="agreementId">The <see cref="Agreement.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once the agreement is cancelled.</returns>
    /// <exception cref="DominaiteValidationException">The id is not one path segment; nothing was sent.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>payments</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">HTTP 404 <c>AGREEMENT_NOT_FOUND</c>, or 409 <c>AGREEMENT_ENDED</c> for an agreement that completed or failed.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task CancelAgreementAsync(
        string agreementId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agreementId);

        var path = $"{AgreementsPath}/{NormalizeHandle(agreementId, "agreementId must be the Agreement.Id from CreateAgreementAsync")}";
        var reply = await this.SendAsync(HttpMethod.Delete, path, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        reply.ReadNothing();
    }

    /// <summary>
    /// Registers a webhook endpoint: a URL that receives the events you name. Needs an API key
    /// with the <c>webhooks:manage</c> scope.
    /// </summary>
    /// <remarks>
    /// Store <see cref="WebhookEndpoint.Secret"/> from this answer: no other answer has it. A
    /// retry with the same key returns the same endpoint without the secret. If you lost the
    /// secret, delete the endpoint and register it again with a new key. On
    /// <c>WEBHOOK_ALREADY_REGISTERED</c> the key was used for an endpoint that still exists, and
    /// its id is in <c>error.params.webhookId</c> of
    /// <see cref="DominaiteApiException.RawResult"/>.
    /// </remarks>
    /// <param name="request">
    /// The endpoint. <see cref="CreateWebhookEndpointRequest.IdempotencyKey"/> is required.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The endpoint with its signing secret.</returns>
    /// <exception cref="DominaiteValidationException">Bad arguments or a missing idempotency key; nothing was sent.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>webhooks:manage</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then retry with the same idempotency key.</exception>
    /// <exception cref="DominaiteApiException">
    /// The gateway refused the endpoint; inspect Code. HTTP 400 <c>WEBHOOK_URL_REJECTED</c>, or
    /// <c>VALIDATION_ERROR</c> for another field (the field errors are in RawResult); 409
    /// <c>WEBHOOK_LIMIT_REACHED</c>, <c>WEBHOOK_ALREADY_REGISTERED</c>, or
    /// <c>DUPLICATE_REQUEST</c> while the first call with this key still runs (retry shortly with
    /// the same key); 422 <c>IDEMPOTENCY_KEY_REUSED</c>.
    /// </exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry with the same key.</exception>
    public async Task<WebhookEndpoint> CreateWebhookEndpointAsync(
        CreateWebhookEndpointRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var idempotencyKey = IdempotencyKeys.Validate(request.IdempotencyKey);
        var body = SerializeWithExtra(request, request.Extra);
        var data = await this.CreateAsync(WebhooksPath, body, idempotencyKey, "webhook endpoint", cancellationToken)
            .ConfigureAwait(false);
        return ToWebhookEndpoint(data);
    }

    /// <summary>
    /// Lists your webhook endpoints, active and inactive, newest first. Needs an API key with the
    /// <c>webhooks:manage</c> scope.
    /// </summary>
    /// <remarks>The list has no pages, and it never has the signing secrets.</remarks>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The endpoints. Empty when you have none.</returns>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>webhooks:manage</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">An unexpected response.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task<IReadOnlyList<WebhookEndpoint>> ListWebhookEndpointsAsync(CancellationToken cancellationToken = default)
    {
        var reply = await this.SendAsync(HttpMethod.Get, WebhooksPath, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        return [.. reply.ReadArray("webhook endpoint list").EnumerateArray().Select(ToWebhookEndpoint)];
    }

    /// <summary>
    /// Removes a webhook endpoint: it receives no more deliveries. Needs an API key with the
    /// <c>webhooks:manage</c> scope.
    /// </summary>
    /// <remarks>
    /// A retry after a lost answer gets <c>WEBHOOK_NOT_FOUND</c> (HTTP 404): the endpoint is
    /// already gone.
    /// </remarks>
    /// <param name="webhookId">The <see cref="WebhookEndpoint.Id"/>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>A task that completes once the endpoint is removed.</returns>
    /// <exception cref="DominaiteValidationException">The id is not a UUID; nothing was sent.</exception>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>webhooks:manage</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">HTTP 404 <c>WEBHOOK_NOT_FOUND</c> for an id that is not one of your endpoints.</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task DeleteWebhookEndpointAsync(
        string webhookId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webhookId);

        var path = $"{WebhooksPath}/{NormalizeWebhookId(webhookId)}";
        var reply = await this.SendAsync(HttpMethod.Delete, path, string.Empty, string.Empty, cancellationToken)
            .ConfigureAwait(false);
        reply.ReadNothing();
    }

    /// <summary>
    /// Lists your online payments created in a time window, oldest first, one page at a time.
    /// Needs an API key with the <c>transactions:read</c> scope.
    /// </summary>
    /// <remarks>
    /// To read every page, call again with <see cref="ListTransactionsRequest.Cursor"/> set to the
    /// <see cref="TransactionPage.NextCursor"/> of the last page, until it is null. Keep
    /// <see cref="ListTransactionsRequest.From"/>, <see cref="ListTransactionsRequest.To"/>, the
    /// settlement window and <see cref="ListTransactionsRequest.Status"/> the same for every page. The parameters travel
    /// in the URL query; the signature covers the path without it.
    /// </remarks>
    /// <param name="request">The window, and optionally a status, a page size and a cursor.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>One page of payments.</returns>
    /// <exception cref="DominaiteAuthException">Wrong credentials, bad signature, clock off, IP not allowlisted.</exception>
    /// <exception cref="DominaitePermissionException">HTTP 403: the key does not have the <c>transactions:read</c> scope.</exception>
    /// <exception cref="DominaiteRateLimitException">HTTP 429: wait, then call again.</exception>
    /// <exception cref="DominaiteApiException">HTTP 400 for a parameter the gateway refused, for example a window longer than 31 days or a changed cursor (the field errors are in RawResult).</exception>
    /// <exception cref="DominaiteTransportException">Network failure or 5xx; retry.</exception>
    public async Task<TransactionPage> ListTransactionsAsync(
        ListTransactionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // GET signs the bare path with an EMPTY idempotency key and an EMPTY body. The query is
        // in the URL only.
        var reply = await this.SendRequestAsync(
                HttpMethod.Get,
                TransactionsPath,
                TransactionQuery(request),
                string.Empty,
                string.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        var data = reply.ReadObject("transaction page");
        var page = Deserialize<TransactionPage>(data, "transaction page");
        page.Raw = data;
        if (data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            page.Items = [.. items.EnumerateArray().Select(item =>
            {
                var row = Deserialize<TransactionItem>(item, "transaction");
                row.Raw = item;
                return row;
            })];
        }

        return page;
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
    /// Sends a create call with its idempotency key and returns the <c>data</c> object. Every error
    /// carries the key, so the caller can retry with it.
    /// </summary>
    private async Task<JsonElement> CreateAsync(
        string path,
        string body,
        string idempotencyKey,
        string what,
        CancellationToken cancellationToken)
    {
        try
        {
            var reply = await this.SendAsync(HttpMethod.Post, path, body, idempotencyKey, cancellationToken)
                .ConfigureAwait(false);
            return reply.ReadObject(what);
        }
        catch (DominaiteException error)
        {
            error.IdempotencyKey = idempotencyKey;
            throw;
        }
    }

    /// <summary>
    /// Signs and sends one call, and parses whatever came back into an <see cref="ApiReply"/>.
    /// </summary>
    /// <remarks>
    /// Only what no route can use is thrown here: transport failures, a redirect, a 401/403, a
    /// 429 with its <c>Retry-After</c>, and a body that is not a JSON object (a 5xx of that kind
    /// is a retryable transport failure; anything else is an API error). Every other status comes back as a reply, so the payment
    /// method routes can read a 402 decline or a coded 502 as the typed answers they are, while
    /// <see cref="RequestAsync"/> rejects them generically.
    /// </remarks>
    private Task<ApiReply> SendAsync(
        HttpMethod method,
        string path,
        string body,
        string idempotencyKey,
        CancellationToken cancellationToken)
        => this.SendRequestAsync(method, path, string.Empty, body, idempotencyKey, cancellationToken);

    /// <summary>
    /// What <see cref="SendAsync"/> does, with a query string. The query goes into the URL only:
    /// the signed path never has one.
    /// </summary>
    private async Task<ApiReply> SendRequestAsync(
        HttpMethod method,
        string path,
        string query,
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

        using var message = new HttpRequestMessage(
            method,
            query.Length > 0 ? $"{this.BaseUrl}{path}?{query}" : this.BaseUrl + path);

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

            // A 429 means the same thing whoever sent it, the API or an edge in front of it, so a
            // body that is not the envelope does not make it a different error.
            if (status == (int)HttpStatusCode.TooManyRequests)
            {
                var retryAfter = ReadRetryAfter(response.Headers.RetryAfter);
                throw new ApiReply(status, TryParseObject(raw) ?? EmptyPayload, retryAfter).Rejection();
            }

            var reply = new ApiReply(status, ParseEnvelope(status, raw));

            // Credentials and permissions are refused the same way on every route.
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
        if (TryParseObject(raw) is { } envelope)
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

    /// <summary>The body as a JSON object, or null when it is not one (not JSON, or another kind).</summary>
    private static JsonElement? TryParseObject(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The wait a <c>Retry-After</c> header asks for: seconds, or a date. Null when there is no
    /// such header or it cannot be read. A date in the past is no wait at all.
    /// </summary>
    private static TimeSpan? ReadRetryAfter(RetryConditionHeaderValue? header)
    {
        var wait = header?.Delta ?? (header?.Date is { } date ? (TimeSpan?)(date - DateTimeOffset.UtcNow) : null);
        return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
    }

    /// <summary>
    /// The statuses that stay generic on every route: input validation and an id that is not
    /// yours. A coded answer outside this set is the gateway describing a payment method outcome,
    /// which the charge and revoke routes surface as <see cref="DominaiteChargeException"/> and
    /// <see cref="DominaiteRevokeException"/>. Credentials (401), permissions (403) and limits (429)
    /// never get here: <see cref="SendAsync"/> throws them first.
    /// </summary>
    private static bool IsGenericFailureStatus(int status) => status is 400 or 404;

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
        public ApiReply(int status, JsonElement envelope, TimeSpan? retryAfter = null)
        {
            this.Status = status;
            this.Envelope = envelope;
            this.RetryAfter = retryAfter;
        }

        public int Status { get; }

        /// <summary>The wait from the <c>Retry-After</c> header; read on a 429 only.</summary>
        public TimeSpan? RetryAfter { get; }

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
        /// The <c>data</c> object on a success, or the generic rejection for a 4xx or 5xx.
        /// </summary>
        public JsonElement ReadObject(string what)
        {
            if (this.Status >= 400)
            {
                throw this.Rejection();
            }

            return this.Data
                ?? throw new DominaiteApiException(this.Status, null, $"The Dominaite API answered without a {what} body.", this.Envelope);
        }

        /// <summary>
        /// The <c>data</c> array on a success (the list routes that have no pages), or the generic
        /// rejection for a 4xx or 5xx.
        /// </summary>
        public JsonElement ReadArray(string what)
        {
            if (this.Status >= 400)
            {
                throw this.Rejection();
            }

            if (!this.Envelope.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                throw new DominaiteApiException(this.Status, null, $"The Dominaite API answered without a {what} body.", this.Envelope);
            }

            return data;
        }

        /// <summary>Nothing on a success (a 204), or the generic rejection for a 4xx or 5xx.</summary>
        public void ReadNothing()
        {
            if (this.Status >= 400)
            {
                throw this.Rejection();
            }
        }

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
        /// The refund in <c>data</c> on a success, or the error the answer stands for: a
        /// <see cref="DominaiteRefundException"/> for a coded 4xx, the generic rejection for
        /// everything else. Credentials, permissions and limits never get here.
        /// </summary>
        public Refund ReadRefund()
        {
            if (this.Status >= 400)
            {
                var code = this.ErrorCode;
                if (this.Status < 500 && code is not null)
                {
                    throw new DominaiteRefundException(
                        this.Status,
                        code,
                        this.ErrorMessage ?? "The refund was refused.",
                        this.Envelope);
                }

                throw this.Rejection();
            }

            if (this.Data is not { } data || StringField(data, "refundId") is null)
            {
                throw new DominaiteApiException(this.Status, null, "The Dominaite API answered the refund without a refund body.");
            }

            var refund = Deserialize<Refund>(data, "refund");
            refund.Raw = data;
            return refund;
        }

        /// <summary>
        /// The refund list in <c>data</c> on a success, each refund with its own Raw, or the error
        /// the answer stands for, mapped like <see cref="ReadRefund"/>.
        /// </summary>
        public RefundList ReadRefundList()
        {
            if (this.Status >= 400)
            {
                var code = this.ErrorCode;
                if (this.Status < 500 && code is not null)
                {
                    throw new DominaiteRefundException(
                        this.Status,
                        code,
                        this.ErrorMessage ?? "The refund list was refused.",
                        this.Envelope);
                }

                throw this.Rejection();
            }

            if (this.Data is not { } data
                || !data.TryGetProperty("refunds", out var refunds)
                || refunds.ValueKind != JsonValueKind.Array)
            {
                throw new DominaiteApiException(this.Status, null, "The Dominaite API answered the refund list without a refund list body.");
            }

            var list = Deserialize<RefundList>(data, "refund list");
            list.Raw = data;
            list.Refunds = [.. refunds.EnumerateArray().Select(item =>
            {
                var refund = Deserialize<Refund>(item, "refund");
                refund.Raw = item;
                return refund;
            })];
            return list;
        }

        /// <summary>
        /// A <see cref="DominaiteStorefrontException"/> when the answer carries one of the
        /// <see cref="ErrorCodes.Storefront"/> codes, else null.
        /// </summary>
        public DominaiteStorefrontException? StorefrontRefusal()
        {
            var code = this.ErrorCode;
            if (code is null || !ErrorCodes.Storefront.Contains(code, StringComparer.Ordinal))
            {
                return null;
            }

            return new DominaiteStorefrontException(
                this.Status,
                code,
                this.ErrorMessage ?? "The storefront refused the checkout session.",
                StringField(this.Payload, "transactionId"),
                this.Envelope);
        }

        /// <summary>
        /// The generic error for a 4xx or 5xx: a 5xx is a retryable transport failure, a 401 an
        /// auth failure, a 403 a permission failure, a 429 a rate limit, and any other 4xx a
        /// <see cref="DominaiteApiException"/> that keeps the code (IDEMPOTENCY_KEY_REQUIRED on a
        /// 400 is the whole point of the rejection).
        /// </summary>
        public DominaiteException Rejection()
        {
            if (this.Status >= 500)
            {
                return new DominaiteTransportException(
                    $"The Dominaite API is unavailable (HTTP {this.Status}); retry with the same idempotency key.",
                    this.Status,
                    code: this.ErrorCode);
            }

            var code = this.ErrorCode;
            var message = this.ErrorMessage;
            switch (this.Status)
            {
                case 401:
                    return new DominaiteAuthException(
                        code ?? "UNAUTHORIZED",
                        message ?? "Authentication failed - check your key id, secret, and server clock.",
                        this.Status);
                case 403:
                    return Forbidden(code, message);
                case 429:
                    return new DominaiteRateLimitException(
                        code,
                        message ?? "Too many requests (HTTP 429). Wait, then retry with the same idempotency key.",
                        this.RetryAfter);
                default:
                    return new DominaiteApiException(this.Status, code, message ?? "Request rejected", this.Envelope);
            }
        }

        /// <summary>
        /// A 403 with a message of its own per code. The credentials were good, so the 401 hint
        /// about the key id, the secret and the clock would send the caller the wrong way.
        /// </summary>
        private static DominaitePermissionException Forbidden(string? code, string? message)
        {
            var text = code switch
            {
                PermissionErrorCodes.InsufficientScope =>
                    $"{(message ?? "This API key does not have the scope this route requires").TrimEnd('.')}. Use an API key that has this scope.",
                PermissionErrorCodes.VerifyOnlyNotEnabled =>
                    "Verify-only sessions are not enabled for this account. We switch it on for your account. Contact Dominaite support to ask for it.",
                _ => message ?? "This API key is not allowed to make this call (HTTP 403).",
            };

            return new DominaitePermissionException(code ?? "FORBIDDEN", text);
        }
    }

    /// <summary>
    /// Validates the request and returns the exact body bytes that get both signed and sent.
    /// Serializing once is the point: hashing a second serialization would let key ordering or
    /// escaping drift between the signature and the wire.
    /// </summary>
    private static string SerializeBody(CheckoutSessionRequest request)
    {
        ValidateMoneyParams(request.Amount, request.Currency, request.OrderReference, request.VerifyOnly == true);
        return SerializeWithExtra(request, request.Extra);
    }

    /// <summary>
    /// Validates a charge and returns the exact body bytes that get both signed and sent, in
    /// contract order: amount, currency, orderReference, then description and agreementId when
    /// set, then the extra fields.
    /// </summary>
    private static string SerializeBody(ChargeRequest request)
    {
        ValidateMoneyParams(request.Amount, request.Currency, request.OrderReference);
        return SerializeWithExtra(request, request.Extra);
    }

    /// <summary>
    /// Validates a refund and returns the exact body bytes that get both signed and sent: amount
    /// then reason, each only when set, so a full refund with no reason is <c>{}</c>.
    /// </summary>
    private static string SerializeBody(RefundRequest request)
    {
        if (request.Amount is <= 0)
        {
            throw new DominaiteValidationException(
                "Amount must be a positive integer in MINOR units (e.g. 2500 for 25.00 EUR), or null to refund everything still refundable");
        }

        return SerializeWithExtra(request, request.Extra);
    }

    /// <summary>
    /// The modelled properties in declaration order, nulls omitted, then each extra field whose
    /// key is not already in the body. Serialized once, so these are the bytes that get both
    /// signed and sent.
    /// </summary>
    private static string SerializeWithExtra<T>(T request, IDictionary<string, object?> extra)
    {
        var node = JsonSerializer.SerializeToNode(request, JsonOptions)!.AsObject();
        foreach (var field in extra)
        {
            if (!node.ContainsKey(field.Key))
            {
                node[field.Key] = JsonSerializer.SerializeToNode(field.Value, JsonOptions);
            }
        }

        return node.ToJsonString(JsonOptions);
    }

    /// <summary>
    /// The money checks every create call makes before anything is sent. A verify-only session
    /// saves a card without a payment, so its amount of 0 is by design: there the SDK leaves the
    /// amount to the gateway.
    /// </summary>
    private static void ValidateMoneyParams(long amount, string? currency, string? orderReference, bool verifyOnly = false)
    {
        // The gateway refuses a verify-only session with any amount but 0: the platform sets the
        // verification amount.
        if (verifyOnly && amount != 0)
        {
            throw new DominaiteValidationException(
                "Leave Amount at 0 with VerifyOnly: the platform sets the verification amount");
        }

        if (amount <= 0 && !verifyOnly)
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

    /// <summary>
    /// A transaction id in the lowercase hyphenated form the gateway signs, or a validation error.
    /// </summary>
    private static string NormalizeTransactionId(string transactionId)
    {
        var normalized = transactionId.Trim().ToLowerInvariant();
        if (!Guid.TryParseExact(normalized, "D", out _))
        {
            throw new DominaiteValidationException(
                "transactionId must be the payment UUID returned by CreateCheckoutSessionAsync or a charge");
        }

        return normalized;
    }

    private static string NormalizeRefundId(string refundId)
    {
        var trimmed = refundId.Trim();
        if (!IdSegmentPattern.IsMatch(trimmed))
        {
            throw new DominaiteValidationException(
                "refundId must be the Refund.RefundId from CreateRefundAsync (letters, digits, _ or -, at most 100 characters)");
        }

        return trimmed;
    }

    private static string NormalizePaymentMethodId(string paymentMethodId)
    {
        var trimmed = paymentMethodId.Trim();
        if (!IdSegmentPattern.IsMatch(trimmed))
        {
            throw new DominaiteValidationException(
                "paymentMethodId must be the StoredPaymentMethod id from GetStatusAsync (letters, digits, _ or -, at most 100 characters)");
        }

        return trimmed;
    }

    /// <summary>
    /// A plan or agreement id exactly as given, trimmed, or a validation error. The gateway signs
    /// these ids as the path carries them.
    /// </summary>
    private static string NormalizeHandle(string id, string what)
    {
        var trimmed = id.Trim();
        if (!IdSegmentPattern.IsMatch(trimmed))
        {
            throw new DominaiteValidationException($"{what} (letters, digits, _ or -, at most 100 characters)");
        }

        return trimmed;
    }

    /// <summary>
    /// A webhook endpoint id in the lowercase hyphenated form the gateway signs, or a validation
    /// error.
    /// </summary>
    private static string NormalizeWebhookId(string webhookId)
    {
        var normalized = webhookId.Trim().ToLowerInvariant();
        if (!Guid.TryParseExact(normalized, "D", out _))
        {
            throw new DominaiteValidationException(
                "webhookId must be the WebhookEndpoint.Id from CreateWebhookEndpointAsync or ListWebhookEndpointsAsync");
        }

        return normalized;
    }

    private static WebhookEndpoint ToWebhookEndpoint(JsonElement data)
    {
        var endpoint = Deserialize<WebhookEndpoint>(data, "webhook endpoint");
        endpoint.Raw = data;
        return endpoint;
    }

    /// <summary>
    /// The query of a transaction list call: the modelled parameters, then the extra ones, each
    /// name and value percent-encoded. The window is written in UTC with a <c>Z</c>, with a
    /// fraction only when the value has one.
    /// </summary>
    private static string TransactionQuery(ListTransactionsRequest request)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("from", QueryInstant(request.From)),
            new("to", QueryInstant(request.To)),
        };
        if (request.SettledFrom is { } settledFrom)
        {
            parameters.Add(new("settledFrom", settledFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        if (request.SettledTo is { } settledTo)
        {
            parameters.Add(new("settledTo", settledTo.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        if (request.Status is not null)
        {
            parameters.Add(new("status", request.Status));
        }

        if (request.Limit is { } limit)
        {
            parameters.Add(new("limit", limit.ToString(CultureInfo.InvariantCulture)));
        }

        if (request.Cursor is not null)
        {
            parameters.Add(new("cursor", request.Cursor));
        }

        foreach (var field in request.Extra)
        {
            if (!parameters.Exists(parameter => string.Equals(parameter.Key, field.Key, StringComparison.Ordinal)))
            {
                parameters.Add(field);
            }
        }

        return string.Join(
            "&",
            parameters.Select(parameter => $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));
    }

    private static string QueryInstant(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        var format = utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-dd'T'HH:mm:ss'Z'"
            : "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
        return utc.ToString(format, CultureInfo.InvariantCulture);
    }

    private static Plan ToPlan(JsonElement data)
    {
        var plan = Deserialize<Plan>(data, "plan");
        plan.Raw = data;
        return plan;
    }

    private static Agreement ToAgreement(JsonElement data)
    {
        var agreement = Deserialize<Agreement>(data, "agreement");
        agreement.Raw = data;
        return agreement;
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

    /// <summary>
    /// Writes a date in a request body as UTC with a <c>Z</c>, whatever offset the value carries,
    /// so the gateway reads the same instant whatever its own time zone is. Reading is the
    /// default.
    /// </summary>
    private sealed class UtcDateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.UtcDateTime);
    }
}
