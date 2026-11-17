#include "biquad.h"

#include <cmath>
#include <complex>
#include <numbers>

namespace m274 {

namespace {

constexpr double kPi = std::numbers::pi;

BiquadCoeffs matchedBoost(double sampleRate, double frequency, double gainDb, double q) {
    double gain = std::pow(10.0, gainDb / 20.0);
    double amplitude = std::sqrt(gain);
    double w0 = 2 * kPi * frequency / sampleRate;
    double damping = 1 / (2 * amplitude * q);  // analog denominator: s^2 + s/(A*Q) + 1

    // Poles by impulse invariance.
    BiquadCoeffs c;
    double decay = std::exp(-damping * w0);
    c.a1 = damping <= 1 ? -2 * decay * std::cos(std::sqrt(1 - damping * damping) * w0)
                        : -2 * decay * std::cosh(std::sqrt(damping * damping - 1) * w0);
    c.a2 = decay * decay;

    // Zeros chosen so the magnitude matches the analog one at DC, at w0 and at Nyquist.
    double A0 = (1 + c.a1 + c.a2) * (1 + c.a1 + c.a2);
    double A1 = (1 - c.a1 + c.a2) * (1 - c.a1 + c.a2);
    double A2 = -4 * c.a2;
    double phi1 = std::sin(w0 / 2) * std::sin(w0 / 2);
    double phi0 = 1 - phi1;
    double phi2 = 4 * phi0 * phi1;
    double R1 = (A0 * phi0 + A1 * phi1 + A2 * phi2) * gain * gain;
    double R2 = (-A0 + A1 + 4 * (phi0 - phi1) * A2) * gain * gain;
    double B0 = A0;
    double B2 = (R1 - R2 * phi1 - B0) / (4 * phi1 * phi1);
    double B1 = R2 + B0 + 4 * (phi1 - phi0) * B2;
    double W = 0.5 * (std::sqrt(B0) + std::sqrt(B1));
    c.b0 = 0.5 * (W + std::sqrt(W * W + B2));
    c.b1 = 0.5 * (std::sqrt(B0) - std::sqrt(B1));
    c.b2 = -B2 / (4 * c.b0);
    return c;
}

BiquadCoeffs normalized(double b0, double b1, double b2, double a0, double a1, double a2) {
    return {b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0};
}

}  // namespace

BiquadCoeffs matchedPeak(double sampleRate, double frequency, double gainDb, double q) {
    if (gainDb >= 0) {
        return matchedBoost(sampleRate, frequency, gainDb, q);
    }
    // A cut is the exact inverse of the boost of the same size (swap numerator and denominator),
    // which keeps cuts as accurate as boosts and makes +x dB followed by -x dB cancel.
    BiquadCoeffs boost = matchedBoost(sampleRate, frequency, -gainDb, q);
    return normalized(1, boost.a1, boost.a2, boost.b0, boost.b1, boost.b2);
}

BiquadCoeffs lowShelf(double sampleRate, double frequency, double gainDb, double q) {
    double A = std::pow(10.0, gainDb / 40.0);
    double w0 = 2 * kPi * frequency / sampleRate, cosw = std::cos(w0);
    double beta = 2 * std::sqrt(A) * std::sin(w0) / (2 * q);
    return normalized(A * ((A + 1) - (A - 1) * cosw + beta), 2 * A * ((A - 1) - (A + 1) * cosw),
                      A * ((A + 1) - (A - 1) * cosw - beta), (A + 1) + (A - 1) * cosw + beta,
                      -2 * ((A - 1) + (A + 1) * cosw), (A + 1) + (A - 1) * cosw - beta);
}

BiquadCoeffs highShelf(double sampleRate, double frequency, double gainDb, double q) {
    double A = std::pow(10.0, gainDb / 40.0);
    double w0 = 2 * kPi * frequency / sampleRate, cosw = std::cos(w0);
    double beta = 2 * std::sqrt(A) * std::sin(w0) / (2 * q);
    return normalized(A * ((A + 1) + (A - 1) * cosw + beta), -2 * A * ((A - 1) + (A + 1) * cosw),
                      A * ((A + 1) + (A - 1) * cosw - beta), (A + 1) - (A - 1) * cosw + beta,
                      2 * ((A - 1) - (A + 1) * cosw), (A + 1) - (A - 1) * cosw - beta);
}

BiquadCoeffs highPass(double sampleRate, double frequency, double q) {
    double w0 = 2 * kPi * frequency / sampleRate, cosw = std::cos(w0);
    double alpha = std::sin(w0) / (2 * q);
    return normalized((1 + cosw) / 2, -(1 + cosw), (1 + cosw) / 2, 1 + alpha, -2 * cosw, 1 - alpha);
}

double magnitudeDb(const BiquadCoeffs& c, double sampleRate, double frequency) {
    std::complex<double> z = std::polar(1.0, -2 * kPi * frequency / sampleRate);
    std::complex<double> h = (c.b0 + c.b1 * z + c.b2 * z * z) / (1.0 + c.a1 * z + c.a2 * z * z);
    return 20 * std::log10(std::abs(h));
}

}  // namespace m274
