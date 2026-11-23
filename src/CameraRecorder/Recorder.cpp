#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#include <algorithm>
#include <chrono>
#include <cctype>
#include <cstdio>
#include <cstring>
#include <string>
#include <stdexcept>
#include <thread>
#include "SpoutDX.h"
#include "PreviewShaders.h"

using Microsoft::WRL::ComPtr;
using Clock = std::chrono::steady_clock;

static void check(HRESULT hr, const char* stage) {
    if (FAILED(hr)) {
        char message[128];
        snprintf(message, sizeof(message), "%s (0x%08lx)", stage, static_cast<unsigned long>(hr));
        throw std::runtime_error(message);
    }
}

#include "GameAudio.h"

static bool stopping() {
    DWORD bytes = 0;
    return !PeekNamedPipe(GetStdHandle(STD_INPUT_HANDLE), nullptr, 0, nullptr, &bytes, nullptr) || bytes != 0;
}

static ComPtr<ID3DBlob> shader(const char* source, const char* target) {
    ComPtr<ID3DBlob> code;
    check(D3DCompile(source, strlen(source), nullptr, nullptr, nullptr, "main", target, 0, 0, &code, nullptr), "Shader");
    return code;
}

// Render the sender's texture directly into a fixed-size recording surface. The preview's
// full-screen triangle and letterboxed viewport also handle portrait and resized cameras.
class FrameRenderer {
    ID3D11Device* device;
    ID3D11DeviceContext* context;
    const UINT width, height;
    ComPtr<ID3D11VertexShader> vs;
    ComPtr<ID3D11PixelShader> ps;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11Texture2D> target, staging;
    ComPtr<ID3D11RenderTargetView> rtv;
    ComPtr<ID3D11ShaderResourceView> sourceView;
    ComPtr<ID3D11Texture2D> sourceTexture;
    D3D11_VIEWPORT viewport{};
public:
    FrameRenderer(spoutDX& receiver, UINT w, UINT h)
        : device(receiver.GetDX11Device()), context(receiver.GetDX11Context()), width(w), height(h) {
        auto v = shader(kVertexShader, "vs_4_0");
        auto p = shader(kPixelShader, "ps_4_0");
        check(device->CreateVertexShader(v->GetBufferPointer(), v->GetBufferSize(), nullptr, &vs), "Vertex shader");
        check(device->CreatePixelShader(p->GetBufferPointer(), p->GetBufferSize(), nullptr, &ps), "Pixel shader");
        D3D11_SAMPLER_DESC sd{};
        sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.MaxLOD = D3D11_FLOAT32_MAX;
        sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
        check(device->CreateSamplerState(&sd, &sampler), "Sampler");
        D3D11_TEXTURE2D_DESC td{};
        td.Width = w; td.Height = h; td.ArraySize = td.MipLevels = td.SampleDesc.Count = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.BindFlags = D3D11_BIND_RENDER_TARGET;
        check(device->CreateTexture2D(&td, nullptr, &target), "Recording texture");
        check(device->CreateRenderTargetView(target.Get(), nullptr, &rtv), "Recording view");
        td.Usage = D3D11_USAGE_STAGING; td.BindFlags = 0; td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        check(device->CreateTexture2D(&td, nullptr, &staging), "Frame readback");
        D3D11_RASTERIZER_DESC rd{};
        rd.FillMode = D3D11_FILL_SOLID; rd.CullMode = D3D11_CULL_NONE; rd.DepthClipEnable = TRUE;
        ComPtr<ID3D11RasterizerState> raster;
        check(device->CreateRasterizerState(&rd, &raster), "Rasterizer");
        context->RSSetState(raster.Get());
    }
    void draw(ID3D11Texture2D* source) {
        if (source != sourceTexture.Get()) {
            sourceTexture = source;
            sourceView.Reset();
            check(device->CreateShaderResourceView(source, nullptr, &sourceView), "Spout texture format");
            D3D11_TEXTURE2D_DESC desc;
            source->GetDesc(&desc);
            float scale = std::min(float(width)/desc.Width, float(height)/desc.Height);
            viewport.Width = desc.Width * scale; viewport.Height = desc.Height * scale;
            viewport.TopLeftX = (width - viewport.Width)/2; viewport.TopLeftY = (height - viewport.Height)/2;
            viewport.MaxDepth = 1;
        }
        const float black[4] = {0, 0, 0, 1};
        context->ClearRenderTargetView(rtv.Get(), black);
        context->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
        context->RSSetViewports(1, &viewport);
        context->VSSetShader(vs.Get(), nullptr, 0); context->PSSetShader(ps.Get(), nullptr, 0);
        context->PSSetSamplers(0, 1, sampler.GetAddressOf());
        context->PSSetShaderResources(0, 1, sourceView.GetAddressOf());
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        context->Draw(3, 0);
        ID3D11ShaderResourceView* empty = nullptr;
        context->PSSetShaderResources(0, 1, &empty);
        context->CopyResource(staging.Get(), target.Get());
    }
    void readPixels(BYTE* pixels) {
        D3D11_MAPPED_SUBRESOURCE mapped;
        check(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped), "GPU frame");
        for (UINT y = 0; y < height; ++y)
            memcpy(pixels + y * width * 4, static_cast<BYTE*>(mapped.pData) + y * mapped.RowPitch, width * 4);
        context->Unmap(staging.Get(), 0);
    }
    ComPtr<IMFSample> frame(ID3D11Texture2D* source) {
        draw(source);
        const DWORD size = width * height * 4;
        ComPtr<IMFMediaBuffer> buffer;
        check(MFCreateMemoryBuffer(size, &buffer), "Frame memory");
        BYTE* pixels = nullptr;
        check(buffer->Lock(&pixels, nullptr, nullptr), "Lock pixels");
        try { readPixels(pixels); } catch (...) { buffer->Unlock(); throw; }
        check(buffer->Unlock(), "Unlock pixels");
        check(buffer->SetCurrentLength(size), "Frame size");
        ComPtr<IMFSample> sample;
        check(MFCreateSample(&sample), "Sample");
        check(sample->AddBuffer(buffer.Get()), "Attach pixels");
        return sample;
    }
};

