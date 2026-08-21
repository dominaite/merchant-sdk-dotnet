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
    /// The API to talk to. Empty and whitespace-only values are ignored, so an unset environment
    /// variable still gives you production. Trailing slashes are trimmed.
    /// </summary>
    /// <remarks>
    /// Dev and staging are the raw function hosts, whose Azure Functions route prefix is
    /// <c>/api</c>; production is <c>https://api.dominaite.com/payments</c>. The base URL's own
    /// prefix is never part of the signed path.
    /// </remarks>
    public string? BaseUrl { get; set; }

    /// <summary>The per-request timeout. Defaults to 45 seconds.</summary>
    public TimeSpan Timeout { get; set; } = DefaultTimeout;

    /// <summary>
    /// Appended to the SDK's User-Agent, which helps when Dominaite support reads the access logs
    /// for your integration.
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
