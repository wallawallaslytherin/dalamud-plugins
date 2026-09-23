using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Airwave.Core;
using Airwave.Relay;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Airwave.Transport.Tests;

public sealed class TlsRelayTests
{
    [Fact]
    public async Task EncryptedRelaySeparatesRolesForwardsAudioAndAllowsReconnect()
    {
        await using var relay = await TlsRelay.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var publisher = await relay.Connect(true, relay.Options.PublishToken, deadline.Token);
        await Assert.ThrowsAsync<WebSocketException>(() => relay.Connect(true, relay.Options.ListenToken, deadline.Token));
        using var listener = await relay.Connect(false, relay.Options.ListenToken, deadline.Token);
        await publisher.SendAsync(AudioProtocol.Encode(ProtocolTests.Packet(0)), WebSocketMessageType.Binary, true, deadline.Token);
        Assert.Equal(0UL, AudioProtocol.Decode((await AudioSocket.ReceiveAsync(listener, deadline.Token))!).Sequence);
        await publisher.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Stopped", deadline.Token);
        Assert.Null(await AudioSocket.ReceiveAsync(listener, deadline.Token));
        Assert.Null(await AudioSocket.ReceiveAsync(publisher, deadline.Token));
        using var nextPublisher = await relay.Connect(true, relay.Options.PublishToken, deadline.Token);
        using var nextListener = await relay.Connect(false, relay.Options.ListenToken, deadline.Token);
        await nextPublisher.SendAsync(AudioProtocol.Encode(ProtocolTests.Packet(10)), WebSocketMessageType.Binary, true, deadline.Token);
        Assert.Equal(10UL, AudioProtocol.Decode((await AudioSocket.ReceiveAsync(nextListener, deadline.Token))!).Sequence);
    }

    [Fact]
    public async Task ProductionClientRejectsUntrustedCertificateBeforeSendingCredentials()
    {
        await using var relay = await TlsRelay.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<RelayConnectionException>(() =>
            StreamConnection.ConnectAsync(relay.Url, relay.Options.PublishToken, true, deadline.Token));
        Assert.Equal(RelayConnectionFailure.SecureConnection, error.Failure);
        Assert.Equal(0, relay.Requests);
        Assert.DoesNotContain(relay.Options.PublishToken, error.ToString());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EvenFixtureTrustRejectsExpiredOrWrongNameCertificates(bool expired, bool wrongName)
    {
        await using var relay = await TlsRelay.Start(expired, wrongName);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<WebSocketException>(() => relay.Connect(true, relay.Options.PublishToken, deadline.Token));
        Assert.Equal(0, relay.Requests);
    }

    // The test alone trusts its ephemeral CA. Production clients and OS trust stores are unchanged.
    private sealed class TlsRelay(WebApplication app, RelayOptions options, X509Certificate2 root,
        X509Certificate2 server, string directory) : IAsyncDisposable
    {
        private int requests;
        public RelayOptions Options => options;
        public string Url => app.Urls.Single().Replace("https://", "wss://", StringComparison.Ordinal);
        public int Requests => Volatile.Read(ref requests);

        public static async Task<TlsRelay> Start(bool expired = false, bool wrongName = false)
        {
            var directory = Path.Combine(Path.GetTempPath(), "Airwave-Tls-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            X509Certificate2? root = null, server = null;
            TlsRelay? relay = null;
            try
            {
                using var rootKey = RSA.Create(2048);
                var rootRequest = new CertificateRequest("CN=Airwave test authority", rootKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
                root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddDays(7));
                using var serverKey = RSA.Create(2048);
                var request = new CertificateRequest("CN=Airwave test relay", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
                var names = new SubjectAlternativeNameBuilder();
                if (wrongName) names.AddDnsName("wrong.example.invalid"); else names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
                using var issued = request.Create(root, DateTimeOffset.UtcNow.AddDays(-2),
                    expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
                server = issued.CopyWithPrivateKey(serverKey);
                var certificatePath = Path.Combine(directory, "relay.pfx");
                await File.WriteAllBytesAsync(certificatePath, server.Export(X509ContentType.Pfx));
                var options = new RelayOptions(ConnectionPolicy.NewToken(), ConnectionPolicy.NewToken());
                var app = RelayApplication.Build(["--urls", "https://127.0.0.1:0", "--Kestrel:Certificates:Default:Path", certificatePath], options);
                relay = new TlsRelay(app, options, root, server, directory);
                app.Use(async (_, next) => { Interlocked.Increment(ref relay.requests); await next(); });
                await app.StartAsync(); return relay;
            }
            catch
            {
                if (relay is not null) await relay.DisposeAsync();
                else { root?.Dispose(); server?.Dispose(); Cleanup(directory); }
                throw;
            }
        }

        public async Task<ClientWebSocket> Connect(bool publishing, string token, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            socket.Options.AddSubProtocol("airwave.v1");
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
            socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is not X509Certificate2 leaf ||
                    (errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(root);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.DisableCertificateDownloads = true;
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
                return chain.Build(leaf) && leaf.RawData.AsSpan().SequenceEqual(server.RawData);
            };
            try { await socket.ConnectAsync(ConnectionPolicy.Endpoint(Url, publishing), cancellationToken); return socket; }
            catch { socket.Dispose(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await app.StopAsync(deadline.Token);
            }
            finally
            {
                try { await app.DisposeAsync(); }
                finally { root.Dispose(); server.Dispose(); Cleanup(directory); }
            }
        }

        private static void Cleanup(string directory)
        {
            if (!Directory.Exists(directory)) return;
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), Path.GetFileName(directory)));
            if (expected != Path.GetFullPath(directory) || !Path.GetFileName(directory).StartsWith("Airwave-Tls-", StringComparison.Ordinal)
                || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid TLS fixture cleanup root.");
            File.Delete(Path.Combine(directory, "relay.pfx"));
            Directory.Delete(directory);
        }
    }
}
