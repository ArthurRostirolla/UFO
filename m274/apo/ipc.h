#pragma once
// Layout shared with the app (src/Ufo.Audio/M274Client.cs). Both sides memory-map the same
// files in C:\ProgramData\UFO, so settings are live and also survive restarts.
#include <cstdint>

#include "output.h"
#include "voice.h"

namespace m274::ipc {

constexpr std::uint32_t kMagic = 0x324F4655;  // "UFO2" (bumped whenever a layout below changes)
constexpr wchar_t kRenderParamsPath[] = L"C:\\ProgramData\\UFO\\engine.bin";
constexpr wchar_t kCaptureParamsPath[] = L"C:\\ProgramData\\UFO\\mic.bin";
constexpr wchar_t kRenderMetersPath[] = L"C:\\ProgramData\\UFO\\engine-meters.bin";
constexpr wchar_t kCaptureMetersPath[] = L"C:\\ProgramData\\UFO\\mic-meters.bin";

/// Written by the app as a seqlock: sequence goes odd, payload is written, sequence goes even.
/// The engine only accepts a payload read between two identical even sequence values.
struct ParamsFile {
    std::uint32_t magic;
    std::uint32_t sequence;
    OutputParams params;
};

struct VoiceParamsFile {
    std::uint32_t magic;
    std::uint32_t sequence;
    VoiceParams params;
};

/// Written by the engine once per audio block; read by the app for its meters.
struct MetersFile {
    std::uint32_t magic;
    std::uint32_t blocks;        // increments while audio flows: proves the engine is alive
    float peakLeft, peakRight;   // output peak of the last block, linear
    float limiterGain;           // smallest limiter gain of the last block (1 = untouched)
    float headroomDb;
    float sampleRate;
    std::uint32_t channels;
    std::uint32_t latencyFrames;
    std::uint32_t paramsSequence;  // last settings the engine applied
    float spectrum[OutputChain::kSpectrumBands];  // output spectrum in dBFS (render only)
};

static_assert(sizeof(ParamsFile) == 8 + 48 + 32 * 16, "ParamsFile layout is mirrored in M274Client.cs");
static_assert(sizeof(VoiceParamsFile) == 8 + 48 + 8 * 16, "VoiceParamsFile layout is mirrored in M274Client.cs");
static_assert(sizeof(MetersFile) == 40 + 32 * 4, "MetersFile layout is mirrored in M274Client.cs");

}  // namespace m274::ipc
