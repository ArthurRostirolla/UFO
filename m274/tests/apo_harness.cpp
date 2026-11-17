// Loads M274.dll the way audiodg does (COM class factory, lock, process) without touching the
// system: proves the object works end to end, including reading settings from a mapped file.
// Usage: apo_harness.exe <path to M274.dll>
#include <windows.h>

#include <audioenginebaseapo.h>
#include <audiomediatype.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <vector>

#include "ipc.h"

static void expect(bool condition, const char* what) {
    if (!condition) {
        std::printf("FAILED: %s\n", what);
        std::exit(1);
    }
}

static double sinePeakDb(IAudioProcessingObjectRT* rt, double frequency, int blocks) {
    const UINT32 frames = 480;
    std::vector<float> buffer(frames * 2);
    APO_CONNECTION_PROPERTY in{}, out{};
    APO_CONNECTION_PROPERTY* ins[] = {&in};
    APO_CONNECTION_PROPERTY* outs[] = {&out};
    float peak = 0;
    for (int block = 0; block < blocks; ++block) {
        for (UINT32 i = 0; i < frames; ++i) {
            double t = (block * frames + i) / 48000.0;
            buffer[2 * i] = buffer[2 * i + 1] = 0.25f * static_cast<float>(std::sin(2 * 3.141592653589793 * frequency * t));
        }
        in.pBuffer = out.pBuffer = reinterpret_cast<UINT_PTR>(buffer.data());
        in.u32ValidFrameCount = frames;
        in.u32BufferFlags = BUFFER_VALID;
        rt->APOProcess(1, ins, 1, outs);
        expect(out.u32ValidFrameCount == frames && out.u32BufferFlags == BUFFER_VALID, "output buffer marked valid");
        if (block >= blocks / 2) {
            for (float sample : buffer) peak = std::max(peak, std::fabs(sample));
        }
    }
    return 20 * std::log10(peak / 0.25);
}

