using System.Net;
using System.Net.Sockets;
using Dominaite.MerchantSdk.Tests.Support;
using Xunit;

namespace Dominaite.MerchantSdk.Tests;

/// <summary>
/// The loopback server every client test stands on. Test classes run in parallel, so a port
/// probed as free can be bound by another server before this one starts.
/// </summary>
public class MockServerTests
{
    [Fact]
    public async Task StartsOnAnotherPortWhenThePickedOneIsAlreadyTaken()
    {
        var taken = UnusedPort();
        using var squatter = new HttpListener();
        squatter.Prefixes.Add($"http://localhost:{taken}/");
        squatter.Start();

        var free = UnusedPort();
        var picks = new Queue<int>([taken, free]);

        using var server = new MockServer(picks.Dequeue, Reply.Enveloped("{}"));
        using var http = new HttpClient();
        var response = await http.GetAsync($"{server.BaseUrl}/ping");

        Assert.Equal($"http://localhost:{free}/api", server.BaseUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(picks);
    }

    private static int UnusedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
