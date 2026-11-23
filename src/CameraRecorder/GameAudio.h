#pragma once
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <tlhelp32.h>
#include <atomic>
#include <array>
#include <deque>
#include <future>
#include <mutex>
#include <cstdint>
#include <memory>
#include <vector>

namespace camera_audio {
using Handle = std::unique_ptr<void, decltype(&CloseHandle)>;

inline DWORD gameProcess() {
    Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0), CloseHandle);
    if (snapshot.get() == INVALID_HANDLE_VALUE) throw std::runtime_error("无法读取 VRChat 进程列表");
    PROCESSENTRY32W item{};
    item.dwSize = sizeof(item);
    DWORD session = 0, pid = 0;
    if (!ProcessIdToSessionId(GetCurrentProcessId(), &session)) throw std::runtime_error("无法识别当前用户会话");
    if (Process32FirstW(snapshot.get(), &item)) do {
        DWORD other = 0;
        if (_wcsicmp(item.szExeFile, L"VRChat.exe") != 0 ||
            !ProcessIdToSessionId(item.th32ProcessID, &other) || other != session) continue;
        if (pid) throw std::runtime_error("多个 VRChat 实例正在运行，请保留要录制的实例");
        pid = item.th32ProcessID;
    } while (Process32NextW(snapshot.get(), &item));
    if (!pid) throw std::runtime_error("未找到 VRChat.exe，请先启动 VRChat 再录制");
    return pid;
}

// ActivateAudioInterfaceAsync completes on an MTA thread. The operation owns
// its own reference to this callback, including in the timeout case.
class Activation final : public IActivateAudioInterfaceCompletionHandler, public IAgileObject {
    std::atomic<ULONG> references{1};
public:
    Handle done{CreateEventW(nullptr, FALSE, FALSE, nullptr), CloseHandle};
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if (!out) return E_POINTER;
        if (iid == IID_IUnknown || iid == __uuidof(IActivateAudioInterfaceCompletionHandler))
            *out = static_cast<IActivateAudioInterfaceCompletionHandler*>(this);
        else if (iid == __uuidof(IAgileObject)) *out = static_cast<IAgileObject*>(this);
        else { *out = nullptr; return E_NOINTERFACE; }
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++references; }
    ULONG STDMETHODCALLTYPE Release() override {
        auto remaining = --references;
        if (!remaining) delete this;
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation*) override {
        SetEvent(done.get()); return S_OK;
    }
};

enum AudioMask { Desktop = 1, Microphone = 2, Game = 4 };
constexpr DWORD rate = 48000, align = 4;
using Frame = LONGLONG;

struct AudioPacket {
    Frame first = 0;
    std::vector<int16_t> stereo;
    Frame end() const { return first + static_cast<Frame>(stereo.size() / 2); }
};

inline Frame qpc100ns() {
    LARGE_INTEGER ticks, frequency;
    QueryPerformanceCounter(&ticks);
    QueryPerformanceFrequency(&frequency);
    return ticks.QuadPart / frequency.QuadPart * 10000000 +
           ticks.QuadPart % frequency.QuadPart * 10000000 / frequency.QuadPart;
}

class Source {
    ComPtr<IAudioClient> client;
    ComPtr<IAudioCaptureClient> capture;
    WORD channels = 2;
    Frame next = -1;
    Frame lastArrival = -1;

    void playback(DWORD pid, bool exclude) {
        // Binary layout of AUDIOCLIENT_ACTIVATION_PARAMS (the MinGW SDK lacks that header).
        struct { DWORD activationType, targetProcessId, loopbackMode; } params{1, pid, exclude ? 1UL : 0UL};
        PROPVARIANT pv{};
        pv.vt = VT_BLOB; pv.blob.cbSize = sizeof(params); pv.blob.pBlobData = reinterpret_cast<BYTE*>(&params);
        ComPtr<Activation> cb;
        cb.Attach(new Activation());
        if (!cb->done) throw std::runtime_error("无法创建音频初始化事件");
        ComPtr<IActivateAudioInterfaceAsyncOperation> op;
        const char* reason = "无法启用播放声音采集，请检查系统是否支持应用音频捕获";
        check(ActivateAudioInterfaceAsync(L"VAD\\Process_Loopback", __uuidof(IAudioClient), &pv, cb.Get(), &op), reason);
        if (WaitForSingleObject(cb->done.get(), 8000) != WAIT_OBJECT_0) throw std::runtime_error("播放声音初始化超时");
        HRESULT result;
        ComPtr<IUnknown> object;
        check(op->GetActivateResult(&result, &object), reason);
        check(result, reason);
        check(object.As(&client), reason);
    }
    void microphone() {
        ComPtr<IMMDeviceEnumerator> devices;
        ComPtr<IMMDevice> device;
        check(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&devices)), "音频设备枚举");
        check(devices->GetDefaultAudioEndpoint(eCapture, eConsole, &device), "找不到 Windows 默认麦克风");
        check(device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)client.GetAddressOf()), "无法打开麦克风");
        WAVEFORMATEX* native = nullptr;
        check(client->GetMixFormat(&native), "读取麦克风格式");
        channels = native->nChannels == 1 ? 1 : 2;
        CoTaskMemFree(native);
    }
