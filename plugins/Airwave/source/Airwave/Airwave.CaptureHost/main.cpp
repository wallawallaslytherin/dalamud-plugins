// SPDX-License-Identifier: AGPL-3.0-only
#include <windows.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <mmsystem.h>
#include <tlhelp32.h>
#include <wrl.h>
#include <wrl/implements.h>
#include <io.h>
#include <fcntl.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <filesystem>
#include <iostream>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

using Microsoft::WRL::ComPtr;
constexpr uint32_t SampleRate = 48000, Channels = 2, FrameBytes = 4;
constexpr uint32_t ChunkFrames = 480, MaxBufferedFrames = 9600;
std::atomic<bool> StopRequested{false};

struct Handle {
    HANDLE value = nullptr;
    explicit Handle(HANDLE h = nullptr) : value(h) {}
    ~Handle() { Reset(); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
    void Reset() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); value = nullptr; }
};
struct ComApartment {
    ComApartment() { if (FAILED(CoInitializeEx(nullptr, COINIT_MULTITHREADED))) throw std::runtime_error("COM initialization failed"); }
    ~ComApartment() { CoUninitialize(); }
};
void Check(HRESULT result, const char* operation) {
    if (FAILED(result)) { std::ostringstream detail; detail << operation << " failed: 0x" << std::hex << static_cast<uint32_t>(result); throw std::runtime_error(detail.str()); }
}
std::string Json(const std::string& value) {
    std::string result = "\"";
    for (const unsigned char character : value.substr(0, 240)) {
        if (character == '\\' || character == '"') { result += '\\'; result += character; }
        else if (character >= 32) result += character;
        else result += ' ';
    }
    return result + '"';
}
BOOL WINAPI OnCtrl(DWORD type) {
    if (type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT || type == CTRL_CLOSE_EVENT) { StopRequested = true; return TRUE; }
    return FALSE;
}
uint32_t Number(const wchar_t* value, uint32_t minimum, uint32_t maximum) {
    const std::wstring text(value);
    if (text.empty() || text.find_first_not_of(L"0123456789") != std::wstring::npos) throw std::runtime_error("Invalid numeric option");
    const auto number = std::stoull(text);
    if (number < minimum || number > maximum) throw std::runtime_error("Numeric option outside allowed range");
    return static_cast<uint32_t>(number);
}
std::wstring ExecutableOf(HANDLE process) {
    std::wstring path(32768, L'\0'); DWORD length = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process, 0, path.data(), &length)) throw std::runtime_error("Unable to verify process executable");
    path.resize(length); return path;
}
DWORD ActualParentPid() {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (snapshot.value == INVALID_HANDLE_VALUE) throw std::runtime_error("Unable to verify launching parent");
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot.value, &entry)) do {
        if (entry.th32ProcessID == GetCurrentProcessId()) return entry.th32ParentProcessID;
    } while (Process32NextW(snapshot.value, &entry));
    throw std::runtime_error("Launching parent is unavailable");
}
WAVEFORMATEX Format() {
    WAVEFORMATEX result{}; result.wFormatTag = WAVE_FORMAT_PCM; result.nChannels = Channels;
    result.nSamplesPerSec = SampleRate; result.wBitsPerSample = 16; result.nBlockAlign = FrameBytes;
    result.nAvgBytesPerSec = SampleRate * FrameBytes; return result;
}