static ComPtr<IMFMediaType> mediaType(const GUID& format, UINT width, UINT height, UINT fps) {
    ComPtr<IMFMediaType> type;
    check(MFCreateMediaType(&type), "Media type");
    check(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video), "Video type");
    check(type->SetGUID(MF_MT_SUBTYPE, format), "Video format");
    check(type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive), "Video scan");
    check(MFSetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, width, height), "Video size");
    check(MFSetAttributeRatio(type.Get(), MF_MT_FRAME_RATE, fps, 1), "Video rate");
    check(MFSetAttributeRatio(type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1), "Pixel aspect");
    return type;
}

static ComPtr<IMFSinkWriter> encoder(const std::wstring& path, int w, int h, int fps, DWORD& stream, camera_audio::GameAudio& audio) {
    ComPtr<IMFAttributes> attrs;
    check(MFCreateAttributes(&attrs, 1), "Encoder attributes");
    check(attrs->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE), "Hardware encoder");
    ComPtr<IMFSinkWriter> writer;
    check(MFCreateSinkWriterFromURL(path.c_str(), nullptr, attrs.Get(), &writer), "Create MP4");
    auto output = mediaType(MFVideoFormat_H264, w, h, fps);
    check(output->SetUINT32(MF_MT_AVG_BITRATE, static_cast<UINT32>(w * double(h) * fps * .16)), "Video bitrate");
    check(writer->AddStream(output.Get(), &stream), "H.264 encoder");
    auto input = mediaType(MFVideoFormat_RGB32, w, h, fps);
    check(input->SetUINT32(MF_MT_DEFAULT_STRIDE, w * 4), "Row stride");
    check(writer->SetInputMediaType(stream, input.Get(), nullptr), "Encoder input");
    audio.configure(writer.Get());
    check(writer->BeginWriting(), "Start MP4");
    return writer;
}

static std::string findCamera(spoutDX& receiver, const char* selected) {
    for (auto& name : receiver.GetSenderList()) {
        if (selected) { if (name == selected) return name; continue; }
        auto lower = name;
        for (auto& c : lower) c = static_cast<char>(tolower(static_cast<unsigned char>(c)));
        if (lower.find("vrchat") != std::string::npos || lower.rfind("vrc", 0) == 0) return name;
    }
    return {};
}

