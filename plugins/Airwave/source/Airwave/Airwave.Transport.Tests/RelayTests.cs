using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Net.Sockets;
using Airwave.Core;
using Airwave.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Airwave.Transport.Tests;

public sealed class RelayTests
{
    [Fact]
    public async Task RedirectCannotForwardCredentialsToAnotherEndpoint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        int redirectedRequests = 0;
        app.MapGet("/v1/publish", (HttpContext context) =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = "/unexpected";
        });
        app.MapGet("/unexpected", () => { Interlocked.Increment(ref redirectedRequests); return Results.Ok(); });
        await app.StartAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var url = app.Urls.Single().Replace("http://", "ws://", StringComparison.Ordinal);
        await Assert.ThrowsAsync<RelayConnectionException>(() => StreamConnection.ConnectAsync(url, ConnectionPolicy.NewToken(), true, deadline.Token));
        Assert.Equal(0, redirectedRequests);
        await app.StopAsync(deadline.Token);
    }

    [Fact]
    public async Task StalledNetworkListenerIsRemovedWhileHealthyListenerContinues()
    {
        await using var relay = await RunningRelay.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var publish = await relay.Publish(deadline.Token);
        using var slow = new ClientWebSocket();
        slow.Options.AddSubProtocol("airwave.v1");
        slow.Options.SetRequestHeader("Authorization", "Bearer " + relay.Options.ListenToken);
        using var http = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ReceiveBufferSize = 1024, NoDelay = true };
                try { await socket.ConnectAsync(context.DnsEndPoint, token); return new NetworkStream(socket, ownsSocket: true); }
                catch { socket.Dispose(); throw; }
            },
        });
        await slow.ConnectAsync(ConnectionPolicy.Endpoint(relay.Url, false), http, deadline.Token);
        await using var healthy = await relay.Listen(deadline.Token);
        Assert.Equal(2, relay.ListenerCount);
        var payload = new byte[AudioProtocol.MaxOpusBytes];
        payload[0] = 0xFC;
        ulong sequence = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        do
        {
            await timer.WaitForNextTickAsync(deadline.Token);
            await publish.SendAsync(new(sequence, checked((long)sequence * 20), payload), deadline.Token);
            Assert.Equal(sequence, (await healthy.ReceiveAsync(deadline.Token))!.Sequence);
            sequence++;
        } while (relay.ListenerCount == 2 && sequence < 450);
        Assert.Equal(1, relay.ListenerCount);
        Assert.True(sequence > 12);
    }

    [Fact]
    public async Task AuthenticationIsRequiredBeforeUpgradeAndRolesAreDistinct()
    {
        await using var relay = await RunningRelay.Start();
        using var http = new HttpClient { BaseAddress = new Uri(relay.HttpUrl) };
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v1/publish")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", relay.Options.ListenToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/v1/publish")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/v1/listen")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/v1/listen?token=redacted")).StatusCode);
        http.DefaultRequestHeaders.Add("Origin", "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/v1/listen")).StatusCode);
    }

    [Fact]
    public async Task OnePublisherForwardsPacketsAndLateJoinStartsAtCurrentFrame()
    {
        await using var relay = await RunningRelay.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var publish = await relay.Publish(deadline.Token);
        await Assert.ThrowsAsync<RelayConnectionException>(() => relay.Publish(deadline.Token));
        await publish.SendAsync(ProtocolTests.Packet(0), deadline.Token);
        await using var listener = await relay.Listen(deadline.Token);
        await Task.Delay(25, deadline.Token);
        await publish.SendAsync(ProtocolTests.Packet(1), deadline.Token);
        var received = await listener.ReceiveAsync(deadline.Token);
        Assert.NotNull(received);
        Assert.Equal(1UL, received.Sequence);
        Assert.Equal(20, received.TimestampMilliseconds);
        await using var late = await relay.Listen(deadline.Token);
        await Task.Delay(25, deadline.Token);
        await publish.SendAsync(ProtocolTests.Packet(2), deadline.Token);
        Assert.Equal(2UL, (await late.ReceiveAsync(deadline.Token))!.Sequence);
        Assert.Equal(2UL, (await listener.ReceiveAsync(deadline.Token))!.Sequence);
        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.SendAsync(ProtocolTests.Packet(3), deadline.Token));
        await publish.DisposeAsync();
        Assert.Null(await listener.ReceiveAsync(deadline.Token));
        Assert.Null(await late.ReceiveAsync(deadline.Token));
    }

    [Fact]
    public async Task ListenerCapacityAndOfflineStateAreEnforced()
    {
        await using var relay = await RunningRelay.Start(1);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var offline = await Assert.ThrowsAsync<RelayConnectionException>(() => relay.Listen(deadline.Token));
        Assert.Equal(RelayConnectionFailure.BroadcastOffline, offline.Failure);
        await using var publish = await relay.Publish(deadline.Token);
        await using var first = await relay.Listen(deadline.Token);
        var full = await Assert.ThrowsAsync<RelayConnectionException>(() => relay.Listen(deadline.Token));
        Assert.Equal(RelayConnectionFailure.Full, full.Failure);
    }

    [Fact]
    public async Task RejectedCredentialsAndBusyBroadcasterHaveActionableErrors()
    {
        await using var relay = await RunningRelay.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var denied = await Assert.ThrowsAsync<RelayConnectionException>(() =>
            StreamConnection.ConnectAsync(relay.Url, relay.Options.ListenToken, true, deadline.Token));
        Assert.Equal(RelayConnectionFailure.Unauthorized, denied.Failure);
        Assert.DoesNotContain(relay.Options.ListenToken, denied.ToString());
        await using var publisher = await relay.Publish(deadline.Token);
        var busy = await Assert.ThrowsAsync<RelayConnectionException>(() => relay.Publish(deadline.Token));
        Assert.Equal(RelayConnectionFailure.BroadcasterBusy, busy.Failure);
    }

    [Fact]
    public async Task ConsistentCaptureGapsAreForwardedWithoutStaleReplay()
    {
        await using var relay = await RunningRelay.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var publish = await relay.Publish(deadline.Token);
        await using var listener = await relay.Listen(deadline.Token);
        await publish.SendAsync(ProtocolTests.Packet(), deadline.Token);
        Assert.Equal(0UL, (await listener.ReceiveAsync(deadline.Token))!.Sequence);
        await Task.Delay(60, deadline.Token);
        await publish.SendAsync(ProtocolTests.Packet(3), deadline.Token);
        Assert.Equal(3UL, (await listener.ReceiveAsync(deadline.Token))!.Sequence);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("oversize")]
    [InlineData("header")]
    [InlineData("opus")]
    [InlineData("timing")]
    public async Task InvalidPublisherInputEndsStream(string mutation)
    {
        await using var relay = await RunningRelay.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var publish = await relay.RawPublisher(deadline.Token);
        await using var listener = await relay.Listen(deadline.Token);
        await publish.SendAsync(AudioProtocol.Encode(ProtocolTests.Packet()), WebSocketMessageType.Binary, true, deadline.Token);
        Assert.NotNull(await listener.ReceiveAsync(deadline.Token));
        var bad = AudioProtocol.Encode(ProtocolTests.Packet(1));
        var type = WebSocketMessageType.Binary;
        switch (mutation)
        {
            case "text": type = WebSocketMessageType.Text; break;
            case "oversize": bad = new byte[AudioProtocol.MaxMessageBytes + 1]; break;
            case "header": bad[0] = 0; break;
            case "opus": bad[AudioProtocol.HeaderBytes] = 0x80; break;
            case "timing": bad[23] = 99; break;
        }
        await publish.SendAsync(bad, type, true, deadline.Token);
        Assert.True(await IsClosed(listener, deadline.Token));
    }

    [Fact]
    public async Task ExcessiveEmptyFragmentsEndTheStream()
    {
        await using var relay = await RunningRelay.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var publish = await relay.RawPublisher(deadline.Token);
        await using var listener = await relay.Listen(deadline.Token);
        try
        {
            for (int fragment = 0; fragment < 40; ++fragment)
                await publish.SendAsync(ReadOnlyMemory<byte>.Empty, WebSocketMessageType.Binary, false, deadline.Token);
        }
        catch (WebSocketException) { }
        Assert.True(await IsClosed(listener, deadline.Token));
    }

    [Fact]
    public async Task FragmentedValidPacketIsAssembledOnce()
    {
        await using var relay = await RunningRelay.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var publish = await relay.RawPublisher(deadline.Token);
        await using var listener = await relay.Listen(deadline.Token);
        var bytes = AudioProtocol.Encode(ProtocolTests.Packet());
        await publish.SendAsync(bytes.AsMemory(0, 7), WebSocketMessageType.Binary, false, deadline.Token);
        await publish.SendAsync(bytes.AsMemory(7), WebSocketMessageType.Binary, true, deadline.Token);
        Assert.Equal(0UL, (await listener.ReceiveAsync(deadline.Token))!.Sequence);
    }

    private static async Task<bool> IsClosed(StreamConnection listener, CancellationToken token)
    {
        try { return await listener.ReceiveAsync(token) is null; }
        catch (WebSocketException) { return true; }
    }

    private sealed class RunningRelay(WebApplication app, RelayOptions options) : IAsyncDisposable
    {
        public RelayOptions Options => options;
        public int ListenerCount => app.Services.GetRequiredService<RelayRoom>().ListenerCount;
        public string HttpUrl => app.Urls.Single();
        public string Url => HttpUrl.Replace("http://", "ws://", StringComparison.Ordinal);

        public static async Task<RunningRelay> Start(int maxListeners = 64)
        {
            var options = new RelayOptions(ConnectionPolicy.NewToken(), ConnectionPolicy.NewToken(), maxListeners);
            var app = RelayApplication.Build(["--urls", "http://127.0.0.1:0"], options);
            await app.StartAsync();
            return new(app, options);
        }

        public Task<StreamConnection> Publish(CancellationToken token) => StreamConnection.ConnectAsync(Url, options.PublishToken, true, token);
        public Task<StreamConnection> Listen(CancellationToken token) => StreamConnection.ConnectAsync(Url, options.ListenToken, false, token);

        public async Task<ClientWebSocket> RawPublisher(CancellationToken token)
        {
            var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Authorization", "Bearer " + options.PublishToken);
            socket.Options.AddSubProtocol("airwave.v1");
            await socket.ConnectAsync(ConnectionPolicy.Endpoint(Url, true), token);
            return socket;
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(timeout.Token);
            await app.DisposeAsync();
        }
    }
}
