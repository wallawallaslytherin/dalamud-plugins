# Airwave

Broadcast live rekordbox audio to listeners in FFXIV. Listeners join the current
moment of the set without waiting for a recording or download. Both the
broadcaster and listeners need Airwave.

## Install

1. Open `/xlsettings` in FFXIV with Dalamud loaded.
2. Choose **Experimental → Custom Plugin Repositories**.
3. Add this URL, enable its entry, and save:

   ```text
   https://raw.githubusercontent.com/wallawallaslytherin/dalamud-plugins/main/pluginmaster.json
   ```

4. Open `/xlplugins`, find **Airwave** under **All Plugins**, and install it.
5. Open `/airwave`. Audio stays off until you choose to start it.

Requires Windows x64 and Dalamud API 15. Broadcasting and the local sound test
require Windows 11 or newer; listening works on Windows 10 or newer. The helpers
include their runtimes, so no separate .NET installation is needed. Install
future updates through Dalamud's plugin installer.

## Listen

Get a listener invite from the broadcaster. In `/airwave`, choose **Listen**,
paste the **Listener invite**, and choose **Join broadcast**. The broadcaster
must already be on air. Adjust **Playback volume** at the top of the window.

Use **Join broadcast** again to reconnect with the saved connection. **Stop all
audio** or `/airwave off` ends the session.

## Broadcast

1. Start playback in rekordbox. Enable **PC MASTER OUT** if your controller uses
   ASIO only, so rekordbox also sends audio to its Windows output.
2. Try **Broadcast → Start local sound test** first. It starts capture and
   listening on this PC. Keep playback volume low if you already hear rekordbox
   directly, then choose **Stop all audio** when finished.
3. For remote listeners, obtain a hosted relay, its listener invite, and its
   separate broadcast key. Paste the invite in **Connection**, choose **Use
   invite without connecting**, enter the **Broadcast key**, and **Save connection**.
4. Choose **Broadcast → Go on air**, then **Copy listener invite** and share it
   with your intended listeners.

The local sound test is limited to this PC. Remote listening requires a reachable
relay with a valid TLS certificate and a `wss://` address. Airwave does not create
public hosting automatically. The optional relay package includes setup and
start/stop shortcuts; see the [relay hosting guide](source/Airwave/Airwave.Relay/README.md).

Anyone holding a listener invite can listen. Keep the separate broadcast key
private. The relay operator can access audio in transit: TLS protects the
connection, but does not provide end-to-end encryption. Airwave does not record
sets or resume audio automatically on startup.

## Version 0.1.0.0

- [Windows x64 plugin package](0.1.0.0/Airwave-0.1.0.0-win-x64.zip)
- [Optional Windows x64 relay package](0.1.0.0/Airwave-Relay-0.1.0.0-win-x64.zip)
- [Matching source archive](0.1.0.0/Airwave-0.1.0.0-source.zip)
- [SHA-256 checksums](0.1.0.0/SHA256SUMS.txt)

See the [full user guide](source/Airwave/README.md) for troubleshooting, portable
installation, commands, and build instructions. The [source tree](source/Airwave/)
and corresponding source archive accompany the binaries.

Airwave is [AGPL-3.0-only](source/Airwave/LICENSE.md). Bundled components retain
their own [licenses and notices](source/Airwave/THIRD-PARTY-NOTICES.md).
