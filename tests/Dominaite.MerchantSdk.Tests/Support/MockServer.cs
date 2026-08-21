using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Dominaite.MerchantSdk.Tests.Support;

/// <summary>One canned response, handed out in order.</summary>
public sealed class Reply
{
    public int Status { get; init; } = 200;

    public string Body { get; init; } = string.Empty;

    public Dictionary<string, string> Headers { get; init; } = [];

    /// <summary>A 200 carrying a payload inside the gateway envelope.</summary>
    public static Reply Enveloped(string payload)
        => new() { Status = 200, Body = $"{{\"success\":true,\"data\":{payload}}}" };

    /// <summary>A non-2xx carrying the gateway's error envelope.</summary>
    public static Reply ErrorEnvelope(int status, string code, string message)
        => new()
        {
            Status = status,
            Body = $"{{\"success\":false,\"error\":{{\"code\":\"{code}\",\"message\":\"{message}\",\"statusCode\":{status}}}}}",
        };

    /// <summary>A raw body with no envelope at all, e.g. a proxy's HTML 502 page.</summary>
    public static Reply Raw(int status, string body) => new() { Status = status, Body = body };

    /// <summary>A redirect, which the real API never sends.</summary>
    public static Reply Redirect(int status, string location)
        => new() { Status = status, Body = string.Empty, Headers = { ["Location"] = location } };
}

/// <summary>What the server saw.</summary>
public sealed class RecordedRequest
{
    public required string Method { get; init; }

    public required string Path { get; init; }

    public required string Body { get; init; }

    public required Dictionary<string, string> Headers { get; init; }

    public string? Header(string name)
        => this.Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A real HTTP server on loopback. It has to be a real socket rather than a stubbed
/// HttpMessageHandler: the redirect test is about what the handler does with a 3xx, and a stub
/// would never exercise that.
/// </summary>
/// <remarks>
/// The base URL carries an <c>/api</c> prefix on purpose - the same shape dev has - so the tests
/// can prove the signed path excludes it.
/// </remarks>
public sealed class MockServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Queue<Reply> _replies;
    private readonly List<RecordedRequest> _requests = [];
    private readonly Task _loop;
    private readonly object _lock = new();

    public MockServer(params Reply[] replies)
    {
        this._replies = new Queue<Reply>(replies);

        var port = FreePort();
        this.BaseUrl = $"http://localhost:{port}/api";
        this._listener = new HttpListener();
        this._listener.Prefixes.Add($"http://localhost:{port}/");
        this._listener.Start();
        this._loop = Task.Run(this.ServeAsync);
    }

    public string BaseUrl { get; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (this._lock)
            {
                return [.. this._requests];
            }
        }
    }

    public RecordedRequest LastRequest => this.Requests[^1];

    public void Dispose()
    {
        this._listener.Stop();
        this._listener.Close();
        try
        {
            this._loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The listener was torn down mid-accept; nothing to report.
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (this._listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await this._listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in context.Request.Headers.AllKeys)
            {
                if (name is not null)
                {
                    headers[name] = context.Request.Headers[name] ?? string.Empty;
                }
            }

            lock (this._lock)
            {
                this._requests.Add(new RecordedRequest
                {
                    Method = context.Request.HttpMethod,
                    Path = context.Request.Url?.AbsolutePath ?? string.Empty,
                    Body = body,
                    Headers = headers,
                });
            }

            Reply reply;
            lock (this._lock)
            {
                reply = this._replies.Count > 0
                    ? this._replies.Dequeue()
                    : Reply.Raw(500, "{\"success\":false}");
            }

            context.Response.StatusCode = reply.Status;
            context.Response.ContentType = "application/json";
            foreach (var header in reply.Headers)
            {
                // HttpListener guards Location behind its own property.
                if (string.Equals(header.Key, "Location", StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.RedirectLocation = header.Value;
                }
                else
                {
                    context.Response.Headers[header.Key] = header.Value;
                }
            }

            var payload = Encoding.UTF8.GetBytes(reply.Body);
            context.Response.ContentLength64 = payload.Length;
            await context.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
            context.Response.Close();
        }
    }
}
