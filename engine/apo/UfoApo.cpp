// UFO Engine: the audio processing object Windows loads into audiodg.exe for the headset endpoints.
// One class, two CLSIDs: the render one reads engine.bin, the capture one reads mic.bin.
//
// Safety rules for everything on the audio thread (APOProcess): no allocation, no locks, no I/O
// calls. Anything unexpected (odd format, missing settings file) turns the object into a plain
// pass-through instead of failing, so a problem here never mutes the headset.
#include <windows.h>

#include <audioenginebaseapo.h>
#include <baseaudioprocessingobject.h>
#include <algorithm>
#include <cstdarg>
#include <cstdio>
#include <new>

#include "engine.h"
#include "ipc.h"

namespace {

// {7C1F2D3A-5B6E-4A91-9C0D-55464F454E01} / ...02
constexpr CLSID kRenderClsid = {0x7c1f2d3a, 0x5b6e, 0x4a91, {0x9c, 0x0d, 0x55, 0x46, 0x4f, 0x45, 0x4e, 0x01}};
constexpr CLSID kCaptureClsid = {0x7c1f2d3a, 0x5b6e, 0x4a91, {0x9c, 0x0d, 0x55, 0x46, 0x4f, 0x45, 0x4e, 0x02}};

HMODULE g_module = nullptr;
LONG g_objects = 0;

/// Diagnostic diary in C:\ProgramData\UFO\apo.log: one line per call Windows makes outside the audio
/// thread, with its result. It is how a failure inside audiodg.exe (which shows no error anywhere)
/// gets explained. Never called from APOProcess, except once for the first block.
void trace(const char* format, ...) {
    char text[512];
    SYSTEMTIME now;
    GetLocalTime(&now);
    int length = sprintf_s(text, "%02u:%02u:%02u.%03u pid %lu  ", now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, GetCurrentProcessId());
    va_list arguments;
    va_start(arguments, format);
    int written = vsnprintf_s(text + length, sizeof(text) - length - 2, _TRUNCATE, format, arguments);
    va_end(arguments);
    length += written < 0 ? static_cast<int>(strlen(text + length)) : written;
    text[length++] = '\r';
    text[length++] = '\n';
    HANDLE file = CreateFileW(L"C:\\ProgramData\\UFO\\apo.log", FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                              nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) return;
    DWORD done;
    WriteFile(file, text, static_cast<DWORD>(length), &done, nullptr);
    CloseHandle(file);
}

/// "float32 2ch 48000Hz" for the diary; "(none)" when Windows passed no format.
void describe(IAudioMediaType* type, char* text, size_t size) {
    UNCOMPRESSEDAUDIOFORMAT format{};
    if (!type || FAILED(type->GetUncompressedAudioFormat(&format))) {
        strcpy_s(text, size, "(none)");
        return;
    }
    sprintf_s(text, size, "%s%lu/%lu %luch %.0fHz", format.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT ? "float" : "pcm",
              format.dwValidBitsPerSample, format.dwBytesPerSampleContainer * 8, format.dwSamplesPerFrame, format.fFramesPerSecond);
}

APO_REG_PROPERTIES makeRegProperties(const CLSID& clsid, const wchar_t* name) {
    APO_REG_PROPERTIES p{};
    p.clsid = clsid;
    p.Flags = static_cast<APO_FLAG>(APO_FLAG_DEFAULT | APO_FLAG_INPLACE);
    wcscpy_s(p.szFriendlyName, name);
    wcscpy_s(p.szCopyrightInfo, L"UFO");
    p.u32MajorVersion = 1;
    p.u32MinInputConnections = p.u32MaxInputConnections = 1;
    p.u32MinOutputConnections = p.u32MaxOutputConnections = 1;
    p.u32MaxInstances = 0xffffffff;
    p.u32NumAPOInterfaces = 1;
    p.iidAPOInterfaceList[0] = __uuidof(IAudioSystemEffects);
    return p;
}

const APO_REG_PROPERTIES kRenderProps = makeRegProperties(kRenderClsid, L"UFO Engine");
const APO_REG_PROPERTIES kCaptureProps = makeRegProperties(kCaptureClsid, L"UFO Engine (microfone)");

/// A file mapped into memory; stays null (and the engine passes audio through) if it can't open.
struct MappedFile {
    HANDLE file = INVALID_HANDLE_VALUE, mapping = nullptr;
    void* view = nullptr;

