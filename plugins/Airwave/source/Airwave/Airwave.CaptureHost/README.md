# Airwave Capture

Windows process-loopback audio capture for an explicitly selected `rekordbox.exe`
process and its child processes. The helper captures no microphone or general
desktop mix and writes no audio files.

```
Airwave.Capture.exe --pid REKORDBOX_PID --parent-pid LAUNCHING_PARENT_PID
```

The launching process must redirect and continuously consume standard output and
standard error. Standard output contains only raw little-endian signed PCM:
48,000 frames per second, stereo, 16 bits per sample, four bytes per frame. Reads
can end at arbitrary byte boundaries; the consumer must assemble complete audio
frames. Standard error carries bounded JSON capture statistics and errors.
Version 1 `capture-stats` messages report cumulative `droppedFrames` in 48 kHz
PCM sample frames during capture, normally every 250 ms. The pipe is coalesced
and bounded; its consumer must keep reading. Counts do not identify the position
of a gap within raw stdout, and a forced stop can miss the last report interval.

The parent PID must match the actual launching process. The helper verifies the
target executable, watches source and parent lifetime, and stops after at most
eight hours. Stop the helper process when the listening or broadcast session
ends. It creates no child processes during ordinary capture.

The writer retains at most 200 ms of PCM, including its in-progress write, and
discards oldest complete queued frames if its consumer falls behind. A blocked
pipe causes process exit code 4 within approximately two seconds. Other failures
return code 1. No stdout headers, WAV containers, recordings, or network sockets
are created.

## Build and test

Use a Visual Studio x64 C++ toolchain and Windows SDK with process-loopback audio
support:

```powershell
$captureBuild = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Airwave\Build\CaptureHost'
cmake -S . -B $captureBuild -A x64
cmake --build $captureBuild --config Release
& (Join-Path $captureBuild 'Release\Airwave.Capture.exe') --self-test
```

The explicit self-test briefly plays owned synthetic tones. It checks that the
selected 440 Hz process is captured while an unrelated 880 Hz process is
excluded, continuous idle silence, source and parent exit, queue bounds, and
blocked-output termination, live drop reporting, and blocked-diagnostic termination.
Samples remain in memory only. The synthetic child
modes do not provide a way to capture arbitrary user-supplied process IDs.

## License and provenance

The Windows process-loopback activation and owned synthetic test foundations
were adapted from a locally developed capture helper in the AGPL-3.0 Pulsar
project. The continuous bounded pipe transport is new. This helper is licensed
under AGPL-3.0-only; see `LICENSE.md`. Pulsar's upstream project is
<https://github.com/Drovolon/Pulsar>.
