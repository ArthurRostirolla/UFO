#pragma once

#include <array>
#include <complex>
#include <cstddef>
#include <cstdint>

#include "biquad.h"
#include "dynamics.h"
#include "fft.h"
#include "limiter.h"

namespace m274 {

enum class BandType : std::uint32_t { Peak = 0, LowShelf = 1, HighShelf = 2, HighPass = 3 };

struct Band {
    BandType type = BandType::Peak;
    float frequency = 1000;
    float gainDb = 0;
    float q = 1.41f;
};

/// Everything the app controls on the output. Plain data: this exact layout travels through the
/// mapped settings file (mirrored in src/Ufo.Audio/M274Client.cs).
struct EngineParams {
    static constexpr std::size_t kMaxBands = 32;

    std::uint32_t enabled = 1;      // 0 = bit-exact pass-through
    std::uint32_t bandCount = 0;
    float trimDb = 0;               // user volume trim, applied after the automatic headroom
    std::uint32_t limiterEnabled = 1;
    float crossfeed = 0;            // 0..1
    float night = 0;                // 0..1: dynamic range compression (quiet parts up, loud parts down)
    float footsteps = 0;            // 0..1: lifts quiet detail around 2-4 kHz, leaves loud sounds alone
    float bassEnhance = 0;          // 0..1: adds harmonics of the low bass (perceived weight, no extra excursion)
    std::uint32_t bassProtect = 0;  // 1 = ease back bass that boosts pushed above full scale, before the limiter has to
    std::uint32_t reserved[3] = {};
    std::array<Band, kMaxBands> bands{};
};

/// Stereo output chain:
/// EQ -> bass harmonics -> detail lift -> headroom/trim -> bass protection -> night -> crossfeed -> limiter.
/// process() never allocates or locks; setParams() may be called from the audio thread between blocks.
class Engine {
public:
    static constexpr std::size_t kSpectrumBands = 32;

    void prepare(double sampleRate);

    /// Applies new settings. Filter coefficients glide to the new values over ~10 ms (no clicks).
    void setParams(const EngineParams& params);

    /// Processes interleaved float frames in place. Channels beyond the first two pass untouched.
    void process(float* samples, std::size_t frames, std::size_t channels);

    /// Headroom in dB (<= 0) that keeps the current curve from boosting above full scale.
    /// Based on the real peak of the combined curve, so overlapping bands are covered.
    double headroomDb() const { return headroomDb_; }

    /// Combined EQ response at a frequency in dB, headroom excluded.
    double responseDb(double frequency) const;

    std::size_t latencyFrames() const { return params_.enabled && params_.limiterEnabled ? Limiter::kLatency : 0; }
    double takeLimiterMinGain() { return limiter_.takeMinGain(); }

    /// Output spectrum in dBFS over 32 log-spaced bands from 20 Hz to 20 kHz (updated ~23x a second).
    const std::array<float, kSpectrumBands>& spectrum() const { return spectrum_; }

private:
    static constexpr std::size_t kGlideFrames = 480;
    static constexpr std::size_t kFftSize = 1024;

    void analyze();

    double sampleRate_ = 48000, headroomDb_ = 0, gain_ = 1, gainTarget_ = 1;
    std::size_t glideLeft_ = 0;
    EngineParams params_{};
    std::array<BiquadCoeffs, EngineParams::kMaxBands> coeffs_{}, target_{}, step_{};
    std::array<std::array<BiquadState, EngineParams::kMaxBands>, 2> state_{};
    Limiter limiter_;

    // Extras
    Crossfeed crossfeed_;
    Compressor night_;
    BiquadCoeffs detailBand_{}, subsonic_{};
    std::array<BiquadState, 2> detailState_{}, subsonicState_{};
    Envelope detailEnvelope_, bassEnvelope_;
    std::array<OnePole, 2> bassLow_{};
    OnePole harmonicLow_, harmonicDc_, harmonicTop_;

    // Analyzer
    Fft fft_;
    std::array<float, kFftSize> ring_{}, window_{};
    std::array<std::complex<float>, kFftSize> bins_{};
    std::array<float, kSpectrumBands> spectrum_{};
    std::size_t ringPos_ = 0;
};

}  // namespace m274
