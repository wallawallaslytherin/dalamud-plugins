# Process integration trial

This executable runs a real loopback relay, a separate audio publisher process,
and two separate listener processes. The default source is a synthetic stereo
tone and both listeners use a paced headless sink; no speaker output or rekordbox
capture occurs by default. The second listener joins after five seconds.

```text
dotnet build Airwave.Integration.Tests/Airwave.Integration.Tests.csproj -c Release
Airwave.Integration.Tests/bin/Release/net10.0-windows/Airwave.Integration.Tests.exe --seconds 30
```

The trial prints only JSON metrics and exits nonzero on a failed acceptance
check. It requires nonzero decoded audio, first playback less than one second
after each listener connects, at least five seconds of continuous observed
playback, zero underruns or dropped frames, and a PCM buffer within 500 ms.
It stops the publisher and verifies both listeners terminate. A successful
default trial covers software decoding and playback pacing; it does not prove
physical speaker output, Internet connectivity, or Internet latency.

Use `--audio-host <absolute executable path>` to test a deployed payload. The
trial copies that host's runtime into its own temporary directory so builds can
continue without replacing running binaries. It supplies access keys through
stdin, uses only an ephemeral loopback address, and does not print secrets.

Actual rekordbox capture requires both `--capture-pid <pid>` and
`--capture-exe <absolute capture helper path>`. The capture helper verifies the
target is rekordbox. Add `--wasapi` only when actual speaker playback is wanted;
that option enables the initial listener's audio output, while the late listener
remains headless.

No audio recordings are created. The trial isolates the hosts' working directory
and temporary-directory environment, watches that directory for file activity,
and reports those observed file counts. This is a scoped check, not a systemwide
file-I/O audit. Frozen runtime copies are created before observation and removed
after processes stop.
