using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Airwave.Core;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;

namespace Airwave.Relay;

public static class RelayApplication
{
    public static WebApplication Build(string[] args, RelayOptions? options = null)
    {
        options ??= RelayOptions.FromEnvironment();
        options.Validate();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:17855");
        // Explicit command-line binding takes precedence over the local default.
        var urlsIndex = Array.FindIndex(args, a => a.Equals("--urls", StringComparison.OrdinalIgnoreCase));
        if (urlsIndex >= 0)
        {
            if (urlsIndex + 1 >= args.Length) throw new ArgumentException("Missing bind address.");
            var bind = args[urlsIndex + 1];
            foreach (var address in bind.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                    || (!ConnectionPolicy.IsLiteralLoopback(uri.Host) && Environment.GetEnvironmentVariable("AIRWAVE_ALLOW_REMOTE_BIND") != "1"))
                    throw new ArgumentException("Remote binding requires explicit configuration.");
            }
            builder.WebHost.UseUrls(bind);
        }
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.AddServerHeader = false;
            server.Limits.MaxConcurrentConnections = 130;
            server.Limits.MaxConcurrentUpgradedConnections = 65;
            server.Limits.MaxRequestBodySize = 0;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(15);
            server.Limits.MaxRequestHeadersTotalSize = 8192;
        });
        builder.Services.Configure<SocketTransportOptions>(transport =>
        {
            transport.MaxReadBufferSize = 4096;
            transport.MaxWriteBufferSize = 4096;
        });
        builder.Services.AddSingleton(new RelayRoom(options.MaximumListeners));
        var app = builder.Build();
        var room = app.Services.GetRequiredService<RelayRoom>();
        app.Lifetime.ApplicationStopping.Register(room.Stop);
        var publishHash = SHA256.HashData(Encoding.ASCII.GetBytes(options.PublishToken));
        var listenHash = SHA256.HashData(Encoding.ASCII.GetBytes(options.ListenToken));
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5), KeepAliveTimeout = TimeSpan.FromSeconds(10) });
        app.MapGet("/health", () => Results.Json(new { status = "ok", protocol = 1 }));
        app.MapGet("/v1/publish", async (HttpContext context) =>
        {
            if (!Authorize(context, publishHash)) return;
            if (!room.TryStartPublisher()) { context.Response.StatusCode = StatusCodes.Status409Conflict; return; }
            WebSocket? socket = null;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync("airwave.v1");
                ulong? sequence = null;
                long lastTimestamp = 0;
                var rate = new FrameRateLimit();
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    var bytes = await AudioSocket.ReceiveAsync(socket, deadline.Token);
                    if (bytes is null) break;
                    var packet = AudioProtocol.Decode(bytes);
                    if (sequence is not null)
                    {
                        var advance = packet.Sequence > sequence.Value ? packet.Sequence - sequence.Value : 0;
                        if (advance is < 1 or > 250 || lastTimestamp > long.MaxValue - (long)advance * 20
                            || packet.TimestampMilliseconds != lastTimestamp + (long)advance * 20)
                            throw new InvalidDataException("Audio timing is invalid.");
                    }
                    if (!rate.Accept()) throw new InvalidDataException("Audio arrived too quickly.");
                    sequence = packet.Sequence;
                    lastTimestamp = packet.TimestampMilliseconds;
                    room.Forward(bytes);
                }
            }
            catch (Exception error) when (IsConnectionError(error)) { }
            finally
            {
                room.EndPublisher();
                if (socket is not null) { await CloseSocket(socket); socket.Dispose(); }
            }
        });
        app.MapGet("/v1/listen", async (HttpContext context) =>
        {
            if (!Authorize(context, listenHash)) return;
            var subscription = room.TrySubscribe(out var failure);
            if (subscription is null) { context.Response.StatusCode = failure; return; }
            WebSocket? socket = null;
            try
            {
                socket = await context.WebSockets.AcceptWebSocketAsync("airwave.v1");
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var sender = SendToListener(socket, subscription, stop.Token);
                var receiver = ReadListenerControl(socket, stop.Token);
                try
                {
                    var completed = await Task.WhenAny(sender, receiver);
                    if (completed == receiver) subscription.Stop();
                    // Finish the only application send before starting the close
                    // handshake. Cancelling a pending receive first would abort
                    // the socket and turn an ordinary source stop into an error.
                    await sender.WaitAsync(TimeSpan.FromSeconds(1));
                    await completed;
                    using var closeDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Stream ended", closeDeadline.Token);
                    await receiver.WaitAsync(TimeSpan.FromMilliseconds(500));
                }
                finally
                {
                    stop.Cancel();
                    try { await Task.WhenAll(sender, receiver); } catch (Exception error) when (IsConnectionError(error)) { }
                }
            }
            catch (Exception error) when (IsConnectionError(error)) { }
            finally
            {
                room.Unsubscribe(subscription);
                if (socket is not null) { await CloseSocket(socket); socket.Dispose(); }
                subscription.Dispose();
            }
        });
        return app;
    }

    private static bool Authorize(HttpContext context, byte[] expectedHash)
    {
        var request = context.Request;
        if (!request.IsHttps && !(context.Connection.RemoteIpAddress is { } peer && IPAddress.IsLoopback(peer)))
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return false; }
        if (request.QueryString.HasValue || request.Headers.ContainsKey("Origin"))
        { context.Response.StatusCode = StatusCodes.Status403Forbidden; return false; }
        var authorization = request.Headers.Authorization;
        if (authorization.Count != 1 || authorization[0] is not { } value || !value.StartsWith("Bearer ", StringComparison.Ordinal)
            || value.Length > 179 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.ASCII.GetBytes(value[7..])), expectedHash))
        { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return false; }
        if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains("airwave.v1"))
        { context.Response.StatusCode = StatusCodes.Status400BadRequest; return false; }
        return true;
    }

    private static async Task SendToListener(WebSocket socket, ListenerSubscription subscription, CancellationToken token)
    {
        while (await subscription.Reader.WaitToReadAsync(token))
        {
            while (subscription.Reader.TryRead(out var queued))
            {
                if (subscription.Stopped.IsCancellationRequested) return;
                if (Stopwatch.GetElapsedTime(queued.CreatedAt) > TimeSpan.FromMilliseconds(250))
                    throw new InvalidDataException("Listener fell behind.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromMilliseconds(250));
                await socket.SendAsync(queued.Bytes.AsMemory(), WebSocketMessageType.Binary, true, deadline.Token);
            }
        }
    }

    private static async Task ReadListenerControl(WebSocket socket, CancellationToken token)
    {
        var bytes = new byte[1];
        var result = await socket.ReceiveAsync(bytes.AsMemory(), token);
        if (result.MessageType != WebSocketMessageType.Close) throw new InvalidDataException("Listeners cannot send audio.");
    }

    private static bool IsConnectionError(Exception error) => error is WebSocketException or OperationCanceledException
        or InvalidDataException or IOException or ObjectDisposedException or BadHttpRequestException or TimeoutException;

    private static async Task CloseSocket(WebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Stream ended", timeout.Token);
        }
        catch (Exception error) when (IsConnectionError(error)) { }
        finally { socket.Abort(); }
    }
}

internal sealed class FrameRateLimit
{
    private long last = Stopwatch.GetTimestamp();
    private double credit = 10;

    public bool Accept()
    {
        var now = Stopwatch.GetTimestamp();
        credit = Math.Min(10, credit + Stopwatch.GetElapsedTime(last, now).TotalSeconds * 55);
        last = now;
        if (credit < 1) return false;
        credit -= 1;
        return true;
    }
}
