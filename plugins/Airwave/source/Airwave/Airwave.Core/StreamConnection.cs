using System.Net;
using System.Net.WebSockets;
using System.Security.Authentication;

namespace Airwave.Core;

public enum RelayConnectionFailure { Unauthorized, BroadcastOffline, BroadcasterBusy, Full, SecureConnection, Unavailable }

public sealed class RelayConnectionException(RelayConnectionFailure failure)
    : IOException(MessageFor(failure))
{
    public RelayConnectionFailure Failure { get; } = failure;
    public static string MessageFor(RelayConnectionFailure failure) => failure switch
    {
        RelayConnectionFailure.Unauthorized => "The access key was rejected. Ask the host for a new invite or check the broadcast key.",
        RelayConnectionFailure.BroadcastOffline => "The broadcaster is not on air yet. Wait for them to start, then join again.",
        RelayConnectionFailure.BroadcasterBusy => "Another broadcaster is already on air. Stop that broadcast before starting this one.",
        RelayConnectionFailure.Full => "The broadcast is full. Try joining again after a listener leaves.",
        RelayConnectionFailure.SecureConnection => "The relay's secure connection could not be verified. Ask the host to check its certificate and address.",
        _ => "The relay could not be reached. Check your connection and relay address, then try again.",
    };
}

public sealed class StreamConnection : IAsyncDisposable
{
    private readonly ClientWebSocket socket;
    private readonly HttpMessageInvoker http;
    private readonly bool publishing;
    private readonly SemaphoreSlim sending = new(1);
    private readonly SemaphoreSlim receiving = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task? publisherControl;
    private bool disposed;

    private StreamConnection(ClientWebSocket socket, HttpMessageInvoker http, bool publishing)
    {
        (this.socket, this.http, this.publishing) = (socket, http, publishing);
        if (publishing) publisherControl = ReadPublisherControlAsync();
    }

    private async Task ReadPublisherControlAsync()
    {
        try
        {
            // A pending receive is required for WebSocket ping/pong processing,
            // including on the side that only publishes application data.
            var buffer = new byte[1];
            _ = await socket.ReceiveAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false);
            // A publisher receives control frames only. A peer close or any
            // application message ends this session; no downstream audio is read.
            socket.Abort();
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            socket.Abort();
        }
    }

    public static async Task<StreamConnection> ConnectAsync(string relayUrl, string token, bool publishing, CancellationToken cancellationToken)
    {
        var uri = ConnectionPolicy.Endpoint(relayUrl, publishing);
        _ = ConnectionPolicy.ValidateToken(token);
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.AddSubProtocol("airwave.v1");
        var http = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Credentials = null,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        });
        try
        {
            await socket.ConnectAsync(uri, http, cancellationToken).ConfigureAwait(false);
            return new(socket, http, publishing);
        }
        catch (Exception error)
        {
            var failure = socket.HttpStatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RelayConnectionFailure.Unauthorized,
                HttpStatusCode.ServiceUnavailable when !publishing => RelayConnectionFailure.BroadcastOffline,
                HttpStatusCode.Conflict => RelayConnectionFailure.BroadcasterBusy,
                HttpStatusCode.TooManyRequests => RelayConnectionFailure.Full,
                _ => IsSecureConnectionFailure(error) ? RelayConnectionFailure.SecureConnection : RelayConnectionFailure.Unavailable,
            };
            socket.Dispose();
            http.Dispose();
            if (error is WebSocketException or HttpRequestException or AuthenticationException)
                throw new RelayConnectionException(failure);
            throw;
        }
    }

    private static bool IsSecureConnectionFailure(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is AuthenticationException or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }) return true;
        return false;
    }

    public async Task SendAsync(AudioPacket packet, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!publishing) throw new InvalidOperationException("A listener cannot publish audio.");
        var bytes = AudioProtocol.Encode(packet);
        await sending.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false); }
        finally { sending.Release(); }
    }

    public async Task<AudioPacket?> ReceiveAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (publishing) throw new InvalidOperationException("A publisher cannot receive listener audio.");
        await receiving.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = await AudioSocket.ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
            return bytes is null ? null : AudioProtocol.Decode(bytes);
        }
        finally { receiving.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Stopped", deadline.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            socket.Abort();
            if (publisherControl is not null) await publisherControl.ConfigureAwait(false);
            socket.Dispose();
            http.Dispose();
            lifetime.Dispose();
        }
    }
}

public static class AudioSocket
{
    public static async Task<byte[]?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var bytes = new byte[AudioProtocol.MaxMessageBytes];
        int length = 0;
        int fragments = 0;
        while (true)
        {
            var result = await socket.ReceiveAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary) throw new InvalidDataException("Only binary audio messages are accepted.");
            if (++fragments > 32) throw new InvalidDataException("Audio message has too many fragments.");
            length += result.Count;
            if (result.EndOfMessage) return bytes.AsSpan(0, length).ToArray();
            if (length == bytes.Length) throw new InvalidDataException("Audio message is too large.");
        }
    }
}
