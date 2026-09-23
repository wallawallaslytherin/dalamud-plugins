# Airwave audio host

This Windows process captures the selected rekordbox process for a publisher,
encodes stereo Opus, or decodes one selected live stream to the default playback
device. Compressed audio and WASAPI run outside the game process. It stores no
audio files and never starts a session by itself.

The format is 48 kHz stereo, 960 samples per channel per packet (20 ms), at a
target of 192 kbps. Concentus 2.2.2 runs in managed mode; native codec discovery
is disabled. NAudio.Wasapi 3.1.0 supplies shared-mode playback. A listener keeps
one output device and PCM provider open for the entire session.

The receiver primes 160 ms of PCM before playing. Its queue cannot exceed
500 ms; exceeding the cap discards the oldest audio down to the target.
Starvation produces silence and reprimes the buffer. A bounded incoming packet
rate also limits decoding work from a nonconforming relay. A publisher's managed
capture queue holds 200 ms; dropping samples from that queue preserves their
sequence gap so the decoder can reset. The native capture helper has a separate
200 ms PCM queue. Its cumulative losses are reported separately during capture.
Raw PCM and asynchronous diagnostics do not identify each gap's position, so
native losses do not produce fabricated sequence gaps in the managed stream.
A stalled send fails after 500 ms. These limits bound application queues, not
physical device or Internet delay.

## Invocation

The plugin starts `Airwave.AudioHost.exe` with:

```text
--mode publish --parent-pid <game pid> --capture-pid <rekordbox pid> --capture-exe <absolute helper path>
--mode listen --parent-pid <game pid>
--mode test
```

Publish and listen receive their connection configuration as the first line on
standard input:

```json
{"RelayUrl":"wss://relay.example","Token":"<access key>","Volume":0.5}
```

Access keys never belong in command-line arguments or URLs. The relay must use
TLS except for a literal loopback address. Subsequent `{"Volume":0.5}` commands
change listener volume. The line `stop`, input EOF, parent exit, or the eight-hour
session limit terminates the session. Connection errors stop the session; the
host does not reconnect automatically. Capture that fails to produce one PCM
frame within two seconds also stops; normal capture emits silence while idle.
Publishers do not monitor their audio
through another local output device.

One initial status line and then one line per second are emitted as JSON on
standard output. `Stage` is `Starting`, `Connecting`, `Capturing`, `OnAir`,
`Buffering`, `Listening`, `Stopped`, `Error`, or `TestPassed`. Error messages are
fixed application messages; they do not repeat connection keys, URLs, or remote
exception text. Status writes have a two-second cancellation deadline. If an
inherited output pipe ignores cancellation, an independent watchdog terminates
the host after another 250 ms; its capture child also stops when its parent exits.
The host never retries a failed status writer or flushes it during teardown.

`FramesSent`, `FramesReceived`, `DroppedFrames` and `NonSilentFrames` count 20 ms
audio packets or their PCM equivalents. `Underruns` counts starvation episodes.
`DroppedFrames` covers the managed publisher queue, missing received packets,
and the listener PCM queue. It excludes PCM discarded inside the native capture
helper. `NativeCaptureDroppedFrames` separately counts discarded 48 kHz PCM
sample frames (48 frames per millisecond), not 20 ms packets. It is `null` until
the helper reports a valid cumulative count and in sessions without native
capture. The count is sampled every 250 ms and may lag the final overflow when
a process is forcibly stopped. A zero packet-drop count alone does not establish
lossless capture.
`BufferedMilliseconds` is the decoded PCM queue duration. `Peak` is the most
recent output/capture amplitude in the range 0–1. For listeners,
`FirstAudioMilliseconds` measures successful relay connection to the first
decoded PCM consumed by the output provider, including initial buffering. For
publishers it measures connection to the first sent audio packet. It does not
measure physical speaker or remote end-to-end latency.

The capture helper keeps stdout strictly PCM16LE. Its stderr emits bounded JSON
lines such as `{"event":"capture-stats","version":1,"droppedFrames":480}`.
Counts are cumulative within one capture process. A single pending diagnostic
snapshot replaces older snapshots; capture never waits for diagnostics to be
written. A blocked diagnostic pipe triggers the same bounded process exit as a
blocked audio pipe. The host drains stderr in fixed-size buffers, discards
oversized/malformed lines, and accepts only monotonic version-1 counts. It does
not retain raw diagnostics or copy them into status errors.

## Build and verification

From the Airwave solution directory:

```text
dotnet build Airwave.AudioHost/Airwave.AudioHost.csproj -c Release -warnaserror
dotnet test Airwave.Audio.Tests/Airwave.Audio.Tests.csproj -c Release -warnaserror
Airwave.AudioHost/bin/Release/net10.0-windows/Airwave.AudioHost.exe --mode test
```

Test mode encodes synthetic stereo tones, validates the wire representation,
decodes them, and consumes the result through the real bounded PCM provider. It
opens neither a network connection nor a capture/playback device.

The integration harness may use `--mode synthetic-publish` to generate tones
over a real relay and `--mode listen --no-output` to consume decoded PCM on a
20 ms clock without speakers. Those modes are test interfaces and are not
offered by the plugin's broadcast UI. Real capture and audible playback require
separate runtime verification.

Dependencies: [Concentus](https://www.nuget.org/packages/Concentus/2.2.2) and
[NAudio.Wasapi](https://www.nuget.org/packages/NAudio.Wasapi/3.1.0).