    void open(const wchar_t* path, bool writable, DWORD size) {
        close();
        file = CreateFileW(path, writable ? GENERIC_READ | GENERIC_WRITE : GENERIC_READ,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                           writable ? OPEN_ALWAYS : OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) {
            return;
        }
        LARGE_INTEGER length{};
        if (!GetFileSizeEx(file, &length) || (!writable && length.QuadPart < size)) {
            close();
            return;
        }
        mapping = CreateFileMappingW(file, nullptr, writable ? PAGE_READWRITE : PAGE_READONLY, 0, size, nullptr);
        if (mapping) {
            view = MapViewOfFile(mapping, writable ? FILE_MAP_WRITE : FILE_MAP_READ, 0, 0, size);
        }
        if (!view) {
            close();
        }
    }

    void close() {
        if (view) UnmapViewOfFile(view);
        if (mapping) CloseHandle(mapping);
        if (file != INVALID_HANDLE_VALUE) CloseHandle(file);
        view = nullptr;
        mapping = nullptr;
        file = INVALID_HANDLE_VALUE;
    }
};

/// The object's own IUnknown, used when it lives inside another COM object (aggregation). Same
/// vtable layout as IUnknown, so a pointer to it can be handed out as the inner IUnknown.
struct INonDelegatingUnknown {
    virtual HRESULT STDMETHODCALLTYPE NonDelegatingQueryInterface(REFIID iid, void** object) = 0;
    virtual ULONG STDMETHODCALLTYPE NonDelegatingAddRef() = 0;
    virtual ULONG STDMETHODCALLTYPE NonDelegatingRelease() = 0;
};

class UfoApo final : public CBaseAudioProcessingObject, public IAudioSystemEffects, public INonDelegatingUnknown {
public:
    /// audiodg.exe creates system effects aggregated inside its own wrapper object: `outer` is that
    /// wrapper, and every interface handed out must forward IUnknown to it. Refusing aggregation
    /// (CLASS_E_NOAGGREGATION) leaves the endpoint unable to open any stream.
    UfoApo(bool isCapture, IUnknown* outer)
        : CBaseAudioProcessingObject(isCapture ? &kCaptureProps : &kRenderProps), isCapture_(isCapture) {
        outer_ = outer ? outer : reinterpret_cast<IUnknown*>(static_cast<INonDelegatingUnknown*>(this));
        InterlockedIncrement(&g_objects);
    }
    ~UfoApo() override {
        paramsFile_.close();
        metersFile_.close();
        InterlockedDecrement(&g_objects);
    }

    // IUnknown as seen from outside: forwarded to the owner (the wrapper, or this object itself).
    STDMETHODIMP QueryInterface(REFIID iid, void** object) override { return outer_->QueryInterface(iid, object); }
    STDMETHODIMP_(ULONG) AddRef() override { return outer_->AddRef(); }
    STDMETHODIMP_(ULONG) Release() override { return outer_->Release(); }

