#pragma once

#include <array>
#include <cstddef>

namespace m274 {

/// Stereo-linked look-ahead limiter with inter-sample ("true peak") detection.
///
/// Each input frame gets the gain it would need. A sliding minimum over the look-ahead window,
/// then a moving average, gives a smooth gain that is already low enough when the delayed frame
/// comes out, so the output never exceeds the ceiling. No allocation, fixed latency.
class Limiter {
public:
    static constexpr std::size_t kTaps = 12;       // per phase of the 4x peak detector
    static constexpr std::size_t kSmooth = 72;     // moving-average length: 1.5 ms at 48 kHz
    static constexpr std::size_t kLatency = kSmooth + kTaps / 2;  // frames of delay added

    void prepare(double sampleRate, double ceilingDb = -0.3, double releaseMs = 80);
    void reset();

    /// Processes one stereo frame in place.
    void process(double& left, double& right);

    /// Smallest gain applied since the last call (1 = untouched); for the app's meter.
    double takeMinGain();

private:
    static constexpr std::size_t kPhases = 3;  // the three positions between two samples

    double truePeak(const std::array<double, kTaps>& history, std::size_t head) const;

    double ceiling_ = 1, release_ = 0, envelope_ = 1, gainSum_ = 0, minGain_ = 1;
    std::size_t delayPos_ = 0, smoothPos_ = 0, histPos_ = 0;
    // The interpolated peak is known kTaps/2 frames late, so audio and the minimum window are
    // that much longer than the average: every averaged value has then seen the frame going out.
    std::array<double, kLatency> delayL_{}, delayR_{}, needed_{};
    std::array<double, kSmooth> smoothed_{};
    std::array<double, kTaps> histL_{}, histR_{};
    std::array<std::array<double, kTaps>, kPhases> interp_{};
};

}  // namespace m274
