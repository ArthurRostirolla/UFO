#pragma once

#include <array>
#include <complex>
#include <cstddef>
#include <cstdint>

#include "biquad.h"
#include "dynamics.h"
#include "engine.h"
#include "fft.h"
#include "limiter.h"

namespace m274 {

/// Everything the app controls on the microphone. Same rules as EngineParams: plain data, fixed
/// layout (mirrored in src/Ufo.Audio/M274Client.cs).
struct VoiceParams {
    static constexpr std::size_t kMaxBands = 8;

    std::uint32_t enabled = 1;      // 0 = bit-exact pass-through
    std::uint32_t highPass = 1;     // 80 Hz rumble filter
    float denoise = 0;              // 0..1 spectral noise reduction
    float gate = 0;                 // 0..1 expander: pushes the background down between phrases
    float deEss = 0;                // 0..1 tames harsh "s" sounds
    float compress = 0;             // 0..1 evens out loud and soft speech
    float leveler = 0;              // 0..1 slow automatic gain towards a steady level
    float trimDb = 0;
    std::uint32_t limiterEnabled = 1;
    std::uint32_t bandCount = 0;
    std::uint32_t reserved[2] = {};
    std::array<Band, kMaxBands> bands{};
};

/// Mono voice chain:
/// high-pass -> noise reduction -> expander -> de-esser -> compressor -> EQ -> leveler -> limiter.
/// Works at any sample rate (the wireless microphone is 16 kHz, the wired one 48 kHz).
class VoiceChain {
public:
    void prepare(double sampleRate);
    void setParams(const VoiceParams& params);

    /// Processes interleaved float frames in place; the first channel is processed and copied to the others.
    void process(float* samples, std::size_t frames, std::size_t channels);

    /// Delay added by the noise reduction (0 when it is off) plus the limiter.
    std::size_t latencyFrames() const;
    double takeLimiterMinGain() { return limiter_.takeMinGain(); }

private:
    static constexpr std::size_t kFrame = 512, kHop = 256, kBins = kFrame / 2 + 1;

    double denoiseSample(double x);
    void denoiseFrame();

    double sampleRate_ = 48000;
    VoiceParams params_{};
    BiquadCoeffs highPass_{};
    BiquadState highPassState_{};
    std::array<BiquadCoeffs, VoiceParams::kMaxBands> bands_{};
    std::array<BiquadState, VoiceParams::kMaxBands> bandState_{};

    // Noise reduction (STFT, 50% overlap, sqrt-Hann on both sides)
    Fft fft_;
    std::array<float, kFrame> window_{}, input_{}, overlap_{};
    std::array<float, kHop> output_{};
    std::array<std::complex<float>, kFrame> spectrum_{};
    std::array<float, kBins> power_{}, noise_{}, gain_{};
    std::size_t hopPos_ = 0;
    bool hasNoiseEstimate_ = false;

    Envelope gateEnvelope_, essEnvelope_, levelEnvelope_;
    OnePole essLow_;
    Compressor compressor_;
    double gateGain_ = 1, gateSmooth_ = 0, levelGainDb_ = 0, levelStep_ = 0;
    Limiter limiter_;
};

}  // namespace m274