    // The object's own IUnknown.
    HRESULT STDMETHODCALLTYPE NonDelegatingQueryInterface(REFIID iid, void** object) override {
        if (!object) return E_POINTER;
        if (iid == __uuidof(IUnknown)) {
            *object = static_cast<INonDelegatingUnknown*>(this);
            NonDelegatingAddRef();
            return S_OK;
        }
        if (iid == __uuidof(IAudioProcessingObject)) {
            *object = static_cast<IAudioProcessingObject*>(this);
        } else if (iid == __uuidof(IAudioProcessingObjectRT)) {
            *object = static_cast<IAudioProcessingObjectRT*>(this);
        } else if (iid == __uuidof(IAudioProcessingObjectConfiguration)) {
            *object = static_cast<IAudioProcessingObjectConfiguration*>(this);
        } else if (iid == __uuidof(IAudioSystemEffects)) {
            *object = static_cast<IAudioSystemEffects*>(this);
        } else {
            *object = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();  // counted by the owner
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE NonDelegatingAddRef() override { return InterlockedIncrement(&references_); }
    ULONG STDMETHODCALLTYPE NonDelegatingRelease() override {
        LONG left = InterlockedDecrement(&references_);
        if (left == 0) delete this;
        return left;
    }

    // IAudioProcessingObject
    STDMETHODIMP Initialize(UINT32 size, BYTE* data) override {
        if ((data == nullptr) != (size == 0)) return E_INVALIDARG;
        if (m_bIsInitialized) return APOERR_ALREADY_INITIALIZED;
        m_bIsInitialized = true;
        trace("%s Initialize size=%u", isCapture_ ? "mic" : "out", size);
        return S_OK;
    }

    // Format negotiation. The base class accepts 32-bit float; when Windows offers anything else it
    // must be told which format to use instead, or the stream cannot be built at all.
    STDMETHODIMP IsInputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override {
        return negotiate("IsInputFormatSupported", opposite, requested, supported,
                         CBaseAudioProcessingObject::IsInputFormatSupported(opposite, requested, supported));
    }
    STDMETHODIMP IsOutputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override {
        return negotiate("IsOutputFormatSupported", opposite, requested, supported,
                         CBaseAudioProcessingObject::IsOutputFormatSupported(opposite, requested, supported));
    }

    STDMETHODIMP GetLatency(HNSTIME* time) override {
        if (!time) return E_POINTER;
        *time = sampleRate_ > 0 ? static_cast<HNSTIME>(latencyFrames() * 10'000'000.0 / sampleRate_) : 0;
        return S_OK;
    }

    // IAudioProcessingObjectConfiguration: the last non-real-time call before audio starts.
    STDMETHODIMP LockForProcess(UINT32 inputs, APO_CONNECTION_DESCRIPTOR** in, UINT32 outputs,
                                APO_CONNECTION_DESCRIPTOR** out) override {
        HRESULT result = CBaseAudioProcessingObject::LockForProcess(inputs, in, outputs, out);
        char inText[64], outText[64];
        describe(inputs ? in[0]->pFormat : nullptr, inText, sizeof(inText));
        describe(outputs ? out[0]->pFormat : nullptr, outText, sizeof(outText));
        trace("%s LockForProcess in=%s (%u frames) out=%s (%u frames) -> 0x%08lX", isCapture_ ? "mic" : "out", inText,
              inputs ? in[0]->u32MaxFrameCount : 0, outText, outputs ? out[0]->u32MaxFrameCount : 0, result);
        if (FAILED(result)) return result;

        UNCOMPRESSEDAUDIOFORMAT format{};
        canProcess_ = SUCCEEDED(in[0]->pFormat->GetUncompressedAudioFormat(&format)) &&
                      format.guidFormatType == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT && format.dwBytesPerSampleContainer == 4 &&
                      format.dwSamplesPerFrame >= 1;
        channels_ = format.dwSamplesPerFrame;
        sampleRate_ = format.fFramesPerSecond;
        if (!canProcess_) return S_OK;  // unknown format: stay loaded as a pass-through

        // Until the app writes settings, both chains are bit-exact pass-throughs.
        ufo::EngineParams renderOff;
        renderOff.enabled = 0;
        ufo::VoiceParams voiceOff;
        voiceOff.enabled = 0;
        if (isCapture_) {
            voice_.prepare(sampleRate_);
            voice_.setParams(voiceOff);
        } else {
            engine_.prepare(sampleRate_);
            engine_.setParams(renderOff);
        }
        paramsFile_.open(isCapture_ ? ufo::ipc::kCaptureParamsPath : ufo::ipc::kRenderParamsPath, false,
                         isCapture_ ? sizeof(ufo::ipc::VoiceParamsFile) : sizeof(ufo::ipc::ParamsFile));
        metersFile_.open(isCapture_ ? ufo::ipc::kCaptureMetersPath : ufo::ipc::kRenderMetersPath, true,
                         sizeof(ufo::ipc::MetersFile));
        appliedSequence_ = 0xffffffff;
        blocks_ = 0;
        hasTracedFirstBlock_ = false;
        pullParams();
        trace("%s ready: settings %s, meters %s", isCapture_ ? "mic" : "out", paramsFile_.view ? "mapped" : "MISSING", metersFile_.view ? "mapped" : "MISSING");
        return S_OK;
    }

    STDMETHODIMP UnlockForProcess() override {
        HRESULT result = CBaseAudioProcessingObject::UnlockForProcess();
        trace("%s UnlockForProcess after %u blocks -> 0x%08lX", isCapture_ ? "mic" : "out", blocks_, result);
        paramsFile_.close();
        metersFile_.close();
        return result;
    }

    // IAudioProcessingObjectRT: the audio thread.
    STDMETHODIMP_(void) APOProcess(UINT32, APO_CONNECTION_PROPERTY** in, UINT32, APO_CONNECTION_PROPERTY** out) override {
        APO_CONNECTION_PROPERTY* input = in[0];
        APO_CONNECTION_PROPERTY* output = out[0];
        UINT32 frames = input->u32ValidFrameCount;
        auto* samples = reinterpret_cast<float*>(input->pBuffer);
        auto* destination = reinterpret_cast<float*>(output->pBuffer);
        bool isSilent = input->u32BufferFlags == BUFFER_SILENT;

        if (canProcess_ && samples && frames) {
            if (isSilent) {
                SecureZeroMemory(samples, sizeof(float) * frames * channels_);  // content is undefined when silent
            }
            pullParams();
            if (isCapture_) {
                voice_.process(samples, frames, channels_);
            } else {
                engine_.process(samples, frames, channels_);
            }
            isSilent = false;  // the limiter may still be releasing its delayed tail
            publishMeters(samples, frames);
        }
        if (destination && destination != samples && samples && !isSilent) {
            CopyMemory(destination, samples, sizeof(float) * frames * channels_);
        }
        output->u32ValidFrameCount = frames;
        output->u32BufferFlags = isSilent ? BUFFER_SILENT : BUFFER_VALID;
        if (!hasTracedFirstBlock_) {  // once per stream: proof that audio reached the engine
            hasTracedFirstBlock_ = true;
            trace("%s first block: %u frames, flags %d, in-place %d, processing %d", isCapture_ ? "mic" : "out", frames,
                  static_cast<int>(input->u32BufferFlags), destination == samples, canProcess_);
        }
    }

private:
    HRESULT negotiate(const char* call, IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported, HRESULT result) {
        char requestedText[64], oppositeText[64], answerText[64] = "(none)";
        describe(requested, requestedText, sizeof(requestedText));
        describe(opposite, oppositeText, sizeof(oppositeText));
        UNCOMPRESSEDAUDIOFORMAT format{};
        if (FAILED(result) && requested && supported && !*supported && SUCCEEDED(requested->GetUncompressedAudioFormat(&format))) {
            // Refused with no alternative: offer the same layout in 32-bit float.
            format.guidFormatType = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
            format.dwBytesPerSampleContainer = 4;
            format.dwValidBitsPerSample = 32;
            if (SUCCEEDED(CreateAudioMediaTypeFromUncompressedAudioFormat(&format, supported))) {
                trace("%s base refused with 0x%08lX; offering float", call, result);
                result = S_FALSE;
            }
        }
        if (supported) describe(*supported, answerText, sizeof(answerText));
        trace("%s %s requested=%s opposite=%s -> 0x%08lX answer=%s", isCapture_ ? "mic" : "out", call, requestedText, oppositeText, result, answerText);
        return result;
    }

    /// Seqlock read of the app's settings; applies them only when a complete, new set is seen.
    void pullParams() {
        // Both files start with the same header: magic, then sequence, then the payload.
        auto* header = static_cast<const volatile std::uint32_t*>(paramsFile_.view);
        if (!header || header[0] != ufo::ipc::kMagic) return;
        std::uint32_t before = header[1];
        if (before == appliedSequence_ || (before & 1)) return;
        const void* payload = static_cast<const BYTE*>(paramsFile_.view) + 2 * sizeof(std::uint32_t);
        if (isCapture_) {
            ufo::VoiceParams copy;
            CopyMemory(&copy, payload, sizeof(copy));
            if (header[1] != before) return;  // the app was mid-write; try again next block
            voice_.setParams(copy);
        } else {
            ufo::EngineParams copy;
            CopyMemory(&copy, payload, sizeof(copy));
            if (header[1] != before) return;
            engine_.setParams(copy);
        }
        appliedSequence_ = before;
    }

    std::size_t latencyFrames() const { return isCapture_ ? voice_.latencyFrames() : engine_.latencyFrames(); }

    void publishMeters(const float* samples, UINT32 frames) {
        auto* meters = static_cast<ufo::ipc::MetersFile*>(metersFile_.view);
        if (!meters) return;
        float peakLeft = 0, peakRight = 0;
        for (UINT32 frame = 0; frame < frames; ++frame) {
            float left = samples[frame * channels_], right = channels_ > 1 ? samples[frame * channels_ + 1] : left;
            peakLeft = std::max(peakLeft, left < 0 ? -left : left);
            peakRight = std::max(peakRight, right < 0 ? -right : right);
        }
        meters->magic = ufo::ipc::kMagic;
        meters->peakLeft = peakLeft;
        meters->peakRight = peakRight;
        meters->limiterGain = static_cast<float>(isCapture_ ? voice_.takeLimiterMinGain() : engine_.takeLimiterMinGain());
        meters->headroomDb = isCapture_ ? 0.0f : static_cast<float>(engine_.headroomDb());
        meters->sampleRate = sampleRate_;
        meters->channels = channels_;
        meters->latencyFrames = static_cast<std::uint32_t>(latencyFrames());
        if (!isCapture_) {
            CopyMemory(meters->spectrum, engine_.spectrum().data(), sizeof(meters->spectrum));
        }
        meters->paramsSequence = appliedSequence_;
        meters->blocks = ++blocks_;
    }

    LONG references_ = 1;
    IUnknown* outer_;  // not counted: the owner outlives this object by definition
    bool isCapture_, canProcess_ = false, hasTracedFirstBlock_ = false;
    UINT32 channels_ = 0;
    float sampleRate_ = 0;
    std::uint32_t appliedSequence_ = 0xffffffff, blocks_ = 0;
    ufo::Engine engine_;      // render endpoints
    ufo::VoiceChain voice_;   // capture endpoints
    MappedFile paramsFile_, metersFile_;
};

class Factory final : public IClassFactory {
public:
    explicit Factory(bool isCapture) : isCapture_(isCapture) {}
    STDMETHODIMP QueryInterface(REFIID iid, void** object) override {
        if (!object) return E_POINTER;
        if (iid == __uuidof(IUnknown) || iid == __uuidof(IClassFactory)) {
            *object = this;
            AddRef();
            return S_OK;
        }
        *object = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&references_); }
    STDMETHODIMP_(ULONG) Release() override {
        LONG left = InterlockedDecrement(&references_);
        if (left == 0) delete this;
        return left;
    }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** object) override {
        if (!object) return E_POINTER;
        *object = nullptr;
        if (outer && iid != __uuidof(IUnknown)) return CLASS_E_NOAGGREGATION;  // COM rule: an aggregator asks for IUnknown
        auto* apo = new (std::nothrow) UfoApo(isCapture_, outer);
        if (!apo) return E_OUTOFMEMORY;
        HRESULT result = apo->NonDelegatingQueryInterface(iid, object);
        apo->NonDelegatingRelease();
        trace("%s CreateInstance %s -> 0x%08lX", isCapture_ ? "mic" : "out", outer ? "aggregated" : "standalone", result);
        return result;
    }
    STDMETHODIMP LockServer(BOOL lock) override {
        lock ? InterlockedIncrement(&g_objects) : InterlockedDecrement(&g_objects);
        return S_OK;
    }

private:
    LONG references_ = 1;
    bool isCapture_;
};

HRESULT writeComServer(const CLSID& clsid, const wchar_t* name) {
    wchar_t id[64], path[MAX_PATH], keyPath[160];
    StringFromGUID2(clsid, id, 64);
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());
    swprintf_s(keyPath, L"SOFTWARE\\Classes\\CLSID\\%s", id);
    HKEY key;
    LSTATUS status = RegCreateKeyExW(HKEY_LOCAL_MACHINE, keyPath, 0, nullptr, 0, KEY_WRITE, nullptr, &key, nullptr);
    if (status != ERROR_SUCCESS) return HRESULT_FROM_WIN32(status);
    RegSetValueExW(key, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(name), static_cast<DWORD>((wcslen(name) + 1) * sizeof(wchar_t)));
    HKEY server;
    status = RegCreateKeyExW(key, L"InprocServer32", 0, nullptr, 0, KEY_WRITE, nullptr, &server, nullptr);
    if (status == ERROR_SUCCESS) {
        RegSetValueExW(server, nullptr, 0, REG_SZ, reinterpret_cast<const BYTE*>(path), static_cast<DWORD>((wcslen(path) + 1) * sizeof(wchar_t)));
        const wchar_t model[] = L"Both";
        RegSetValueExW(server, L"ThreadingModel", 0, REG_SZ, reinterpret_cast<const BYTE*>(model), sizeof(model));
        RegCloseKey(server);
    }
    RegCloseKey(key);
    return HRESULT_FROM_WIN32(status);
}

