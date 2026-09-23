# Airwave

Airwave sends live rekordbox audio to listeners in FFXIV. Listeners join the
current moment of the set, without waiting for a recording or download.
Both the broadcaster and listeners need Airwave; existing appearance syncing
can continue separately.

## Install

Use the complete Windows x64 plugin package with Dalamud API 15. The audio
engine and local relay include their runtimes: **no separate .NET installation
is needed**. Keep the `audio`, `capture`, `relay`, and `licenses` folders beside
`Airwave.dll` and `Airwave.json`. Extract the ZIP fully before installing it.
Source archives are for building, not installation.

To install the portable package through Dalamud's developer-location UI:

1. Extract it to a folder you intend to keep, such as `C:\Games\Airwave`.
2. Open Dalamud Settings with `/xlsettings`, choose **Experimental**, and enable
   **Enable Developer Mode** if the developer settings are hidden.
3. Under **Dev Plugin Locations**, choose **Select Dev Plugin DLL** and select
   the extracted `Airwave.dll`. Keep the location enabled and save the settings.
4. Open the plugin installer with `/xlplugins`, use **Scan Dev Plugins** if
   needed, and enable Airwave in its installed developer-plugin entry.

Keep the extracted folder in place. To update a portable installation, stop all
audio and disable Airwave, replace the complete package, then enable it again.
Do not register two copies at once.

Open `/airwave` after the plugin is installed. The window and all audio sessions
stay closed on startup. Airwave never resumes a broadcast automatically.

## Listen to a broadcast

1. Get a listener invite from the broadcaster.
2. Open `/airwave`, choose **Listen**, and paste the **Listener invite**.
3. Choose **Join broadcast**. Adjust **Playback volume** at the top of the window.

The broadcaster must already be on air. Airwave saves the connection; use
**Join broadcast** again to reconnect. Choose **Stop all audio** or use
`/airwave off` to end the session.

## Broadcast your set

1. Start rekordbox. If your controller uses ASIO only, enable rekordbox's
   **PC MASTER OUT** so the set also reaches its Windows audio output.
2. First try **Broadcast → Start local sound test**. This starts capture, a
   relay on this PC, and listening together. Keep playback volume low if you
   already hear the direct rekordbox output. Choose **Stop all audio** afterward.
3. For remote listeners, obtain a hosted Airwave relay, its listener invite,
   and its separate broadcast key. Paste the invite in **Connection** and choose
   **Use invite without connecting**. Enter the **Broadcast key**, then choose
   **Save connection**.
4. Choose **Broadcast → Go on air**, then **Copy listener invite**. Send that
   invite only to the people you want to admit.

The relay host instructions include a simple broadcaster setup sequence in
[Airwave relay](Airwave.Relay/README.md). Capture includes only rekordbox and its
child processes. There is no desktop-mix or microphone fallback. Broadcasting
and the local sound test require Windows 11 x64 or later (process-loopback
capture requires Windows build 20348 or newer). Listening works on Windows 10
x64 or later. See [Microsoft's process-loopback requirements](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/).

The local sound test works only on this PC. Remote listeners need reachable
hosting with a valid TLS certificate; the plugin does not create public hosting.
The optional relay package supplies the server and setup helpers.

## Connections and keys

The relay address is a base `wss://` address such as `wss://radio.example.org`.
The listener key admits listeners. A different broadcast key controls the single
current broadcaster. No Discord or other account is involved.

An invite contains the relay address and listener key. Anyone holding it can
listen. It never includes the broadcast key. The relay operator can rotate keys
to revoke old access. Saved plugin keys are protected for the current Windows
account. Clipboard actions deliberately copy a key or invite: clear your
clipboard after pasting if you use clipboard history or synchronization.

Connections to remote relays use TLS. The relay operator can read the audio in
transit; Airwave does not provide end-to-end encryption. Connecting also reveals
the client's network address to that relay. Airwave has no analytics or automatic
updates, and does not upload player locations.

## Troubleshooting

- **No sound in the local test:** start playback in rekordbox, enable PC MASTER
  OUT if needed, check the audio level, and check Playback volume and the Windows
  output device. **Check audio engine** runs a silent codec/buffer check; it does
  not prove rekordbox capture or speaker output.
- **Cannot join:** the broadcaster must be on air. Check the relay address and
  listener key; a broadcast key cannot be used to listen. Ask the relay operator
  to check the endpoint if the address or certificate fails.
- **Cannot go on air:** check the separate broadcast key and ensure another
  broadcaster is not already using that relay.
- **Disconnected:** reconnect with **Join broadcast** after the cause is fixed.
  Slow listeners are disconnected to prevent growing audio delay. Network errors
  stop the session visibly.
- **Helper missing or cannot start:** extract the complete package again. Keep
  all helper folders together; moving just the main DLL is insufficient.

`/airwave status` reports the current state. `/airwave test` runs the silent
engine check; `/airwave on` broadcasts, `/airwave listen` joins a configured
broadcast, and `/airwave off` stops everything. `/airwave local` runs the combined
local sound test, the same as **Start local sound test**.

## Audio behavior

Audio uses 48 kHz stereo Opus at 192 kbps, in 20 ms packets. Playback primes
160 ms of audio and caps its PCM queue at 500 ms. Capture, encoding, the network,
and the playback device add delay; these buffer limits do not promise a fixed
end-to-end Internet latency.

Audio stays in memory. Airwave does not record sets. Helpers stop on plugin
unload, game exit, explicit stop, source exit, or prolonged loss of progress.
The audio engine runs in its own process. Packet sizes, formats, rates, and
queues are bounded.

## Build and package

Building from source requires Windows x64, PowerShell 7, .NET 10 SDK, Dalamud
API 15 assemblies, CMake, Visual Studio C++ tools, and a recent Windows SDK.
Set `DalamudLibPath` when the Dalamud assemblies are outside the usual profile.
These are development requirements; recipients use the bundled helpers.

```powershell
./Build.ps1
./Package.ps1
```

`Build.ps1` builds and runs the transport, audio, and lifecycle checks, then
writes the complete payload to `artifacts/plugin`. Audio and relay helpers are
self-contained `win-x64` publishes, with separate runtime files, no trimming,
and no symbols. Runtime notice files are taken from the exact restored packs.
Native CMake intermediates use the local application-data cache under
`Airwave/Build/CaptureHost`; `-NativeBuildDirectory` selects another cache
outside the source tree.

`Package.ps1` creates the plugin ZIP, optional standalone relay ZIP, corresponding
source ZIP, and `SHA256SUMS.txt` in `artifacts/packages`. It checks every runtime
file against the build inventory. The source ZIP uses `SourceFiles.txt`; update
that explicit list when adding source files. Generated outputs and local host
settings are not source inputs. Distribute the matching source ZIP alongside
the binaries to provide the corresponding source under the AGPL license.

Transport tests use real loopback sockets. Audio tests cover the codec, bounded
buffers, and subprocess control. Lifecycle tests cover protected settings and
helper cleanup. The capture helper's explicit `--self-test` plays brief owned
tones to test process isolation; ordinary build tests stay silent.

## License

Airwave is AGPL-3.0-only; see [LICENSE.md](LICENSE.md). The capture helper retains
its adaptation provenance in [its README](Airwave.CaptureHost/README.md).
Bundled dependencies retain their licenses and attribution in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and `licenses`.
