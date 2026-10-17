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