void deleteComServer(const CLSID& clsid) {
    wchar_t id[64], keyPath[160];
    StringFromGUID2(clsid, id, 64);
    swprintf_s(keyPath, L"SOFTWARE\\Classes\\CLSID\\%s", id);
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, keyPath);
}

}  // namespace

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_module = module;
        DisableThreadLibraryCalls(module);
        wchar_t host[MAX_PATH] = L"?";
        GetModuleFileNameW(nullptr, host, MAX_PATH);
        trace("loaded into %ls", host);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** object) {
    bool isRender = clsid == kRenderClsid, isCapture = clsid == kCaptureClsid;
    trace("DllGetClassObject %s", isRender ? "out" : isCapture ? "mic" : "unknown class");
    if (!isRender && !isCapture) return CLASS_E_CLASSNOTAVAILABLE;
    auto* factory = new (std::nothrow) Factory(isCapture);
    if (!factory) return E_OUTOFMEMORY;
    HRESULT result = factory->QueryInterface(iid, object);
    factory->Release();
    return result;
}

STDAPI DllCanUnloadNow() { return g_objects == 0 ? S_OK : S_FALSE; }

STDAPI DllRegisterServer() {
    HRESULT result = writeComServer(kRenderClsid, kRenderProps.szFriendlyName);
    if (SUCCEEDED(result)) result = writeComServer(kCaptureClsid, kCaptureProps.szFriendlyName);
    if (SUCCEEDED(result)) result = RegisterAPO(&kRenderProps);
    if (SUCCEEDED(result)) result = RegisterAPO(&kCaptureProps);
    return result;
}

STDAPI DllUnregisterServer() {
    UnregisterAPO(kRenderClsid);
    UnregisterAPO(kCaptureClsid);
    deleteComServer(kRenderClsid);
    deleteComServer(kCaptureClsid);
    return S_OK;
}
