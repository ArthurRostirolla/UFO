#include "engine.h"

#include <algorithm>
#include <cmath>
#include <numbers>

namespace m274 {

namespace {

BiquadCoeffs design(const Band& band, double sampleRate) {
    // Keep every filter inside a sane, stable range whatever arrives from the app.
    double frequency = std::clamp(static_cast<double>(band.frequency), 10.0, sampleRate * 0.45);
    double gain = std::clamp(static_cast<double>(band.gainDb), -24.0, 24.0);
    double q = std::clamp(static_cast<double>(band.q), 0.1, 20.0);
    switch (band.type) {
        case BandType::LowShelf: return lowShelf(sampleRate, frequency, gain, q);
        case BandType::HighShelf: return highShelf(sampleRate, frequency, gain, q);
        case BandType::HighPass: return highPass(sampleRate, frequency, q);
        default: return matchedPeak(sampleRate, frequency, gain, q);
    }
}

/// Band-pass with 0 dB at its centre (RBJ, constant peak gain).
BiquadCoeffs bandPass(double sampleRate, double frequency, double q) {
    double w0 = 2 * std::numbers::pi * frequency / sampleRate, alpha = std::sin(w0) / (2 * q), a0 = 1 + alpha;
    return {alpha / a0, 0, -alpha / a0, -2 * std::cos(w0) / a0, (1 - alpha) / a0};
}

double unit(float value) { return std::clamp(static_cast<double>(value), 0.0, 1.0); }

}  // namespace

void Engine::prepare(double sampleRate) {
    sampleRate_ = sampleRate;
    limiter_.prepare(sampleRate);
    for (auto& channel : state_) {
        channel.fill({});
    }
    crossfeed_.prepare(sampleRate);
    night_.prepare(sampleRate, 10, 250);
    detailBand_ = bandPass(sampleRate, std::min(2800.0, sampleRate * 0.3), 1.2);
    subsonic_ = highPass(sampleRate, 15, 0.707);
    detailState_.fill({});
    subsonicState_.fill({});
    detailEnvelope_.set(5, 150, sampleRate);
    bassEnvelope_.set(2, 200, sampleRate);
    for (auto& low : bassLow_) low.setCutoff(120, sampleRate);
    harmonicLow_.setCutoff(110, sampleRate);
    harmonicDc_.setCutoff(60, sampleRate);
    harmonicTop_.setCutoff(400, sampleRate);

    fft_.prepare(kFftSize);
    for (std::size_t i = 0; i < kFftSize; ++i) {
        window_[i] = 0.5f - 0.5f * static_cast<float>(std::cos(2 * std::numbers::pi * i / kFftSize));
    }
    ring_.fill(0);
    spectrum_.fill(-120);
    ringPos_ = 0;

    setParams(params_);
    coeffs_ = target_;  // nothing to glide from on the first block
    gain_ = gainTarget_;
    glideLeft_ = 0;
}

void Engine::setParams(const EngineParams& params) {
    params_ = params;
    params_.bandCount = std::min<std::uint32_t>(params_.bandCount, EngineParams::kMaxBands);
    for (std::size_t i = 0; i < EngineParams::kMaxBands; ++i) {
        target_[i] = i < params_.bandCount ? design(params_.bands[i], sampleRate_) : BiquadCoeffs{};
    }

    // Peak of the combined curve: a 1/24-octave grid from 20 Hz to 20 kHz plus each band's own
    // centre, which the grid can step past.
    double peak = 0;
    for (int step = 0; step <= 240; ++step) {
        double frequency = 20.0 * std::pow(2.0, step / 24.0);
        if (frequency > sampleRate_ * 0.45) {
            break;
        }
        peak = std::max(peak, responseDb(frequency));
    }
    for (std::size_t i = 0; i < params_.bandCount; ++i) {
        peak = std::max(peak, responseDb(std::clamp(static_cast<double>(params_.bands[i].frequency), 10.0, sampleRate_ * 0.45)));
    }
    headroomDb_ = -peak;
    double trim = std::clamp(static_cast<double>(params_.trimDb), -24.0, 12.0);
    gainTarget_ = dbToGain(headroomDb_ + trim);

    for (std::size_t i = 0; i < EngineParams::kMaxBands; ++i) {
        step_[i] = {(target_[i].b0 - coeffs_[i].b0) / kGlideFrames, (target_[i].b1 - coeffs_[i].b1) / kGlideFrames,
                    (target_[i].b2 - coeffs_[i].b2) / kGlideFrames, (target_[i].a1 - coeffs_[i].a1) / kGlideFrames,
                    (target_[i].a2 - coeffs_[i].a2) / kGlideFrames};
    }
    glideLeft_ = kGlideFrames;
}

double Engine::responseDb(double frequency) const {
    double total = 0;
    for (std::size_t i = 0; i < params_.bandCount; ++i) {
        total += magnitudeDb(target_[i], sampleRate_, frequency);
    }
    return total;
}

void Engine::process(float* samples, std::size_t frames, std::size_t channels) {
    if (!params_.enabled || channels == 0) {
        return;  // untouched buffer: bit-exact bypass
    }
    const double crossfeed = unit(params_.crossfeed), night = unit(params_.night);
    const double footsteps = unit(params_.footsteps), bassEnhance = unit(params_.bassEnhance);
    double gainStep = glideLeft_ ? (gainTarget_ - gain_) / static_cast<double>(glideLeft_) : 0;

    for (std::size_t frame = 0; frame < frames; ++frame) {
        if (glideLeft_) {
            for (std::size_t i = 0; i < EngineParams::kMaxBands; ++i) {
                coeffs_[i].b0 += step_[i].b0;
                coeffs_[i].b1 += step_[i].b1;
                coeffs_[i].b2 += step_[i].b2;
                coeffs_[i].a1 += step_[i].a1;
                coeffs_[i].a2 += step_[i].a2;
            }
            gain_ += gainStep;
            if (--glideLeft_ == 0) {
                coeffs_ = target_;
                gain_ = gainTarget_;
            }
        }

        float* in = samples + frame * channels;
        double left = in[0], right = channels > 1 ? in[1] : in[0];
        for (std::size_t i = 0; i < params_.bandCount; ++i) {
            left = state_[0][i].process(coeffs_[i], left);
            right = state_[1][i].process(coeffs_[i], right);
        }

        if (bassEnhance > 0) {
            // Rectifying the low bass creates its harmonics; the ear rebuilds the fundamental from them.
            double low = harmonicLow_.process(0.5 * (left + right));
            double rectified = std::abs(low);
            double harmonics = harmonicTop_.process(rectified - harmonicDc_.process(rectified));
            left += 0.8 * bassEnhance * harmonics;
            right += 0.8 * bassEnhance * harmonics;
        }

        if (footsteps > 0) {
            // Upward lift of the 2-4 kHz band only while it is quiet: full below -45 dBFS, none above -25.
            double bandL = detailState_[0].process(detailBand_, left), bandR = detailState_[1].process(detailBand_, right);
            double levelDb = gainToDb(detailEnvelope_.process(std::max(std::abs(bandL), std::abs(bandR))));
            double lift = dbToGain(9.0 * footsteps * std::clamp((-25.0 - levelDb) / 20.0, 0.0, 1.0)) - 1.0;
            left += lift * bandL;
            right += lift * bandR;
        }

        left *= gain_;
        right *= gain_;

        if (params_.bassProtect) {
            left = subsonicState_[0].process(subsonic_, left);
            right = subsonicState_[1].process(subsonic_, right);
            // A first-order low shelf that only cuts, and only bass pushed ABOVE full scale by the
            // user's boosts: up to -6 dB at +9 dBFS. Mastered music sits right at full scale all the
            // time, so starting any lower (it used to start at -12 dBFS) thinned the bass of every
            // track and left the mids sticking out.
            double lowL = bassLow_[0].process(left), lowR = bassLow_[1].process(right);
            double levelDb = gainToDb(bassEnvelope_.process(std::max(std::abs(lowL), std::abs(lowR))));
            double cut = 0.5 * std::clamp(levelDb / 9.0, 0.0, 1.0);
            left -= cut * lowL;
            right -= cut * lowR;
        }

        if (night > 0) {
            double g = night_.gainFor(left, right, -30.0, 1.0 + 3.0 * night, 9.0 * night);
            left *= g;
            right *= g;
        }

        if (crossfeed > 0 && channels > 1) {
            crossfeed_.process(left, right, crossfeed);
        }

        if (params_.limiterEnabled) {
            limiter_.process(left, right);
        }
        in[0] = static_cast<float>(left);
        if (channels > 1) {
            in[1] = static_cast<float>(right);
        }

        ring_[ringPos_] = static_cast<float>(0.5 * (left + right));
        if (++ringPos_ == kFftSize) {
            ringPos_ = 0;
            analyze();
        }
    }
    // Flush denormals that an idle (silent) filter would otherwise grind on.
    for (auto& channel : state_) {
        for (auto& s : channel) {
            if (std::abs(s.z1) < 1e-30) s.z1 = 0;
            if (std::abs(s.z2) < 1e-30) s.z2 = 0;
        }
    }
}

void Engine::analyze() {
    for (std::size_t i = 0; i < kFftSize; ++i) {
        bins_[i] = ring_[i] * window_[i];
    }
    fft_.forward(bins_.data());
    // Strongest bin inside each log-spaced band, scaled so a full-scale sine reads 0 dBFS.
    const double scale = 4.0 / kFftSize;
    for (std::size_t band = 0; band < kSpectrumBands; ++band) {
        double low = 20.0 * std::pow(1000.0, static_cast<double>(band) / kSpectrumBands);
        double high = 20.0 * std::pow(1000.0, static_cast<double>(band + 1) / kSpectrumBands);
        auto first = static_cast<std::size_t>(low * kFftSize / sampleRate_);
        auto last = std::min<std::size_t>(static_cast<std::size_t>(high * kFftSize / sampleRate_), kFftSize / 2 - 1);
        first = std::clamp<std::size_t>(first, 1, kFftSize / 2 - 1);
        float strongest = 0;
        for (std::size_t bin = first; bin <= std::max(first, last); ++bin) {
            strongest = std::max(strongest, std::abs(bins_[bin]));
        }
        spectrum_[band] = static_cast<float>(gainToDb(strongest * scale));
    }
}

}  // namespace m274
