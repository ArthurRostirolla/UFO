#pragma once
// Small building blocks shared by the output chain (engine) and the microphone chain (voice).
#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <numbers>

namespace ufo {

inline double dbToGain(double db) { return std::pow(10.0, db / 20.0); }
inline double gainToDb(double gain) { return 20.0 * std::log10(std::max(gain, 1e-9)); }

/// Coefficient for a one-pole smoother that covers ~63% of a step in the given time.
inline double timeCoeff(double milliseconds, double sampleRate) {
    return 1.0 - std::exp(-1.0 / (std::max(milliseconds, 0.01) * 0.001 * sampleRate));
}

/// First-order low-pass; x - lp(x) is the matching high-pass, so splits recombine exactly.
struct OnePole {
    double a = 1, z = 0;
    void setCutoff(double frequency, double sampleRate) { a = 1.0 - std::exp(-2.0 * std::numbers::pi * frequency / sampleRate); }
    double process(double x) { return z += a * (x - z); }
};

/// Peak envelope follower with separate attack and release.
struct Envelope {
    double attack = 1, release = 1, value = 0;
    void set(double attackMs, double releaseMs, double sampleRate) {
        attack = timeCoeff(attackMs, sampleRate);
        release = timeCoeff(releaseMs, sampleRate);
    }
    double process(double x) {
        double level = std::abs(x);
        value += (level > value ? attack : release) * (level - value);
        return value;
    }
};

/// Headphone crossfeed: a delayed, low-passed share of each channel leaks into the other, like
/// loudspeakers in a room, which makes hard-panned music less tiring. Level-compensated for mono.
class Crossfeed {
public:
    void prepare(double sampleRate) {
        lowL_.setCutoff(700, sampleRate);
        lowR_.setCutoff(700, sampleRate);
        delay_ = std::clamp<std::size_t>(static_cast<std::size_t>(0.00029 * sampleRate), 1, kMaxDelay - 1);  // ~0.29 ms
        bufferL_.fill(0);
        bufferR_.fill(0);
    }
    void process(double& left, double& right, double amount) {
        double feedL = bufferL_[pos_], feedR = bufferR_[pos_];
        bufferL_[pos_] = lowL_.process(left);
        bufferR_[pos_] = lowR_.process(right);
        pos_ = (pos_ + 1) % delay_;
        double feed = 0.5 * amount, norm = 1.0 / (1.0 + 0.35 * amount);
        double outL = (left + feed * feedR) * norm;
        right = (right + feed * feedL) * norm;
        left = outL;
    }

private:
    static constexpr std::size_t kMaxDelay = 64;
    OnePole lowL_, lowR_;
    std::array<double, kMaxDelay> bufferL_{}, bufferR_{};
    std::size_t delay_ = 14, pos_ = 0;
};

/// Downward compressor on the louder of the two channels (stereo image stays put).
class Compressor {
public:
    void prepare(double sampleRate, double attackMs, double releaseMs) { envelope_.set(attackMs, releaseMs, sampleRate); }

    /// Returns the linear gain for this frame. ratio 1 = off.
    double gainFor(double left, double right, double thresholdDb, double ratio, double makeupDb) {
        double levelDb = gainToDb(envelope_.process(std::max(std::abs(left), std::abs(right))));
        double over = std::max(0.0, levelDb - thresholdDb);
        return dbToGain(makeupDb - over * (1.0 - 1.0 / ratio));
    }

private:
    Envelope envelope_;
};

}  // namespace ufo