static int record(const std::wstring& destination, int h, int fps, const char* selected, unsigned audioMask) {
    spoutDX receiver;
    if (!receiver.OpenDirectX11(nullptr)) throw std::runtime_error("DirectX 11 unavailable");
    std::string camera;
    while (camera.empty()) {
        if (stopping()) return 2;
        camera = findCamera(receiver, selected);
        if (camera.empty()) Sleep(150);
    }
    receiver.SetReceiverName(camera.c_str());
    int w = h * 16 / 9;
    FrameRenderer renderer(receiver, w, h);
    camera_audio::GameAudio audio(audioMask);
    DWORD stream = 0;
    ComPtr<IMFSinkWriter> writer;
    const auto temp = destination + L"." + std::to_wstring(GetCurrentProcessId()) + L".partial.mp4";
    Clock::time_point start;
    long long next = 0;
    bool lost = false;
    try {
        while (!stopping()) {
            bool received = receiver.ReceiveTexture();
            receiver.IsUpdated();
            auto texture = received ? receiver.GetSenderTexture() : nullptr;
            if (!texture || camera != receiver.GetSenderName()) {
                if (writer) { lost = true; break; }
                Sleep(50); continue;
            }
            if (!writer) {
                writer = encoder(temp, w, h, fps, stream, audio);
                start = audio.start();
                puts("RECORDING"); fflush(stdout);
            }
            audio.drain(writer.Get(), next * 10000000 / fps);
            auto sample = renderer.frame(texture);
            auto elapsed = std::chrono::duration_cast<std::chrono::microseconds>(Clock::now() - start).count();
            auto index = next == 0 ? 0LL : std::max(next, static_cast<long long>(elapsed * fps / 1000000));
            auto pts = index * 10000000 / fps;
            check(sample->SetSampleTime(pts), "Timestamp");
            check(sample->SetSampleDuration((index + 1) * 10000000 / fps - pts), "Duration");
            check(writer->WriteSample(stream, sample.Get()), "Write MP4");
            next = index + 1;
            std::this_thread::sleep_until(start + std::chrono::microseconds(next * 1000000 / fps));
        }
    } catch (...) {
        if (writer && next > 0 && SUCCEEDED(writer->Finalize())) {
            writer.Reset();
            MoveFileW(temp.c_str(), destination.c_str());
        } else if (!next) { writer.Reset(); DeleteFileW(temp.c_str()); }
        throw;
    }
    if (!writer) return 2;
    try { audio.finish(writer.Get(), next * 10000000 / fps); }
    catch (...) {
        if (SUCCEEDED(writer->Finalize())) { writer.Reset(); MoveFileW(temp.c_str(), destination.c_str()); }
        throw;
    }
    check(writer->Finalize(), "Finalize MP4");
    writer.Reset();
    if (!MoveFileW(temp.c_str(), destination.c_str())) throw std::runtime_error("Rename finished MP4");
    return lost ? 3 : 0;
}

// Only the newest frame is useful to the viewer. Downscale in D3D before
// readback, and keep the preview transport independent of the MP4 encoder.
static void preview(HANDLE pipe) {
    spoutDX receiver;
    if (!receiver.OpenDirectX11(nullptr)) throw std::runtime_error("DirectX 11 unavailable");
    constexpr UINT width = 320, height = 180;
    FrameRenderer renderer(receiver, width, height);
    std::vector<BYTE> message(1 + width * height * 4);
    std::string camera;
    while (!stopping()) {
        const auto next = Clock::now() + std::chrono::microseconds(33333);
        if (camera.empty()) {
            camera = findCamera(receiver, nullptr);
            if (!camera.empty()) receiver.SetReceiverName(camera.c_str());
        }
        auto texture = !camera.empty() && receiver.ReceiveTexture() ? receiver.GetSenderTexture() : nullptr;
        receiver.IsUpdated();
        DWORD size = 1;
        if (texture && camera == receiver.GetSenderName()) {
            renderer.draw(texture);
            renderer.readPixels(message.data() + 1);
            message[0] = 1;
            size = static_cast<DWORD>(message.size());
        } else {
            if (!camera.empty()) receiver.ReleaseReceiver();
            camera.clear();
            message[0] = 0;
        }
        DWORD written = 0;
        if (!WriteFile(pipe, message.data(), size, &written, nullptr) || written != size) break;
        if (size == 1) Sleep(180);
        else std::this_thread::sleep_until(next);
    }
}

int wmain(int argc, wchar_t** argv) {
    if (argc == 3 && wcscmp(argv[1], L"--preview") == 0) {
        std::wstring name = L"\\\\.\\pipe\\";
        HANDLE pipe = CreateFileW((name + argv[2]).c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) return 1;
        int result = 0;
        timeBeginPeriod(1);
        try { preview(pipe); } catch (const std::exception& e) { fprintf(stderr, "%s\n", e.what()); result = 1; }
        timeEndPeriod(1);
        CloseHandle(pipe);
        return result;
    }
    if (argc < 4 || argc > 6) return 1;
    int h = _wtoi(argv[2]), fps = _wtoi(argv[3]);
    if ((h != 720 && h != 1080 && h != 1440) || (fps != 30 && fps != 60 && fps != 120)) return 1;
    std::string source;
    unsigned audioMask = camera_audio::Game;
    for (int i = 4; i < argc; ++i) {
        std::wstring arg(argv[i]);
        if (arg.rfind(L"--audio=", 0) == 0) {
            if (arg.size() != 9 || arg[8] < L'0' || arg[8] > L'7') return 1;
            audioMask = arg[8] - L'0';
        } else source.assign(arg.begin(), arg.end());
    }
    int result = 1;
    try {
        check(CoInitializeEx(nullptr, COINIT_MULTITHREADED), "COM");
        check(MFStartup(MF_VERSION), "Windows Media Foundation");
        timeBeginPeriod(1);
        result = record(argv[1], h, fps, source.empty() ? nullptr : source.c_str(), audioMask);
    } catch (const std::exception& e) { fprintf(stderr, "%s\n", e.what()); }
    timeEndPeriod(1);
    MFShutdown(); CoUninitialize();
    return result;
}
