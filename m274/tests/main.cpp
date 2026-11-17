// Numeric checks for the M274 DSP. Any failure prints the reason and exits non-zero.
#include <chrono>
#include <cmath>
#include <complex>
#include <cstdio>
#include <cstdlib>
#include <numbers>
#include <vector>

#include "biquad.h"
#include "engine.h"
#include "limiter.h"
#include "voice.h"

using namespace m274;

static void expect(bool condition, const char* what) {
    if (!condition) {
        std::printf("FAILED: %s\n", what);
        std::exit(1);
    }
}

// Analog peaking prototype: (s^2 + s*A/Q + 1) / (s^2 + s/(A*Q) + 1).
static double analogPeakDb(double f0, double gainDb, double q, double f) {
    double a = std::pow(10.0, gainDb / 40.0);
    std::complex<double> s(0, f / f0);
    return 20 * std::log10(std::abs((s * s + s * a / q + 1.0) / (s * s + s / (a * q) + 1.0)));
}

static double worstError(double f0, double gainDb, double q) {
    BiquadCoeffs c = matchedPeak(48000, f0, gainDb, q);
    double worst = 0;
    for (double f = 20; f <= 20000; f *= 1.01) {
        worst = std::max(worst, std::abs(magnitudeDb(c, 48000, f) - analogPeakDb(f0, gainDb, q, f)));
    }
    return worst;
}