// Diagnostics have a single coalescing slot. The producer never waits for pipe
// I/O or queues an unbounded history. A cumulative count survives coalescing.
class DiagnosticOutput {
    HANDLE pipe;
    std::mutex mutex;
    std::condition_variable ready;
    std::string pending;
    bool stopping = false;
    std::atomic<bool> watchStopped{false}, writing{false};
    std::atomic<ULONGLONG> progress{0};
    std::atomic<bool> failed{false};
    std::thread writer, watchdog;
    void WriteLoop() {
        for (;;) {
            std::string message;
            {
                std::unique_lock lock(mutex);
                ready.wait(lock, [&] { return stopping || !pending.empty(); });
                if (pending.empty()) return;
                message = std::move(pending); pending.clear();
            }
            progress = GetTickCount64(); writing = true;
            size_t offset = 0;
            while (offset < message.size()) {
                DWORD written = 0;
                if (!WriteFile(pipe, message.data() + offset, static_cast<DWORD>(message.size() - offset), &written, nullptr) || !written) {
                    failed = true; writing = false; return;
                }
                offset += written; progress = GetTickCount64();
            }
            writing = false;
        }
    }
public:
    explicit DiagnosticOutput(HANDLE output) : pipe(output) {
        writer = std::thread([this] { WriteLoop(); });
        try {
            watchdog = std::thread([this] {
                while (!watchStopped) {
                    if (writing && GetTickCount64() - progress.load() >= 1800)
                        TerminateProcess(GetCurrentProcess(), 4);
                    Sleep(20);
                }
            });
        } catch (...) {
            { std::lock_guard lock(mutex); stopping = true; }
            ready.notify_all(); writer.join(); throw;
        }
    }
    ~DiagnosticOutput() {
        { std::lock_guard lock(mutex); stopping = true; }
        ready.notify_all();
        const auto cancelAt = GetTickCount64() + 100;
        const auto deadline = cancelAt + 1000;
        while (WaitForSingleObject(writer.native_handle(), 0) != WAIT_OBJECT_0) {
            if (GetTickCount64() >= cancelAt) CancelSynchronousIo(writer.native_handle());
            if (GetTickCount64() >= deadline) TerminateProcess(GetCurrentProcess(), 4);
            WaitForSingleObject(writer.native_handle(), 10);
        }
        writer.join(); watchStopped = true; watchdog.join();
    }
    void Publish(std::string message) {
        if (message.size() > 511) return;
        // Losing an intermediate snapshot is safe: the next one is cumulative.
        std::unique_lock lock(mutex, std::try_to_lock);
        if (!lock.owns_lock() || stopping) return;
        pending = std::move(message); pending += '\n'; ready.notify_one();
    }
    void CheckConsumer() const {
        if (failed) throw std::runtime_error("Capture diagnostic consumer closed or failed");
    }
};

