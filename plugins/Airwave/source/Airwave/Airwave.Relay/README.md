# Airwave relay

The relay forwards one live broadcast to at most 64 listeners. It does not
record audio, retain earlier frames for new listeners, or log keys or audio.
The optional Windows x64 relay package includes .NET and ASP.NET Core runtimes.
You do not need to install either framework.

Use the plugin's **Start local sound test** to try audio on one PC. The relay
package is for people who want to run their own separate relay. Remote listening
also requires hosting, a domain, and a valid TLS endpoint.

## Try the standalone relay on this PC

Extract the entire relay ZIP. Keep its files together.

1. Double-click **Setup-Relay.cmd** once. It creates independent random broadcast
   and listener keys protected for your current Windows account.
2. Double-click **Start-Relay.cmd**. The relay runs in the background on
   `http://127.0.0.1:17855`; keep this folder in place while it is running.
3. Use **Copy-Listener-Invite.cmd** and the broadcaster setup below to connect.
4. Double-click **Stop-Relay.cmd** when finished.

The setup shortcuts run the included `Airwave.Relay.exe`. They create no
firewall rules, services, scheduled tasks, tunnels, or public hosting. The relay
does not start automatically at login. Repeating setup preserves existing keys.
No PowerShell setup script or separate framework installation is required.

Host settings live in `%LOCALAPPDATA%\Airwave\RelayHost`, outside the extracted
package. Keys use Windows account protection; the folder permits access to that
account. They cannot be moved to a different account by copying the settings.
The helper passes keys to the relay's process environment, never as command-line
arguments or URLs. Administrators and software running as the same account can
still access a running process. No keys are printed to the console.

## Set up the broadcaster and listeners

Copy a listener invite, paste it in Airwave's **Connection** tab, and choose
**Use invite without connecting**. The invite supplies the relay address and
listener key. Copy the separate
broadcast key with **Copy-Broadcast-Key.cmd**, paste it into **Connection →
Broadcast key**, and choose **Save connection**. Choose **Broadcast → Go on air**.

Share the listener invite with your intended listeners. They paste it into
**Listen → Listener invite** and choose **Join broadcast** after you go on air.
Only the broadcaster needs the broadcast key. Clipboard actions intentionally
copy secrets: clear your clipboard after pasting if you use history or clipboard
synchronization. Never post a broadcast key or include keys in diagnostic logs.

The default invite works only on this PC. It cannot be used by remote listeners.

## Use a hosted TLS address

Run the relay and a TLS reverse proxy on the same host. Keep the relay on
loopback; expose only the proxy. For Internet access, arrange a domain pointing
to the host, reachability on the proxy's HTTPS port, and certificate issuance.
The Airwave setup helper does not provision any of these.

For example, a Caddy configuration for your own domain is:

```caddyfile
radio.example.org {
    reverse_proxy 127.0.0.1:17855 {
        flush_interval -1
        transport http {
            max_conns_per_host 130
        }
    }
}
```

Replace the example domain. Caddy supports WebSocket upgrades and can manage
HTTPS certificates when the domain and network requirements are met. Follow
[Caddy's automatic HTTPS requirements](https://caddyserver.com/docs/automatic-https)
and [reverse-proxy documentation](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy).
Configure connection and bandwidth limits appropriate to your host. Do not add
access logging that captures authorization headers or query contents. A proxy
reload may disconnect active listeners; do host maintenance between sets.

Once the TLS endpoint works, stop the relay and set the address placed in invites:

```powershell
./Airwave.Relay.exe --host-stop
./Airwave.Relay.exe --host-setup --public-url wss://radio.example.org
./Airwave.Relay.exe --host-start
```

This updates the invite address while keeping both keys and the loopback bind.
Copy a new invite after changing the address. Check `https://radio.example.org/health`
without any credentials, then test a real broadcast and listener from a different
network. A successful health response proves reachability, not audio delivery.
Clients must connect directly to the correct `wss://` address; authenticated
WebSocket connections do not follow redirects.

If using another proxy, preserve WebSocket upgrades and the Authorization header,
disable buffering, and use a loopback upstream on the same host. Avoid browser
login gates or extra authentication headers that replace Airwave's bearer key.
Do not expose the loopback HTTP port directly to remote clients. The relay sees
loopback proxy traffic as local; the proxy must enforce the public TLS boundary.

The operator can access audio in transit; this is TLS transport encryption,
not end-to-end encryption. Hosting also reveals client network addresses to the
host or its proxy. Choose a relay operator you trust.

## Manage the relay

From PowerShell in the extracted relay folder:

```powershell
./Airwave.Relay.exe --host-status
./Airwave.Relay.exe --host-copy-invite
./Airwave.Relay.exe --host-copy-broadcast-key
./Airwave.Relay.exe --host-copy-address
```

To revoke all existing access, stop the relay, run `--host-rotate-keys`, and start
it again. Copy the new broadcast key and listener invite; old copies will no
longer work. To use a different loopback port, stop the relay and run setup with
both `--public-url` and `--port`; update the reverse proxy's upstream port too.

The helper verifies the saved process identity before stopping it. If it cannot
identify that process, it leaves it alone and preserves the process receipt.
If a relay is still running from another extracted folder, use that folder's
Stop-Relay shortcut first. Host commands serialize access to the saved settings;
if another command is busy, wait for it to finish before retrying.
If startup fails, check whether the
chosen port is already in use and ensure you run under the account that created
the settings. Keep the full package together when upgrading. Stop the old relay
before replacing its files; host settings remain outside the package.

## Other hosting environments

The supplied portable archive targets Windows x64. For another platform, build
the included relay source with the .NET 10 SDK for that host's runtime identifier.
Provision two distinct keys through the service manager's secret environment:

- `AIRWAVE_PUBLISH_TOKEN`: the broadcaster key.
- `AIRWAVE_LISTEN_TOKEN`: the listener key.

Each key must encode at least 32 independently generated random bytes in
canonical base64url. Use the process environment, not URLs or command arguments.
Run `Airwave.Relay` with `--urls http://127.0.0.1:17855` behind a same-host TLS
proxy. A direct non-loopback bind additionally requires `AIRWAVE_ALLOW_REMOTE_BIND=1`
and explicit `--urls`; unencrypted non-loopback requests are rejected.

## Protocol and limits

`GET /health` returns readiness. The authenticated WebSocket routes are
`/v1/publish` and `/v1/listen`, with subprotocol `airwave.v1` and
`Authorization: Bearer ...`. Browser-origin requests and query strings are
rejected. Only one broadcaster may connect. No current broadcaster returns
HTTP 503, a second broadcaster returns 409, and full listener capacity returns
429. The wrong role key returns 401.

Messages carry 20 ms of 48 kHz audio. Opus packets are limited to 1,275 bytes.
A listener queue holds at most 12 frames (240 ms), and queued or blocked sends
are limited to 250 ms. A slow listener is disconnected rather than collecting
delayed audio. Network buffers and travel add further latency. Publisher
inactivity for five seconds, malformed frames, or invalid timing ends the
broadcast and disconnects listeners.

Transport checks run from the source archive with
`dotnet test Airwave.Transport.Tests/Airwave.Transport.Tests.csproj -c Release`.
They exercise real sockets, role separation, forwarding, fresh late joins,
malformed input, slow listeners, and credential-safe redirect refusal.
Protocol framing follows [RFC 6716 section 3](https://www.rfc-editor.org/rfc/rfc6716.html#section-3).

Airwave is AGPL-3.0-only. Supply the matching source archive with redistributed
binaries and make corresponding source available to users of your hosted relay.
Keep the included license and third-party notice files with the package.