int main() {
    // Matched peaking filters follow the analog curve up to 20 kHz, boosts and cuts alike.
    expect(worstError(1000, 6, 1.41) < 0.01, "1 kHz boost matches analog");
    expect(worstError(16000, 12, 1.41) < 0.4, "16 kHz +12 dB stays within 0.4 dB");
    expect(worstError(16000, -12, 1.41) < 0.4, "16 kHz -12 dB stays within 0.4 dB");
    expect(std::abs(magnitudeDb(matchedPeak(48000, 8000, 9, 2), 48000, 8000) - 9) < 0.001, "gain at center is exact");
    {
        BiquadCoeffs up = matchedPeak(48000, 12000, 7, 1), down = matchedPeak(48000, 12000, -7, 1);
        for (double f = 20; f <= 20000; f *= 1.2) {
            expect(std::abs(magnitudeDb(up, 48000, f) + magnitudeDb(down, 48000, f)) < 1e-9, "boost and cut cancel");
        }
    }

    // Engine: bypass leaves the buffer bit-exact.
    {
        Engine engine;
        engine.prepare(48000);
        EngineParams params;
        params.enabled = 0;
        engine.setParams(params);
        std::vector<float> buffer(960), original;
        for (std::size_t i = 0; i < buffer.size(); ++i) buffer[i] = std::sin(i * 0.1f) * 0.5f;
        original = buffer;
        engine.process(buffer.data(), 480, 2);
        expect(buffer == original, "bypass is bit-exact");
    }

    // Engine: headroom follows the real peak of overlapping bands (two +6 dB bands stack).
    {
        Engine engine;
        engine.prepare(48000);
        EngineParams params;
        params.bandCount = 2;
        params.bands[0] = {BandType::Peak, 1000, 6, 1.0f};
        params.bands[1] = {BandType::Peak, 1100, 6, 1.0f};
        engine.setParams(params);
        expect(engine.headroomDb() < -10.5 && engine.headroomDb() > -12.1, "headroom covers stacked boosts");
        params.bandCount = 1;
        params.bands[0] = {BandType::Peak, 1000, -8, 1.0f};
        engine.setParams(params);
        expect(engine.headroomDb() == 0, "cuts need no headroom");
    }

    // Engine: a sine at a boosted band comes out at the predicted level (EQ + headroom).
    {
        Engine engine;
        engine.prepare(48000);
        EngineParams params;
        params.bandCount = 1;
        params.limiterEnabled = 0;
        params.bands[0] = {BandType::Peak, 100, 6, 1.41f};
        engine.setParams(params);
        std::vector<float> buffer(48000 * 2);
        for (std::size_t i = 0; i < 48000; ++i) {
            buffer[2 * i] = buffer[2 * i + 1] = 0.5f * static_cast<float>(std::sin(2 * std::numbers::pi * 4000 * i / 48000.0));
        }
        engine.process(buffer.data(), 48000, 2);
        float peak = 0;
        for (std::size_t i = 24000; i < 48000; ++i) peak = std::max(peak, std::abs(buffer[2 * i]));
        double wantDb = engine.responseDb(4000) + engine.headroomDb();
        expect(std::abs(20 * std::log10(peak / 0.5) - wantDb) < 0.05, "measured level matches predicted curve");
    }

    // Limiter: worst-case input (full-scale square with inter-sample overs, 4x too hot) never
    // leaves above the ceiling, including between samples; quiet input is left alone.
    {
        Limiter limiter;
        limiter.prepare(48000, -0.3);
        double ceiling = std::pow(10.0, -0.3 / 20.0), worst = 0, previous = 0;
        for (int i = 0; i < 48000; ++i) {
            double phase = 2 * std::numbers::pi * 11025.0 * i / 48000.0 + 0.785;  // peaks fall between samples
            double x = 4.0 * std::sin(phase) * (i % 3000 < 1500 ? 1.0 : 0.05);
            double l = x, r = -x;
            limiter.process(l, r);
            worst = std::max({worst, std::abs(l), std::abs(r), std::abs(0.5 * (l + previous))});
            previous = l;
        }
        expect(worst <= ceiling + 1e-9, "limiter holds the ceiling");
        expect(limiter.takeMinGain() < 0.3, "limiter reported its gain reduction");

        limiter.reset();
        double l = 0.25, r = 0.25;
        for (int i = 0; i < 200; ++i) { l = 0.25; r = 0.25; limiter.process(l, r); }
        expect(std::abs(l - 0.25) < 1e-12, "quiet signal passes untouched");
    }

    // Cost: 10 bands + limiter on one second of stereo audio must be far below real time.
    {
        Engine engine;
        engine.prepare(48000);
        EngineParams params;
        params.bandCount = 10;
        for (int i = 0; i < 10; ++i) params.bands[i] = {BandType::Peak, 31.0f * static_cast<float>(1 << i), 3, 1.41f};
        engine.setParams(params);
        std::vector<float> buffer(480 * 2, 0.1f);
        auto start = std::chrono::steady_clock::now();
        for (int block = 0; block < 100; ++block) engine.process(buffer.data(), 480, 2);
        double seconds = std::chrono::duration<double>(std::chrono::steady_clock::now() - start).count();
        std::printf("cpu: %.2f%% of one core\n", seconds * 100);
        expect(seconds < 0.05, "engine uses under 5% of a core");
    }

    // Helpers for the feature checks: steady-state RMS of what a chain does to a signal.
    auto rmsDb = [](const std::vector<float>& buffer, std::size_t channels, std::size_t fromFrame) {
        double sum = 0;
        std::size_t count = 0;
        for (std::size_t i = fromFrame * channels; i < buffer.size(); i += channels) {
            sum += static_cast<double>(buffer[i]) * buffer[i];
            ++count;
        }
        return 10 * std::log10(sum / static_cast<double>(count) + 1e-20);
    };
    auto stereoSine = [](double frequency, double amplitude, std::size_t frames, double rate = 48000) {
        std::vector<float> buffer(frames * 2);
        for (std::size_t i = 0; i < frames; ++i) {
            buffer[2 * i] = buffer[2 * i + 1] = static_cast<float>(amplitude * std::sin(2 * std::numbers::pi * frequency * i / rate));
        }
        return buffer;
    };
    auto runEngine = [](const EngineParams& params, std::vector<float> buffer) {
        Engine engine;
        engine.prepare(48000);
        engine.setParams(params);
        engine.process(buffer.data(), buffer.size() / 2, 2);
        return buffer;
    };

    // Extras off: the chain is transparent apart from the limiter's delay.
    {
        EngineParams params;
        auto out = runEngine(params, stereoSine(1000, 0.25, 48000));
        expect(std::abs(rmsDb(out, 2, 24000) - rmsDb(stereoSine(1000, 0.25, 48000), 2, 24000)) < 0.01, "flat chain is transparent");
    }

    // Night mode: quiet material comes up, loud material comes down (less dynamic range).
    {
        EngineParams params;
        params.night = 1;
        double quiet = rmsDb(runEngine(params, stereoSine(1000, 0.003, 48000)), 2, 24000) - rmsDb(stereoSine(1000, 0.003, 48000), 2, 24000);
        double loud = rmsDb(runEngine(params, stereoSine(1000, 0.9, 48000)), 2, 24000) - rmsDb(stereoSine(1000, 0.9, 48000), 2, 24000);
        std::printf("night: quiet %+.1f dB, loud %+.1f dB\n", quiet, loud);
        expect(quiet > 6 && loud < -6, "night mode narrows the dynamic range");
    }

    // Footsteps: quiet detail at 2.8 kHz is lifted, the same band when loud is not, bass is never touched.
    {
        EngineParams params;
        params.footsteps = 1;
        params.limiterEnabled = 0;
        auto gainAt = [&](double frequency, double amplitude) {
            return rmsDb(runEngine(params, stereoSine(frequency, amplitude, 48000)), 2, 24000) - rmsDb(stereoSine(frequency, amplitude, 48000), 2, 24000);
        };
        double quiet = gainAt(2800, 0.001), loud = gainAt(2800, 0.5), bass = gainAt(100, 0.001);
        std::printf("footsteps: quiet %+.1f dB, loud %+.1f dB, bass %+.1f dB\n", quiet, loud, bass);
        expect(quiet > 6 && std::abs(loud) < 0.5 && std::abs(bass) < 0.5, "footsteps lifts only quiet upper-mid detail");
    }

    // Bass protection: only bass boosted past full scale is eased back. Bass at mastered level
    // (just under full scale) must pass untouched, or every track sounds thin.
    {
        EngineParams params;
        params.bassProtect = 1;
        params.limiterEnabled = 0;
        auto gainAt = [&](double frequency, double amplitude) {
            return rmsDb(runEngine(params, stereoSine(frequency, amplitude, 48000)), 2, 24000) - rmsDb(stereoSine(frequency, amplitude, 48000), 2, 24000);
        };
        double boosted = gainAt(50, 2.5), mastered = gainAt(50, 0.95), mids = gainAt(2000, 0.9);
        std::printf("bass protect: boosted %+.1f dB, mastered %+.1f dB, mids %+.1f dB\n", boosted, mastered, mids);
        expect(boosted < -3 && std::abs(mastered) < 0.3 && std::abs(mids) < 0.3, "bass protection acts only above full scale");

        // The default chain (flat curve, protection and limiter on) must be transparent on full-level music.
        EngineParams defaults;
        defaults.bassProtect = 1;
        double bass = rmsDb(runEngine(defaults, stereoSine(60, 0.95, 48000)), 2, 24000) - rmsDb(stereoSine(60, 0.95, 48000), 2, 24000);
        double voice = rmsDb(runEngine(defaults, stereoSine(1000, 0.95, 48000)), 2, 24000) - rmsDb(stereoSine(1000, 0.95, 48000), 2, 24000);
        std::printf("default chain: 60 Hz %+.2f dB, 1 kHz %+.2f dB\n", bass, voice);
        expect(std::abs(bass) < 0.3 && std::abs(voice) < 0.3, "the default chain does not change full-level music");
    }

    // Crossfeed: a hard-left signal leaks to the right; a mono signal keeps its level within 1 dB.
    {
        EngineParams params;
        params.crossfeed = 1;
        params.limiterEnabled = 0;
        auto left = stereoSine(300, 0.25, 48000);
        for (std::size_t i = 1; i < left.size(); i += 2) left[i] = 0;
        auto out = runEngine(params, left);
        std::vector<float> rightOnly(out.size());
        for (std::size_t i = 0; i < out.size() / 2; ++i) rightOnly[2 * i] = out[2 * i + 1];
        expect(rmsDb(rightOnly, 2, 24000) > rmsDb(out, 2, 24000) - 14, "crossfeed leaks into the other channel");
        double mono = rmsDb(runEngine(params, stereoSine(300, 0.25, 48000)), 2, 24000) - rmsDb(stereoSine(300, 0.25, 48000), 2, 24000);
        expect(std::abs(mono) < 1.0, "crossfeed keeps mono level");
    }

    // Bass harmonics: a 60 Hz tone gains energy at 120 Hz that was not in the input.
    {
        EngineParams params;
        params.bassEnhance = 1;
        params.limiterEnabled = 0;
        auto out = runEngine(params, stereoSine(60, 0.5, 48000));
        double re = 0, im = 0;
        for (std::size_t i = 24000; i < 48000; ++i) {
            re += out[2 * i] * std::cos(2 * std::numbers::pi * 120 * i / 48000.0);
            im += out[2 * i] * std::sin(2 * std::numbers::pi * 120 * i / 48000.0);
        }
        double harmonicDb = 20 * std::log10(2 * std::hypot(re, im) / 24000 + 1e-12);
        std::printf("bass harmonics: 120 Hz at %.1f dBFS\n", harmonicDb);
        expect(harmonicDb > -40, "bass enhancer adds the second harmonic");
    }

    // Analyzer: a full-scale-ish 1 kHz sine shows up in the right band at the right level.
    {
        Engine engine;
        engine.prepare(48000);
        EngineParams params;
        params.limiterEnabled = 0;
        engine.setParams(params);
        auto buffer = stereoSine(1000, 0.5, 4096);
        engine.process(buffer.data(), 4096, 2);
        std::size_t strongest = 0;
        for (std::size_t band = 1; band < Engine::kSpectrumBands; ++band) {
            if (engine.spectrum()[band] > engine.spectrum()[strongest]) strongest = band;
        }
        double centre = 20.0 * std::pow(1000.0, (strongest + 0.5) / Engine::kSpectrumBands);
        expect(centre > 800 && centre < 1250, "analyzer finds the tone's band");
        expect(std::abs(engine.spectrum()[strongest] - 20 * std::log10(0.5)) < 1.5, "analyzer reads the tone's level");
    }

    // Voice chain, at the wireless microphone's 16 kHz and the wired one's 48 kHz.
    for (double rate : {16000.0, 48000.0}) {
        auto frames = static_cast<std::size_t>(rate * 4);
        // Deterministic noise (LCG) plus a 440 Hz "voice" that starts after one second.
        std::vector<float> noiseOnly(frames), mixed(frames);
        std::uint32_t seed = 12345;
        for (std::size_t i = 0; i < frames; ++i) {
            seed = seed * 1664525u + 1013904223u;
            float noise = (static_cast<float>(seed >> 8) / 8388608.0f - 1.0f) * 0.02f;
            noiseOnly[i] = noise;
            mixed[i] = noise + (i > frames / 4 ? 0.2f * static_cast<float>(std::sin(2 * std::numbers::pi * 440 * i / rate)) : 0.0f);
        }
        auto runVoice = [&](const VoiceParams& params, std::vector<float> buffer) {
            VoiceChain voice;
            voice.prepare(rate);
            voice.setParams(params);
            voice.process(buffer.data(), buffer.size(), 1);
            return buffer;
        };
        auto tail = static_cast<std::size_t>(rate * 3);

        VoiceParams off;
        off.enabled = 0;
        expect(runVoice(off, mixed) == mixed, "voice bypass is bit-exact");

        VoiceParams denoise;
        denoise.highPass = 0;
        denoise.limiterEnabled = 0;
        denoise.denoise = 1;
        double noiseCut = rmsDb(runVoice(denoise, noiseOnly), 1, tail) - rmsDb(noiseOnly, 1, tail);
        double voiceKept = rmsDb(runVoice(denoise, mixed), 1, tail) - rmsDb(mixed, 1, tail);
        std::printf("voice %.0f Hz: noise %+.1f dB, speech %+.1f dB\n", rate, noiseCut, voiceKept);
        expect(noiseCut < -10, "noise reduction removes steady noise");
        expect(std::abs(voiceKept) < 1.5, "noise reduction keeps the voice level");

        VoiceParams gate;
        gate.highPass = 0;
        gate.limiterEnabled = 0;
        gate.gate = 1;
        std::vector<float> roomNoise = noiseOnly;  // a quiet room: about -48 dBFS peak, below the threshold
        for (float& sample : roomNoise) sample *= 0.2f;
        expect(rmsDb(runVoice(gate, roomNoise), 1, tail) - rmsDb(roomNoise, 1, tail) < -8, "expander pushes the background down");
        expect(std::abs(rmsDb(runVoice(gate, mixed), 1, tail) - rmsDb(mixed, 1, tail)) < 1.0, "expander leaves speech alone");

        VoiceParams all;
        all.denoise = all.gate = all.deEss = all.compress = all.leveler = 1;
        all.bandCount = 1;
        all.bands[0] = {BandType::Peak, 3000, 4, 1.0f};
        auto out = runVoice(all, mixed);
        float peak = 0;
        bool isFinite = true;
        for (float sample : out) {
            peak = std::max(peak, std::abs(sample));
            isFinite = isFinite && std::isfinite(sample);
        }
        expect(isFinite && peak <= std::pow(10.0, -1.0 / 20.0) + 1e-6, "full voice chain stays finite and under its ceiling");
    }

    std::printf("DSP checks passed.\n");
    return 0;
}
