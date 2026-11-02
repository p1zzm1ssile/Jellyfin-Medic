using System.Net;
using System.Net.Sockets;
using System.Text;
using JellyfinMedic.Services;

namespace JellyfinMedic.Tests;

public sealed class LinkCheckerTests : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private int _requests;

    [Fact]
    public async Task A_failing_address_is_not_asked_again_on_the_next_report()
    {
        _listener.Start();
        _ = AnswerWith500Async();
        string url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/manifest.json";

        await LinkChecker.CheckAsync(new[] { url }, CancellationToken.None);
        int afterFirst = Volatile.Read(ref _requests);
        await LinkChecker.CheckAsync(new[] { url }, CancellationToken.None);

        Assert.Equal(2, afterFirst); // one try, and one more a moment later
        Assert.Equal(afterFirst, Volatile.Read(ref _requests));
    }

    public void Dispose() => _listener.Stop();

    private async Task AnswerWith500Async()
    {
        while (true)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                _ = await stream.ReadAsync(new byte[8192]);
                Interlocked.Increment(ref _requests);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException)
            {
                return; // the test is over
            }
        }
    }
}
