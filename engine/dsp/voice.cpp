#include "voice.h"

#include <algorithm>
#include <cmath>
#include <numbers>

namespace ufo {

namespace {
double unit(float value) { return std::clamp(static_cast<double>(value), 0.0, 1.0); }
}  // namespace

void VoiceChain::prepare(double sampleRate) {
    sampleRate_ = sampleRate;
    highPass_ = highPass(sampleRate, 80, 0.707);
    highPassState_ = {};
    bandState_.fill({});

    fft_.prepare(kFrame);
    for (std::size_t i = 0; i < kFrame; ++i) {
        window_[i] = static_cast<float>(std::sqrt(0.5 - 0.5 * std::cos(2 * std::numbers::pi * i / kFrame)));
    }
    input_.fill(0);
    overlap_.fill(0);
    output_.fill(0);
    power_.fill(0);
    noise_.fill(0);
    gain_.fill(1);
    hopPos_ = 0;
    hasNoiseEstimate_ = false;

    gateEnvelope_.set(2, 120, sampleRate);
    gateSmooth_ = timeCoeff(15, sampleRate);
    gateGain_ = 1;
    // "s" energy sits above ~5 kHz; on the 16 kHz microphone only up to 8 kHz exists, so split lower.
    essLow_.setCutoff(std::min(5000.0, sampleRate * 0.28), sampleRate);
    essEnvelope_.set(1, 60, sampleRate);
    compressor_.prepare(sampleRate, 5, 120);
    levelEnvelope_.set(50, 400, sampleRate);
    levelGainDb_ = 0;
    levelStep_ = 3.0 / sampleRate;  // the leveler moves at most 3 dB per second
    limiter_.prepare(sampleRate, -1.0);
    setParams(params_);
}

void VoiceChain::setParams(const VoiceParams& params) {
    params_ = params;
    params_.bandCount = std::min<std::uint32_t>(params_.bandCount, VoiceParams::kMaxBands);
    for (std::size_t i = 0; i < params_.bandCount; ++i) {
        const Band& band = params_.bands[i];
        double frequency = std::clamp(static_cast<double>(band.frequency), 20.0, sampleRate_ * 0.45);
        double gain = std::clamp(static_cast<double>(band.gainDb), -18.0, 18.0);
        double q = std::clamp(static_cast<double>(band.q), 0.1, 20.0);
        bands_[i] = band.type == BandType::LowShelf    ? lowShelf(sampleRate_, frequency, gain, q)
                    : band.type == BandType::HighShelf ? highShelf(sampleRate_, frequency, gain, q)
                    : band.type == BandType::HighPass  ? highPass(sampleRate_, frequency, q)
                                                       : matchedPeak(sampleRate_, frequency, gain, q);
    }
}

std::size_t VoiceChain::latencyFrames() const {
    if (!params_.enabled) return 0;
    return (params_.denoise > 0 ? kFrame : 0) + (params_.limiterEnabled ? Limiter::kLatency : 0);
}

/// Feeds one sample into the overlap-add noise reducer and returns one (delayed by kFrame).
double VoiceChain::denoiseSample(double x) {
    input_[kFrame - kHop + hopPos_] = static_cast<float>(x);
    double y = output_[hopPos_];
    if (++hopPos_ == kHop) {
        hopPos_ = 0;
        denoiseFrame();
        std::copy(input_.begin() + kHop, input_.end(), input_.begin());
    }
    return y;
}

void VoiceChain::denoiseFrame() {
    for (std::size_t i = 0; i < kFrame; ++i) {
        spectrum_[i] = input_[i] * window_[i];
    }
    fft_.forward(spectrum_.data());

    const float amount = static_cast<float>(unit(params_.denoise));
    const float floorGain = static_cast<float>(dbToGain(-6.0 - 18.0 * amount));  // never fully mute: avoids "underwater" artefacts
    const float oversubtract = 1.0f + 1.5f * amount;
    // The noise floor is tracked as the running minimum of each bin's smoothed power: speech comes
    // and goes, steady noise (fan, hiss) stays. It may creep up ~3 dB per second to follow changes.
    const float rise = static_cast<float>(std::pow(2.0, static_cast<double>(kHop) / sampleRate_));
    for (std::size_t bin = 0; bin < kBins; ++bin) {
        float power = std::norm(spectrum_[bin]);
        power_[bin] = hasNoiseEstimate_ ? 0.7f * power_[bin] + 0.3f * power : power;
        noise_[bin] = hasNoiseEstimate_ ? std::min(power_[bin], noise_[bin] * rise) : power_[bin];
        float wanted = power_[bin] > 0 ? std::max(1.0f - oversubtract * noise_[bin] / power_[bin], 0.0f) : 1.0f;
        wanted = std::max(wanted, floorGain);
        gain_[bin] = 0.5f * gain_[bin] + 0.5f * wanted;  // smooth in time: no musical noise
        spectrum_[bin] *= gain_[bin];
        if (bin > 0 && bin < kFrame / 2) {
            spectrum_[kFrame - bin] = std::conj(spectrum_[bin]);
        }
    }
    hasNoiseEstimate_ = true;

    fft_.inverse(spectrum_.data());
    for (std::size_t i = 0; i < kHop; ++i) {
        output_[i] = overlap_[i] + spectrum_[i].real() * window_[i];
        overlap_[i] = spectrum_[i + kHop].real() * window_[i + kHop];
    }
}

void VoiceChain::process(float* samples, std::size_t frames, std::size_t channels) {
    if (!params_.enabled || channels == 0) {
        return;  // untouched buffer: bit-exact bypass
    }
    const double gate = unit(params_.gate), deEss = unit(params_.deEss);
    const double compress = unit(params_.compress), leveler = unit(params_.leveler);
    const double trim = dbToGain(std::clamp(static_cast<double>(params_.trimDb), -24.0, 24.0));
    const double gateThresholdDb = -62.0 + 24.0 * gate;

    for (std::size_t frame = 0; frame < frames; ++frame) {
        float* in = samples + frame * channels;
        double x = in[0];

        if (params_.highPass) {
            x = highPassState_.process(highPass_, x);
        }
        if (params_.denoise > 0) {
            x = denoiseSample(x);
        }
        if (gate > 0) {
            // 2:1 downward expansion below the threshold, at most -24 dB, smoothed so it never chatters.
            double levelDb = gainToDb(gateEnvelope_.process(x));
            double wanted = dbToGain(std::clamp(levelDb - gateThresholdDb, -24.0, 0.0));
            gateGain_ += gateSmooth_ * (wanted - gateGain_);
            x *= gateGain_;
        }
        if (deEss > 0) {
            double low = essLow_.process(x), high = x - low;
            double levelDb = gainToDb(essEnvelope_.process(high));
            x = low + high * (1.0 - 0.7 * deEss * std::clamp((levelDb + 28.0) / 12.0, 0.0, 1.0));
        }
        if (compress > 0) {
            x *= compressor_.gainFor(x, x, -22.0, 1.0 + 3.0 * compress, 6.0 * compress);
        }
        for (std::size_t i = 0; i < params_.bandCount; ++i) {
            x = bandState_[i].process(bands_[i], x);
        }
        if (leveler > 0) {
            // Slow ride towards -20 dBFS while someone is speaking; frozen in silence so noise is not pumped up.
            double levelDb = gainToDb(levelEnvelope_.process(x));
            if (levelDb > -50.0) {
                double wantedDb = std::clamp(-20.0 - levelDb, -12.0 * leveler, 12.0 * leveler);
                levelGainDb_ += std::clamp(wantedDb - levelGainDb_, -levelStep_, levelStep_);
            }
            x *= dbToGain(levelGainDb_);
        }
        x *= trim;
        if (params_.limiterEnabled) {
            double other = x;
            limiter_.process(x, other);
        }

        for (std::size_t channel = 0; channel < channels; ++channel) {
            in[channel] = static_cast<float>(x);
        }
    }
    if (std::abs(highPassState_.z1) < 1e-30) highPassState_.z1 = 0;
    if (std::abs(highPassState_.z2) < 1e-30) highPassState_.z2 = 0;
}

}  // namespace ufo
