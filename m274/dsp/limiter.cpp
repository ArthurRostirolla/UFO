#include "limiter.h"

#include <algorithm>
#include <cmath>
#include <numbers>

namespace m274 {

void Limiter::prepare(double sampleRate, double ceilingDb, double releaseMs) {
    ceiling_ = std::pow(10.0, ceilingDb / 20.0);
    release_ = 1 - std::exp(-1.0 / (releaseMs * 0.001 * sampleRate));

    // Windowed-sinc interpolators for the positions 1/4, 2/4 and 3/4 between samples.
    for (std::size_t phase = 0; phase < kPhases; ++phase) {
        double offset = (phase + 1) / 4.0, sum = 0;
        for (std::size_t tap = 0; tap < kTaps; ++tap) {
            double t = static_cast<double>(tap) - (kTaps / 2.0 - 1) - offset;
            double sinc = std::sin(std::numbers::pi * t) / (std::numbers::pi * t);  // t is never 0
            double window = 0.5 + 0.5 * std::cos(std::numbers::pi * t / (kTaps / 2.0));  // Hann
            interp_[phase][tap] = sinc * window;
            sum += interp_[phase][tap];
        }
        for (double& tap : interp_[phase]) {
            tap /= sum;
        }
    }
    reset();
}

void Limiter::reset() {
    delayL_.fill(0);
    delayR_.fill(0);
    histL_.fill(0);
    histR_.fill(0);
    needed_.fill(1);
    smoothed_.fill(1);
    gainSum_ = static_cast<double>(kSmooth);
    envelope_ = 1;
    minGain_ = 1;
    delayPos_ = smoothPos_ = histPos_ = 0;
}

double Limiter::truePeak(const std::array<double, kTaps>& history, std::size_t head) const {
    // history[head] is the newest sample; taps run from oldest to newest.
    double peak = 0;
    for (const auto& phase : interp_) {
        double value = 0;
        for (std::size_t tap = 0; tap < kTaps; ++tap) {
            value += phase[tap] * history[(head + 1 + tap) % kTaps];
        }
        peak = std::max(peak, std::abs(value));
    }
    return peak;
}

void Limiter::process(double& left, double& right) {
    // Gain for the frame leaving now: the average of envelopes that have all seen it.
    double gain = std::min(1.0, gainSum_ / static_cast<double>(kSmooth));

    histL_[histPos_] = left;
    histR_[histPos_] = right;
    double peak = std::max({std::abs(left), std::abs(right), truePeak(histL_, histPos_), truePeak(histR_, histPos_)});
    histPos_ = (histPos_ + 1) % kTaps;

    double outL = delayL_[delayPos_], outR = delayR_[delayPos_];
    delayL_[delayPos_] = left;
    delayR_[delayPos_] = right;
    needed_[delayPos_] = peak > ceiling_ ? ceiling_ / peak : 1.0;
    delayPos_ = (delayPos_ + 1) % kLatency;

    // ponytail: linear scan for the window minimum (78 values per frame, ~3.7M ops/s at 48 kHz).
    // Use a monotonic deque if the look-ahead ever grows.
    double target = *std::min_element(needed_.begin(), needed_.end());
    envelope_ = target < envelope_ ? target : envelope_ + (target - envelope_) * release_;

    gainSum_ += envelope_ - smoothed_[smoothPos_];
    smoothed_[smoothPos_] = envelope_;
    smoothPos_ = (smoothPos_ + 1) % kSmooth;

    minGain_ = std::min(minGain_, gain);
    left = outL * gain;
    right = outR * gain;
}

double Limiter::takeMinGain() {
    double value = minGain_;
    minGain_ = 1;
    return value;
}

}  // namespace m274