// The capture thread never waits for its consumer. At most 200 ms of PCM is
// retained here, including the current write. Overflow discards the oldest
// complete frames. A non-reading consumer cannot orphan a blocked writer.
class PipeOutput {
    HANDLE pipe;
    std::mutex mutex;
    std::condition_variable ready;
    std::deque<std::vector<BYTE>> queue;
    uint32_t queuedFrames = 0, writingFrames = 0;
    bool stopping = false;
    std::atomic<bool> watchStopped{false}, writing{false};
    std::atomic<ULONGLONG> progress{0};
    std::atomic<DWORD> failure{0};
    std::thread writer, watchdog;
    void WriteLoop() {
        for (;;) {
            std::vector<BYTE> chunk;
            {
                std::unique_lock lock(mutex);
                ready.wait(lock, [&] { return stopping || !queue.empty(); });
                if (stopping) return;
                chunk = std::move(queue.front()); queue.pop_front();
                writingFrames = static_cast<uint32_t>(chunk.size()) / FrameBytes;
                queuedFrames -= writingFrames;
            }
            progress = GetTickCount64(); writing = true;
            size_t offset = 0;
            while (offset < chunk.size()) {
                DWORD written = 0;
                if (!WriteFile(pipe, chunk.data() + offset, static_cast<DWORD>(chunk.size() - offset), &written, nullptr) || !written) {
                    failure = GetLastError() ? GetLastError() : ERROR_WRITE_FAULT;
                    writing = false; return;
                }
                offset += written; progress = GetTickCount64();
            }
            writing = false;
            { std::lock_guard lock(mutex); writingFrames = 0; }
        }
    }
public:
    uint64_t totalFrames = 0, droppedFrames = 0, signalSamples = 0;
    uint32_t peakSample = 0, maximumBufferedFrames = 0;
    explicit PipeOutput(HANDLE output) : pipe(output) {
        if (!pipe || pipe == INVALID_HANDLE_VALUE || GetFileType(pipe) != FILE_TYPE_PIPE)
            throw std::runtime_error("Audio stdout must be a redirected pipe");
        SetHandleInformation(pipe, HANDLE_FLAG_INHERIT, 0);
        writer = std::thread([this] { WriteLoop(); });
        try {
            watchdog = std::thread([this] {
                while (!watchStopped) {
                    if (writing && GetTickCount64() - progress.load() >= 1800) {
                        // Process termination is intentional: an OS pipe write that
                        // cannot finish must never hold capture alive indefinitely.
                        TerminateProcess(GetCurrentProcess(), 4);
                    }
                    Sleep(20);
                }
            });
        } catch (...) {
            { std::lock_guard lock(mutex); stopping = true; }
            ready.notify_all(); writer.join(); throw;
        }
    }
    ~PipeOutput() {
        { std::lock_guard lock(mutex); stopping = true; queue.clear(); }
        ready.notify_all();
        const auto deadline = GetTickCount64() + 1000;
        while (WaitForSingleObject(writer.native_handle(), 0) != WAIT_OBJECT_0) {
            // Retry cancellation across the small gap between dequeue and the
            // next synchronous write, rather than missing that write entirely.
            CancelSynchronousIo(writer.native_handle());
            if (GetTickCount64() >= deadline) TerminateProcess(GetCurrentProcess(), 4);
            WaitForSingleObject(writer.native_handle(), 10);
        }
        writer.join(); watchStopped = true; watchdog.join();
    }
    void CheckConsumer() const {
        if (failure) throw std::runtime_error("Audio consumer closed or failed");
    }
    void Append(const BYTE* data, uint32_t frames, bool silent) {
        CheckConsumer();
        if (frames > SampleRate) throw std::runtime_error("Capture packet exceeded one second");
        for (uint32_t offset = 0; offset < frames;) {
            const uint32_t count = std::min(ChunkFrames, frames - offset);
            std::vector<BYTE> chunk(count * FrameBytes, 0);
            if (!silent && data) std::copy_n(data + offset * FrameBytes, chunk.size(), chunk.data());
            for (uint32_t i = 0; i < count * Channels; ++i) {
                const auto magnitude = static_cast<uint32_t>(std::abs(static_cast<int>(reinterpret_cast<const int16_t*>(chunk.data())[i])));
                if (magnitude > 8) ++signalSamples;
                peakSample = std::max(peakSample, magnitude);
            }
            {
                std::lock_guard lock(mutex);
                while (queuedFrames + writingFrames + count > MaxBufferedFrames && !queue.empty()) {
                    const auto removed = static_cast<uint32_t>(queue.front().size()) / FrameBytes;
                    queuedFrames -= removed; droppedFrames += removed; queue.pop_front();
                }
                queue.push_back(std::move(chunk)); queuedFrames += count;
                maximumBufferedFrames = std::max(maximumBufferedFrames, queuedFrames + writingFrames);
            }
            totalFrames += count; offset += count; ready.notify_one();
        }
    }
};

std::string CaptureStatistics(const PipeOutput& output, uint64_t discontinuities, const std::string& reason = "active") {
    std::ostringstream message;
    message << "{\"event\":\"capture-stats\",\"version\":1,\"droppedFrames\":" << output.droppedFrames
        << ",\"frames\":" << output.totalFrames << ",\"signalSamples\":" << output.signalSamples
        << ",\"peakSample\":" << output.peakSample << ",\"maximumBufferedFrames\":" << output.maximumBufferedFrames
        << ",\"discontinuities\":" << discontinuities << ",\"reason\":" << Json(reason) << "}";
    return message.str();
}