public:
    Handle available{CreateEventW(nullptr, FALSE, FALSE, nullptr), CloseHandle};

    void open(bool mic, DWORD pid, bool exclude) {
        if (!available) throw std::runtime_error("无法创建录音事件");
        if (mic) microphone();
        else playback(pid, exclude);
        WORD stride = channels * sizeof(int16_t);
        WAVEFORMATEX format{WAVE_FORMAT_PCM, channels, rate, rate * stride, stride, 16, 0};
        DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                      AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        if (!mic) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
        check(client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 2000000, 0, &format, nullptr),
              mic ? "麦克风初始化失败，请检查输入设备及麦克风权限" : "播放声音初始化失败");
        check(client->SetEventHandle(available.get()), "音频采样事件");
        check(client->GetService(IID_PPV_ARGS(&capture)), "获取录音服务");
    }
    void start() { check(client->Start(), "启动声音采集"); }
    void stop() { check(client->Stop(), "停止声音采集"); }

    bool read(Frame epoch, AudioPacket& out) {
        BYTE* bytes = nullptr;
        DWORD flags = 0;
        UINT32 count = 0;
        UINT64 stamp = 0;
        HRESULT hr = capture->GetBuffer(&bytes, &count, &flags, nullptr, &stamp);
        if (hr == AUDCLNT_S_BUFFER_EMPTY) return false;
        check(hr, "读取录音数据");
        auto arrival = (qpc100ns() - epoch) * rate / 10000000;
        bool timed = stamp && !(flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR);
        Frame measured = timed ? (static_cast<Frame>(stamp) - epoch) * rate / 10000000 : arrival - count;
        const bool gap = next < 0 || (flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) ||
                         (arrival - lastArrival > rate / 10 && measured - next > rate / 20);
        if (gap) next = std::max(next, measured);
        // Keep adjacent PCM packets contiguous. Correct slow device clock drift
        // by interpolating within a packet instead of cutting it or inserting zeros.
        int adjust = 0;
        if (!gap && timed && count > 2) {
            if (measured - next > rate / 100) adjust = 1;
            if (next - measured > rate / 100) adjust = -1;
        }
        UINT32 frames = count + adjust;
        out.first = next;
        out.stereo.resize(frames * 2);
        if (!(flags & AUDCLNT_BUFFERFLAGS_SILENT) && count) {
            auto pcm = reinterpret_cast<const int16_t*>(bytes);
            if (adjust == 0 && channels == 2) memcpy(out.stereo.data(), pcm, count * align);
            else {
                for (UINT32 i = 0; i < frames; ++i) {
                    double position = adjust ? double(i) * (count - 1) / (frames - 1) : i;
                    UINT32 left = static_cast<UINT32>(position), right = std::min(left + 1, count - 1);
                    for (WORD c = 0; c < 2; ++c) {
                        WORD ch = channels == 1 ? 0 : c;
                        int a = pcm[left * channels + ch], b = pcm[right * channels + ch];
                        out.stereo[i * 2 + c] = static_cast<int16_t>(a + (b - a) * (position - left));
                    }
                }
            }
        }
        check(capture->ReleaseBuffer(count), "释放录音缓冲区");
        lastArrival = arrival;
        next += frames;
        return true;
    }
};

class GameAudio {
    const unsigned mask;
    const int inputs;
    std::array<std::deque<AudioPacket>, 2> pending;
    std::mutex mutex;
    Handle stopped{nullptr, CloseHandle};
    std::thread worker;
    std::exception_ptr failure;
    Frame ready = 0, written = 0;
    DWORD stream = 0;

    static Frame timeOf(Frame frames) { return frames * 10000000 / rate; }
    void append(IMFSinkWriter* writer, const int16_t* pcm, DWORD frames) {
        ComPtr<IMFMediaBuffer> buffer;
        check(MFCreateMemoryBuffer(frames * align, &buffer), "Audio buffer");
        BYTE* data;
        check(buffer->Lock(&data, nullptr, nullptr), "Audio lock");
        memcpy(data, pcm, frames * align);
        check(buffer->Unlock(), "Audio unlock");
        check(buffer->SetCurrentLength(frames * align), "PCM length");
        ComPtr<IMFSample> sample;
        check(MFCreateSample(&sample), "Audio sample");
        check(sample->AddBuffer(buffer.Get()), "PCM sample");
        check(sample->SetSampleTime(timeOf(written)), "Audio timestamp");
        check(sample->SetSampleDuration(timeOf(written + frames) - timeOf(written)), "Audio duration");
        check(writer->WriteSample(stream, sample.Get()), "Write AAC");
        written += frames;
    }

