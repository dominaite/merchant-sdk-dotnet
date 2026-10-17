namespace Dominaite.MerchantSdk;

/// <summary>
/// Everything about a <see cref="DominaiteClient"/> that is not a credential.
/// </summary>
public sealed class DominaiteClientOptions
{
    /// <summary>
    /// Serverless cold starts hit 10+ seconds outside prod, so 45s is the floor that does not
    /// turn a cold start into a false transport failure.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The API to talk to. Required: there is no default. Trailing slashes are trimmed.
    /// </summary>
    /// <remarks>
    /// Use <see cref="DominaiteClient.SandboxBaseUrl"/> (<c>https://api.staging.dominaite.com/payments</c>)
    /// with sandbox keys, or <see cref="DominaiteClient.ProductionBaseUrl"/>
    /// (<c>https://api.dominaite.com/payments</c>) with live keys. A missing, empty or
    /// whitespace-only value fails construction with a <see cref="DominaiteValidationException"/>.
    /// The URL must be <c>https://</c>; plain <c>http://</c> is accepted only for localhost,
    /// 127.0.0.1 and ::1. The base URL's own prefix is never part of the signed path.
    /// </remarks>
    public string? BaseUrl { get; set; }

    /// <summary>The timeout for one attempt. Defaults to 45 seconds.</summary>
    /// <remarks>
    /// The timeout applies to each attempt, not to the whole call. With retries, one call can wait
    /// up to (<see cref="MaxRetries"/> + 1) x <see cref="Timeout"/>, plus the waits between
    /// attempts.
    /// </remarks>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>
    /// How many times a failed call is sent again. Defaults to 2. Set 0 to send every call once.
    /// A negative value fails construction with a <see cref="DominaiteValidationException"/>.
    /// </summary>
    /// <remarks>
    /// Only calls that are safe to replay are retried: every GET, and every POST that carries an
    /// idempotency key, except <see cref="DominaiteClient.CreateWebhookEndpointAsync"/>. Each
    /// attempt sends the same path, body and idempotency key. Only a transport failure (a timeout,
    /// a dropped connection, a 5xx with no typed meaning on the route) and
    /// <c>PAYMENT_PROCESSING_UNAVAILABLE</c> are retried. A 429 is never retried: wait
    /// <see cref="DominaiteRateLimitException.RetryAfter"/> yourself. Before retry n (from 0) the
    /// client waits a random time between 0 and 500ms x 2^n, capped at 8 seconds.
    /// </remarks>
    public int MaxRetries { get; set; } = 2;

    /// <summary>The wait between attempts. Tests replace it to run without real waits.</summary>
    internal Func<TimeSpan, CancellationToken, Task>? Sleep { get; set; }

    /// <summary>A random number in [0, 1) for the jitter. Tests replace it to pin the waits.</summary>
    internal Func<double>? Random { get; set; }

    /// <summary>
    /// Appended to the SDK's User-Agent after one space, which helps when Dominaite support reads
    /// the access logs for your integration. The SDK sends
    /// <c>dominaite-dotnet/&lt;version&gt; (dotnet &lt;runtime version&gt;) &lt;suffix&gt;</c>. A suffix
    /// with a control character fails construction with a <see cref="DominaiteValidationException"/>.
    /// </summary>
    public string? UserAgentSuffix { get; set; }

    /// <summary>
    /// Where the SDK sends its warnings: a <c>Dominaite-Sdk-Notice</c> from the API (an old SDK
    /// version, once per distinct notice), each retry of a call, and a ping clock skew
    /// over 60 seconds. Defaults to <see cref="System.Diagnostics.Trace.TraceWarning(string)"/>.
    /// </summary>
    /// <remarks>
    /// A warning never holds the secret, a signature, a cashier token or a request or response
    /// body. Whatever the callback throws is dropped, so it cannot fail the call.
    /// </remarks>
    public Action<string>? OnWarning { get; set; }

    /// <summary>
    /// Turns on the debug log: one line per request with the method, the signed path, the status,
    /// the time in milliseconds, the <c>Dominaite-Request-Id</c> response header and the
    /// idempotency key. Off when null, the default.
    /// </summary>
    /// <remarks>
    /// A debug line never holds the secret, a signature, a cashier token, a query string or a
    /// request or response body. Whatever the callback throws is dropped.
    /// </remarks>
    public Action<string>? OnDebug { get; set; }

    /// <summary>
    /// Your own <see cref="System.Net.Http.HttpClient"/>, for a proxy-aware transport or a
    /// factory-managed one. The SDK will not dispose it, and it overrides
    /// <see cref="Timeout"/>.
    /// </summary>
    /// <remarks>
    /// Configure its handler with <c>AllowAutoRedirect = false</c>. The Dominaite API never
    /// redirects, and a handler that follows a 3xx would replay your signed request at whatever
    /// host the redirect names. The client the SDK builds for itself already refuses to follow
    /// them.
    /// </remarks>
    public HttpClient? HttpClient { get; set; }
}

/// <summary>
/// How <see cref="DominaiteClient.CreateCheckoutSessionWithRetryAsync"/> backs off. Deprecated with
/// that method: set <see cref="DominaiteClientOptions.MaxRetries"/> instead.
/// </summary>
public sealed class RetryOptions
{
    /// <summary>Total attempts including the first.</summary>
    public int Attempts { get; set; } = 3;

    /// <summary>
    /// The longest wait before the first retry. It doubles each attempt, capped at 8 seconds, and
    /// each wait is a random time up to that ceiling.
    /// </summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
