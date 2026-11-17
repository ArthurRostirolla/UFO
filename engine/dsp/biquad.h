#pragma once

namespace ufo {

/// Normalized biquad coefficients (a0 = 1).
struct BiquadCoeffs {
    double b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;
};

/// Peaking filter whose response matches the analog prototype up to Nyquist (Vicanek, "Matched
/// Second Order Digital Filters", 2016). The usual RBJ design narrows and loses gain near Nyquist:
/// at 48 kHz a +12 dB band at 16 kHz is off by ~6 dB; this one stays within ~0.35 dB.
BiquadCoeffs matchedPeak(double sampleRate, double frequency, double gainDb, double q);

// ponytail: shelves and high-pass use the RBJ design; they sit far from Nyquist in practice
// (shelves below ~10 kHz, high-pass below 200 Hz). Switch to matched designs if that changes.
BiquadCoeffs lowShelf(double sampleRate, double frequency, double gainDb, double q);
BiquadCoeffs highShelf(double sampleRate, double frequency, double gainDb, double q);
BiquadCoeffs highPass(double sampleRate, double frequency, double q);

/// Magnitude of the filter at a frequency, in dB.
double magnitudeDb(const BiquadCoeffs& coeffs, double sampleRate, double frequency);

/// One channel of filter state (transposed direct form II, double precision).
struct BiquadState {
    double z1 = 0, z2 = 0;

    double process(const BiquadCoeffs& c, double x) {
        double y = c.b0 * x + z1;
        z1 = c.b1 * x - c.a1 * y + z2;
        z2 = c.b2 * x - c.a2 * y;
        return y;
    }
};

}  // namespace ufo