    void collect(std::promise<Clock::time_point>& started) {
        std::array<Source, 2> sources;
        int n = 0;
        if (mask & (Desktop | Game)) {
            // Desktop + game is the whole render mix, captured once across endpoints.
            bool all = (mask & Desktop) && (mask & Game);
            sources[n++].open(false, all ? GetCurrentProcessId() : gameProcess(), mask & Desktop);
        }
        if (mask & Microphone) sources[n++].open(true, 0, false);
        HANDLE events[3]{stopped.get()};
        auto start = Clock::now();
        Frame epoch = qpc100ns();
        for (int i = 0; i < n; ++i) {
            events[i + 1] = sources[i].available.get();
            sources[i].start();
        }
        started.set_value(start);
        for (;;) {
            DWORD event = WaitForMultipleObjects(n + 1, events, FALSE, 25);
            if (event == WAIT_FAILED) throw std::runtime_error("等待声音采集失败");
            bool done = event == WAIT_OBJECT_0;
            if (done) for (int i = 0; i < n; ++i) sources[i].stop();
            Frame poll = (qpc100ns() - epoch) * rate / 10000000;
            for (int i = 0; i < n; ++i) {
                for (;;) {
                    AudioPacket packet;
                    if (!sources[i].read(epoch, packet)) break;
                    std::lock_guard lock(mutex);
                    auto& q = pending[i];
                    if (!q.empty() && packet.first - q.front().first > 30 * rate)
                        throw std::runtime_error("视频编码长时间未响应，录音缓冲区已满");
                    q.push_back(std::move(packet));
                }
            }
            std::lock_guard lock(mutex);
            ready = poll - rate / 8;
            if (done) return;
        }
    }
    void join() {
        if (worker.joinable()) { SetEvent(stopped.get()); worker.join(); }
    }
    void mixUntil(IMFSinkWriter* writer, Frame limit) {
        while (written < limit) {
            DWORD count = static_cast<DWORD>(std::min<Frame>(1024, limit - written));
            std::array<int, 2048> mix{};
            {
                std::lock_guard lock(mutex);
                for (int source = 0; source < inputs; ++source) {
                    auto& q = pending[source];
                    while (!q.empty() && q.front().first < written + count) {
                        auto& p = q.front();
                        Frame end = std::min(p.end(), written + count);
                        for (Frame f = std::max(written, p.first); f < end; ++f)
                            for (int c = 0; c < 2; ++c)
                                mix[(f - written) * 2 + c] += p.stereo[(f - p.first) * 2 + c];
                        if (p.end() > written + count) break;
                        q.pop_front();
                    }
                }
            }
            std::array<int16_t, 2048> pcm;
            for (DWORD i = 0; i < count * 2; ++i) pcm[i] = static_cast<int16_t>(mix[i] / inputs);
            append(writer, pcm.data(), count);
        }
    }
public:
    explicit GameAudio(unsigned selection) : mask(selection),
        inputs(int(bool(mask & (Desktop | Game))) + int(bool(mask & Microphone))) {}
    ~GameAudio() { join(); }
    void configure(IMFSinkWriter* writer) {
        if (!mask) return;
        ComPtr<IMFMediaType> pcm;
        check(MFCreateMediaType(&pcm), "PCM");
        check(pcm->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio), "PCM major");
        check(pcm->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM), "PCM format");
        check(pcm->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2), "PCM channels");
        check(pcm->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, rate), "PCM sample rate");
        check(pcm->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16), "PCM bits");
        check(pcm->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, align), "PCM alignment");
        check(pcm->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, rate * align), "PCM bandwidth");
        ComPtr<IMFMediaType> aac;
        check(MFCreateMediaType(&aac), "AAC");
        check(aac->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio), "AAC major");
        check(aac->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC), "AAC format");
        check(aac->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, 2), "AAC channels");
        check(aac->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, rate), "AAC sample rate");
        check(aac->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16), "AAC bits");
        check(aac->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 24000), "AAC bitrate");
        check(writer->AddStream(aac.Get(), &stream), "AAC encoder");
        check(writer->SetInputMediaType(stream, pcm.Get(), nullptr), "AAC input");
    }

    Clock::time_point start() {
        if (!mask) return Clock::now();
        stopped.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
        if (!stopped) throw std::runtime_error("无法创建录音停止事件");
        std::promise<Clock::time_point> signal;
        auto started = signal.get_future();
        worker = std::thread([this, signal = std::move(signal)]() mutable {
            HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            try {
                check(hr, "录音线程初始化");
                collect(signal);
            } catch (...) {
                std::lock_guard lock(mutex);
                failure = std::current_exception();
                try { signal.set_exception(failure); } catch (const std::future_error&) { /* already started */ }
            }
            if (SUCCEEDED(hr)) CoUninitialize();
        });
        return started.get();
    }
    void drain(IMFSinkWriter* writer, Frame videoEnd100ns) {
        if (!mask) return;
        Frame limit;
        {
            std::lock_guard lock(mutex);
            if (failure) std::rethrow_exception(failure);
            limit = std::min(ready, videoEnd100ns * rate / 10000000);
        }
        mixUntil(writer, limit);
    }
    void finish(IMFSinkWriter* writer, Frame videoEnd100ns) {
        if (!mask) return;
        join();
        if (failure) std::rethrow_exception(failure);
        mixUntil(writer, videoEnd100ns * rate / 10000000);
    }
};
}