int wmain(int argc, wchar_t** argv) {
    expect(argc == 2, "pass the DLL path");
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);

    // Settings file: one +6 dB band at 1 kHz, limiter off, so levels are easy to predict.
    CreateDirectoryW(L"C:\\ProgramData\\UFO", nullptr);
    m274::ipc::ParamsFile params{};
    params.magic = m274::ipc::kMagic;
    params.sequence = 2;
    params.params.bandCount = 1;
    params.params.limiterEnabled = 0;
    params.params.bands[0] = {m274::BandType::Peak, 1000, 6, 1.41f};
    // Opened shared and written in place: the app may have this file mapped while the test runs.
    HANDLE file = CreateFileW(m274::ipc::kRenderParamsPath, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_ALWAYS, 0, nullptr);
    expect(file != INVALID_HANDLE_VALUE, "can write the settings file");
    DWORD written = 0;
    WriteFile(file, &params, sizeof(params), &written, nullptr);
    CloseHandle(file);

    HMODULE dll = LoadLibraryW(argv[1]);
    expect(dll != nullptr, "DLL loads");
    using GetClassObject = HRESULT(STDAPICALLTYPE*)(REFCLSID, REFIID, void**);
    auto getClassObject = reinterpret_cast<GetClassObject>(GetProcAddress(dll, "DllGetClassObject"));
    expect(getClassObject != nullptr, "DLL exports DllGetClassObject");

    const CLSID renderClsid = {0x7c1f2d3a, 0x5b6e, 0x4a91, {0x9c, 0x0d, 0x55, 0x46, 0x4f, 0x45, 0x4e, 0x01}};
    IClassFactory* factory = nullptr;
    expect(SUCCEEDED(getClassObject(renderClsid, __uuidof(IClassFactory), reinterpret_cast<void**>(&factory))), "class factory");
    // audiodg.exe creates the object aggregated inside its own wrapper; refusing that mutes the endpoint.
    struct Wrapper final : IUnknown {
        LONG count = 1;
        IUnknown* inner = nullptr;
        STDMETHODIMP QueryInterface(REFIID iid, void** object) override {
            if (iid == __uuidof(IUnknown)) {
                *object = this;
                AddRef();
                return S_OK;
            }
            return inner->QueryInterface(iid, object);
        }
        STDMETHODIMP_(ULONG) AddRef() override { return ++count; }
        STDMETHODIMP_(ULONG) Release() override { return --count; }
    } wrapper;
    expect(factory->CreateInstance(&wrapper, __uuidof(IAudioProcessingObject), reinterpret_cast<void**>(&wrapper.inner)) == CLASS_E_NOAGGREGATION,
           "an aggregator must ask for IUnknown");
    expect(SUCCEEDED(factory->CreateInstance(&wrapper, __uuidof(IUnknown), reinterpret_cast<void**>(&wrapper.inner))), "creates aggregated");
    IAudioProcessingObject* aggregated = nullptr;
    expect(SUCCEEDED(wrapper.QueryInterface(__uuidof(IAudioProcessingObject), reinterpret_cast<void**>(&aggregated))), "aggregated object exposes the APO");
    expect(wrapper.count == 2, "the interface is counted by the wrapper");
    IUnknown* identity = nullptr;
    expect(SUCCEEDED(aggregated->QueryInterface(__uuidof(IUnknown), reinterpret_cast<void**>(&identity))) && identity == &wrapper,
           "identity of the aggregated object is the wrapper");
    identity->Release();
    aggregated->Release();
    wrapper.inner->Release();

    IAudioProcessingObject* apo = nullptr;
    expect(SUCCEEDED(factory->CreateInstance(nullptr, __uuidof(IAudioProcessingObject), reinterpret_cast<void**>(&apo))), "create APO");
    IAudioProcessingObjectRT* rt = nullptr;
    IAudioProcessingObjectConfiguration* config = nullptr;
    IAudioSystemEffects* effects = nullptr;
    expect(SUCCEEDED(apo->QueryInterface(&rt)) && SUCCEEDED(apo->QueryInterface(&config)) && SUCCEEDED(apo->QueryInterface(&effects)),
           "exposes the interfaces audiodg asks for");
    expect(SUCCEEDED(apo->Initialize(0, nullptr)), "initialize");

    UNCOMPRESSEDAUDIOFORMAT format{};
    format.guidFormatType = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
    format.dwSamplesPerFrame = 2;
    format.dwBytesPerSampleContainer = 4;
    format.dwValidBitsPerSample = 32;
    format.fFramesPerSecond = 48000;
    format.dwChannelMask = 0x3;  // front left | front right
    IAudioMediaType* mediaType = nullptr;
    expect(SUCCEEDED(CreateAudioMediaTypeFromUncompressedAudioFormat(&format, &mediaType)), "media type");
    IAudioMediaType* supported = nullptr;
    expect(apo->IsInputFormatSupported(nullptr, mediaType, &supported) == S_OK, "accepts float 48 kHz stereo");
    if (supported) supported->Release();

    APO_CONNECTION_DESCRIPTOR in{}, out{};
    std::vector<float> scratch(480 * 2);
    in.Type = out.Type = APO_CONNECTION_BUFFER_TYPE_EXTERNAL;
    in.pBuffer = out.pBuffer = reinterpret_cast<UINT_PTR>(scratch.data());
    in.u32MaxFrameCount = out.u32MaxFrameCount = 480;
    in.pFormat = out.pFormat = mediaType;
    in.u32Signature = out.u32Signature = APO_CONNECTION_DESCRIPTOR_SIGNATURE;
    APO_CONNECTION_DESCRIPTOR* ins[] = {&in};
    APO_CONNECTION_DESCRIPTOR* outs[] = {&out};
    expect(SUCCEEDED(config->LockForProcess(1, ins, 1, outs)), "lock for process");

    // +6 dB band with 6 dB of automatic headroom: 1 kHz comes out level, 100 Hz comes out -6 dB.
    double at1k = sinePeakDb(rt, 1000, 40), at100 = sinePeakDb(rt, 100, 40);
    std::printf("1 kHz: %+.2f dB   100 Hz: %+.2f dB\n", at1k, at100);
    expect(std::fabs(at1k - 0.0) < 0.1, "boosted band lands at full level after headroom");
    expect(std::fabs(at100 + 6.0) < 0.15, "rest of the spectrum sits at the headroom level");

    // Meters prove the M274 saw the settings and is counting blocks.
    HANDLE meters = CreateFileW(m274::ipc::kRenderMetersPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
    expect(meters != INVALID_HANDLE_VALUE, "meters file exists");
    m274::ipc::MetersFile seen{};
    DWORD read = 0;
    ReadFile(meters, &seen, sizeof(seen), &read, nullptr);
    CloseHandle(meters);
    expect(seen.magic == m274::ipc::kMagic && seen.blocks == 80 && seen.paramsSequence == 2, "meters report blocks and applied settings");
    expect(std::fabs(seen.headroomDb + 6.0f) < 0.05f, "meters report the headroom");

    expect(SUCCEEDED(config->UnlockForProcess()), "unlock");
    mediaType->Release();
    effects->Release();
    config->Release();
    rt->Release();
    apo->Release();
    factory->Release();
    std::printf("APO harness passed.\n");
    return 0;
}
