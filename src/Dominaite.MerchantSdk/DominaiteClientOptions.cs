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

    /// <summary>The per-request timeout. Defaults to 45 seconds.</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>
    /// Appended to the SDK's User-Agent after one space, which helps when Dominaite support reads
    /// the access logs for your integration. The SDK sends
    /// <c>dominaite-dotnet/&lt;version&gt; (dotnet &lt;runtime version&gt;) &lt;suffix&gt;</c>. A suffix
    /// with a control character fails construction with a <see cref="DominaiteValidationException"/>.
    /// </summary>
    public string? UserAgentSuffix { get; set; }

    /// <summary>
    /// Where the SDK sends its warnings: a <c>Dominaite-Sdk-Notice</c> from the API (an old SDK
    /// version, once per distinct notice), a retry in
    /// <see cref="DominaiteClient.CreateCheckoutSessionWithRetryAsync"/>, and a ping clock skew
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
/// How <see cref="DominaiteClient.CreateCheckoutSessionWithRetryAsync"/> backs off.
/// </summary>
public sealed class RetryOptions
{
    /// <summary>Total attempts including the first.</summary>
    public int Attempts { get; set; } = 3;

    /// <summary>The wait before the first retry. It doubles each attempt.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