class Activation final : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, Microsoft::WRL::FtmBase, IActivateAudioInterfaceCompletionHandler> {
public:
    Handle done{CreateEventW(nullptr, FALSE, FALSE, nullptr)};
    HRESULT result = E_PENDING;
    ComPtr<IAudioClient> audio;
    STDMETHOD(ActivateCompleted)(IActivateAudioInterfaceAsyncOperation* operation) override {
        ComPtr<IUnknown> unknown; HRESULT activated = E_UNEXPECTED;
        result = operation->GetActivateResult(&activated, &unknown);
        if (SUCCEEDED(result)) result = activated;
        if (SUCCEEDED(result)) result = unknown.As(&audio);
        SetEvent(done.value); return S_OK;
    }
};
struct CaptureOptions { DWORD pid = 0, parentPid = 0; uint32_t durationMs = 28800000; };
std::string Capture(const CaptureOptions& options, PipeOutput& output, bool ownedSyntheticTest = false) {
    DiagnosticOutput diagnostics(GetStdHandle(STD_ERROR_HANDLE));
    if (!ownedSyntheticTest && options.parentPid != ActualParentPid())
        throw std::runtime_error("Parent PID must identify the process that launched Airwave Capture");
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, FALSE, options.pid));
    Handle parent(OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, FALSE, options.parentPid));
    if (!process.value || !parent.value) throw std::runtime_error("Target or launching parent is unavailable");
    const auto executable = std::filesystem::path(ExecutableOf(process.value)).filename().wstring();
    if (!ownedSyntheticTest && _wcsicmp(executable.c_str(), L"rekordbox.exe"))
        throw std::runtime_error("Capture permits only rekordbox.exe");
    if (WaitForSingleObject(process.value, 0) != WAIT_TIMEOUT || WaitForSingleObject(parent.value, 0) != WAIT_TIMEOUT)
        throw std::runtime_error("Target or launching parent has already exited");
    auto activation = Microsoft::WRL::Make<Activation>();
    if (!activation || !activation->done.value) throw std::runtime_error("Activation event creation failed");
    AUDIOCLIENT_ACTIVATION_PARAMS parameters{};
    parameters.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
    parameters.ProcessLoopbackParams.TargetProcessId = options.pid;
    parameters.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
    PROPVARIANT property{}; property.vt = VT_BLOB; property.blob.cbSize = sizeof(parameters);
    property.blob.pBlobData = reinterpret_cast<BYTE*>(&parameters);
    ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
    Check(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &property, activation.Get(), &operation), "Process-loopback activation");
    HANDLE activationWaits[]{activation->done.value, process.value, parent.value};
    if (WaitForMultipleObjects(3, activationWaits, FALSE, 2000) != WAIT_OBJECT_0)
        throw std::runtime_error("Process-loopback activation timed out or source/parent exited");
    Check(activation->result, "Process-loopback activation result");
    auto audio = activation->audio; auto format = Format();
    Check(audio->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM, 0, 0, &format, nullptr), "Process-loopback initialization");
    Handle ready(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    if (!ready.value) throw std::runtime_error("Capture event creation failed");
    Check(audio->SetEventHandle(ready.value), "SetEventHandle");
    ComPtr<IAudioCaptureClient> capture; Check(audio->GetService(IID_PPV_ARGS(&capture)), "Get capture service");
    Check(audio->Start(), "Start capture");
    struct AudioGuard { IAudioClient* client; ~AudioGuard() { client->Stop(); } } guard{audio.Get()};
    const auto deadline = GetTickCount64() + options.durationMs;
    auto nextReport = GetTickCount64() + 250;
    uint64_t discontinuities = 0;
    diagnostics.Publish(CaptureStatistics(output, discontinuities));
    std::string reason = "duration-limit";
    HANDLE waits[]{ready.value, process.value, parent.value};
    while (GetTickCount64() < deadline) {
        if (StopRequested) { reason = "requested"; break; }
        if (WaitForSingleObject(process.value, 0) == WAIT_OBJECT_0) { reason = "target-exited"; break; }
        if (WaitForSingleObject(parent.value, 0) == WAIT_OBJECT_0) { reason = "parent-exited"; break; }
        output.CheckConsumer();
        diagnostics.CheckConsumer();
        if (GetTickCount64() >= nextReport) {
            diagnostics.Publish(CaptureStatistics(output, discontinuities));
            nextReport = GetTickCount64() + 250;
        }
        const auto waited = WaitForMultipleObjects(3, waits, FALSE, 50);
        if (waited == WAIT_OBJECT_0 + 1) { reason = "target-exited"; break; }
        if (waited == WAIT_OBJECT_0 + 2) { reason = "parent-exited"; break; }
        if (waited == WAIT_FAILED) throw std::runtime_error("Capture wait failed");
        UINT32 packet = 0; Check(capture->GetNextPacketSize(&packet), "GetNextPacketSize");
        while (packet) {
            BYTE* bytes = nullptr; UINT32 frames = 0; DWORD flags = 0;
            Check(capture->GetBuffer(&bytes, &frames, &flags, nullptr, nullptr), "GetBuffer");
            try {
                if (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) ++discontinuities;
                output.Append(bytes, frames, (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
            } catch (...) { capture->ReleaseBuffer(frames); throw; }
            Check(capture->ReleaseBuffer(frames), "ReleaseBuffer");
            Check(capture->GetNextPacketSize(&packet), "GetNextPacketSize");
        }
    }
    diagnostics.Publish(CaptureStatistics(output, discontinuities, reason));
    return reason;
}

int Tone(uint32_t frequency, uint32_t milliseconds) {
    Sleep(300); auto format = Format(); HWAVEOUT output{};
    if (waveOutOpen(&output, WAVE_MAPPER, &format, 0, 0, CALLBACK_NULL) != MMSYSERR_NOERROR)
        throw std::runtime_error("Synthetic tone output unavailable");
    const uint32_t frames = SampleRate * milliseconds / 1000;
    std::vector<int16_t> samples(frames * Channels);
    for (uint32_t i = 0; i < frames; ++i) {
        auto value = static_cast<int16_t>(1200 * std::sin(2 * 3.141592653589793 * frequency * i / SampleRate));
        samples[i * Channels] = value; samples[i * Channels + 1] = value;
    }
    WAVEHDR header{}; header.lpData = reinterpret_cast<char*>(samples.data()); header.dwBufferLength = static_cast<DWORD>(samples.size() * sizeof(int16_t));
    if (waveOutPrepareHeader(output, &header, sizeof(header)) || waveOutWrite(output, &header, sizeof(header))) {
        waveOutClose(output); throw std::runtime_error("Synthetic tone playback failed");
    }
    while (!(header.dwFlags & WHDR_DONE)) Sleep(10);
    waveOutUnprepareHeader(output, &header, sizeof(header)); waveOutClose(output); return 0;
}
struct Child {
    PROCESS_INFORMATION info{};
    explicit Child(const std::wstring& arguments) {
        wchar_t module[32768]{};
        if (!GetModuleFileNameW(nullptr, module, 32768)) throw std::runtime_error("Unable to locate test executable");
        std::wstring command = L"\"" + std::wstring(module) + L"\" " + arguments;
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        if (!CreateProcessW(module, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &startup, &info))
            throw std::runtime_error("Unable to start owned synthetic test process");
    }
    ~Child() {
        if (WaitForSingleObject(info.hProcess, 100) != WAIT_OBJECT_0) { TerminateProcess(info.hProcess, 5); WaitForSingleObject(info.hProcess, 1000); }
        CloseHandle(info.hThread); CloseHandle(info.hProcess);
    }
};
struct TestPipe {
    Handle read, write;
    TestPipe() {
        if (!CreatePipe(&read.value, &write.value, nullptr, 4096)) throw std::runtime_error("Unable to create test pipe");
    }
};
double Amplitude(const std::vector<int16_t>& samples, double frequency) {
    double cosine = 0, sine = 0;
    const auto frames = samples.size() / Channels;
    for (size_t i = 0; i < frames; ++i) {
        const double phase = 2 * 3.141592653589793 * frequency * static_cast<double>(i) / SampleRate;
        cosine += samples[i * Channels] * std::cos(phase); sine += samples[i * Channels] * std::sin(phase);
    }
    return frames ? 2 * std::sqrt(cosine * cosine + sine * sine) / static_cast<double>(frames) : 0;
}
struct TestResult { std::vector<int16_t> samples; std::string reason; uint64_t frames = 0, dropped = 0, elapsed = 0; uint32_t maximumBuffered = 0; };
TestResult CaptureTest(const CaptureOptions& options) {
    TestPipe pipe; TestResult result;
    std::thread reader([&] {
        std::vector<BYTE> bytes; BYTE chunk[4096]; DWORD count = 0;
        while (ReadFile(pipe.read.value, chunk, sizeof(chunk), &count, nullptr) && count) {
            if (bytes.size() + count > SampleRate * FrameBytes * 6) break;
            bytes.insert(bytes.end(), chunk, chunk + count);
        }
        result.samples.resize(bytes.size() / sizeof(int16_t));
        std::copy_n(bytes.data(), result.samples.size() * sizeof(int16_t), reinterpret_cast<BYTE*>(result.samples.data()));
    });
    try {
        const auto before = GetTickCount64();
        {
            PipeOutput output(pipe.write.value);
            result.reason = Capture(options, output, true);
            result.frames = output.totalFrames; result.dropped = output.droppedFrames;
            result.maximumBuffered = output.maximumBufferedFrames;
            Sleep(30);
        }
        result.elapsed = GetTickCount64() - before;
    } catch (...) { pipe.write.Reset(); reader.join(); throw; }
    pipe.write.Reset(); reader.join(); return result;
}
int BlockedPipeTestChild() {
    TestPipe pipe; PipeOutput output(pipe.write.value);
    for (int i = 0; i < 300; ++i) { output.Append(nullptr, ChunkFrames, true); Sleep(10); }
    return 7;
}
int BlockedDiagnosticTestChild() {
    TestPipe pipe; DiagnosticOutput diagnostics(pipe.write.value);
    for (int i = 0; i < 300; ++i) { diagnostics.Publish(std::string(500, 'x')); Sleep(10); }
    return 7;
}
int SelfTest() {
    bool passed = true;
    {
        Child target(L"--synthetic-tone 440 3500"), excluded(L"--synthetic-tone 880 3500");
        const auto result = CaptureTest({target.info.dwProcessId, GetCurrentProcessId(), 3000});
        const auto included = Amplitude(result.samples, 440), other = Amplitude(result.samples, 880);
        const bool ok = included > 10 && other < included * 0.03 && result.frames >= SampleRate * 28 / 10
            && result.frames <= SampleRate * 32 / 10 && result.dropped == 0 && result.maximumBuffered <= MaxBufferedFrames;
        passed &= ok;
        std::cout << "{\"test\":\"process-isolation\",\"passed\":" << (ok ? "true" : "false")
            << ",\"included440Hz\":" << included << ",\"excluded880Hz\":" << other << ",\"frames\":" << result.frames << "}" << std::endl;
    }
    {
        Child target(L"--synthetic-idle 3000");
        const auto result = CaptureTest({target.info.dwProcessId, GetCurrentProcessId(), 2000});
        const bool quiet = std::all_of(result.samples.begin(), result.samples.end(), [](int16_t value) { return std::abs(static_cast<int>(value)) <= 2; });
        const bool ok = quiet && result.frames >= SampleRate * 18 / 10 && result.frames <= SampleRate * 22 / 10 && result.dropped == 0;
        passed &= ok;
        std::cout << "{\"test\":\"continuous-silence\",\"passed\":" << (ok ? "true" : "false") << ",\"frames\":" << result.frames << "}" << std::endl;
    }
    {
        Child target(L"--synthetic-idle 3000"), parent(L"--synthetic-idle 800");
        const auto result = CaptureTest({target.info.dwProcessId, parent.info.dwProcessId, 3000});
        const bool ok = result.reason == "parent-exited" && result.elapsed < 1600;
        passed &= ok;
        std::cout << "{\"test\":\"parent-exit\",\"passed\":" << (ok ? "true" : "false") << ",\"elapsedMs\":" << result.elapsed << "}" << std::endl;
    }
    {
        Child target(L"--synthetic-idle 800");
        const auto result = CaptureTest({target.info.dwProcessId, GetCurrentProcessId(), 3000});
        const bool ok = result.reason == "target-exited" && result.elapsed < 1600;
        passed &= ok;
        std::cout << "{\"test\":\"target-exit\",\"passed\":" << (ok ? "true" : "false") << ",\"elapsedMs\":" << result.elapsed << "}" << std::endl;
    }
    {
        TestPipe pipe, diagnosticPipe; bool ok = false;
        {
            PipeOutput output(pipe.write.value);
            DiagnosticOutput diagnostics(diagnosticPipe.write.value);
            for (int i = 0; i < 1000; ++i) output.Append(nullptr, ChunkFrames, true);
            const auto expected = CaptureStatistics(output, 0);
            bool reported = false;
            const auto deadline = GetTickCount64() + 500;
            while (!reported && GetTickCount64() < deadline) {
                diagnostics.Publish(expected); Sleep(10);
                DWORD available = 0;
                if (!PeekNamedPipe(diagnosticPipe.read.value, nullptr, 0, nullptr, &available, nullptr)) break;
                if (available >= expected.size() + 1) {
                    char line[512]{}; DWORD read = 0;
                    reported = ReadFile(diagnosticPipe.read.value, line, static_cast<DWORD>(expected.size() + 1), &read, nullptr)
                        && std::string(line, read) == expected + '\n';
                }
            }
            ok = output.droppedFrames > 0 && output.maximumBufferedFrames <= MaxBufferedFrames && reported;
            std::cout << "{\"test\":\"bounded-backpressure\",\"passed\":" << (ok ? "true" : "false")
                << ",\"maximumBufferedFrames\":" << output.maximumBufferedFrames << ",\"droppedFrames\":" << output.droppedFrames
                << ",\"activeDiagnosticReported\":" << (reported ? "true" : "false") << "}" << std::endl;
        }
        passed &= ok;
    }
    {
        const auto before = GetTickCount64(); Child child(L"--self-test-blocked-diagnostic-child");
        const auto waited = WaitForSingleObject(child.info.hProcess, 2700);
        DWORD code = 0; GetExitCodeProcess(child.info.hProcess, &code);
        const auto elapsed = GetTickCount64() - before;
        const bool ok = waited == WAIT_OBJECT_0 && code == 4 && elapsed < 2600;
        passed &= ok;
        std::cout << "{\"test\":\"blocked-diagnostic-consumer\",\"passed\":" << (ok ? "true" : "false") << ",\"elapsedMs\":" << elapsed << "}" << std::endl;
    }
    {
        const auto before = GetTickCount64(); Child child(L"--self-test-blocked-child");
        const auto waited = WaitForSingleObject(child.info.hProcess, 2500);
        DWORD code = 0; GetExitCodeProcess(child.info.hProcess, &code);
        const auto elapsed = GetTickCount64() - before;
        const bool ok = waited == WAIT_OBJECT_0 && code == 4 && elapsed < 2400;
        passed &= ok;
        std::cout << "{\"test\":\"blocked-consumer\",\"passed\":" << (ok ? "true" : "false") << ",\"elapsedMs\":" << elapsed << "}" << std::endl;
    }
    std::cout << "{\"event\":\"self-test\",\"passed\":" << (passed ? "true" : "false") << "}" << std::endl;
    return passed ? 0 : 3;
}
int wmain(int argc, wchar_t** argv) {
    try {
        ComApartment apartment;
        if (argc == 2 && std::wstring(argv[1]) == L"--self-test") return SelfTest();
        if (argc == 2 && std::wstring(argv[1]) == L"--self-test-blocked-child") return BlockedPipeTestChild();
        if (argc == 2 && std::wstring(argv[1]) == L"--self-test-blocked-diagnostic-child") return BlockedDiagnosticTestChild();
        if (argc == 4 && std::wstring(argv[1]) == L"--synthetic-tone") return Tone(Number(argv[2], 100, 2000), Number(argv[3], 1, 5000));
        if (argc == 3 && std::wstring(argv[1]) == L"--synthetic-idle") { Sleep(Number(argv[2], 1, 5000)); return 0; }
        if (argc != 5) {
            std::cerr << "Airwave.Capture --pid REKORDBOX_PID --parent-pid LAUNCHING_PARENT_PID\n"
                << "Stdout must be a redirected pipe: 48000 Hz stereo PCM16LE. No recordings are written.\n"
                << "--self-test exercises owned synthetic processes and anonymous pipes.\n";
            return argc == 1 ? 0 : 2;
        }
        CaptureOptions options;
        for (int i = 1; i < argc; i += 2) {
            const std::wstring option(argv[i]);
            if (option == L"--pid" && !options.pid) options.pid = Number(argv[i + 1], 1, UINT32_MAX);
            else if (option == L"--parent-pid" && !options.parentPid) options.parentPid = Number(argv[i + 1], 1, UINT32_MAX);
            else throw std::runtime_error("Unknown or duplicate option");
        }
        if (!options.pid || !options.parentPid) throw std::runtime_error("--pid and --parent-pid are required");
        if (_setmode(_fileno(stdout), _O_BINARY) == -1) throw std::runtime_error("Unable to configure binary stdout");
        SetConsoleCtrlHandler(OnCtrl, TRUE);
        PipeOutput output(GetStdHandle(STD_OUTPUT_HANDLE));
        Capture(options, output); return 0;
    } catch (const std::exception& error) {
        try {
            DiagnosticOutput diagnostics(GetStdHandle(STD_ERROR_HANDLE));
            diagnostics.Publish("{\"event\":\"error\",\"message\":" + Json(error.what()) + "}");
        } catch (...) { }
        return 1;
    }
}
