#pragma once

#include <array>
#include <cmath>
#include <complex>
#include <cstddef>
#include <cstdint>
#include <numbers>
#include <utility>

namespace ufo {

/// In-place radix-2 FFT for sizes up to 1024, tables built once, no allocation.
class Fft {
public:
    static constexpr std::size_t kMaxSize = 1024;

    void prepare(std::size_t size) {
        size_ = size;
        for (std::size_t i = 0; i < size / 2; ++i) {
            twiddle_[i] = std::polar(1.0f, static_cast<float>(-2 * std::numbers::pi * i / size));
        }
        std::size_t bits = 0;
        while ((std::size_t{1} << bits) < size) ++bits;
        for (std::size_t i = 0; i < size; ++i) {
            std::size_t reversed = 0;
            for (std::size_t bit = 0; bit < bits; ++bit) {
                if (i & (std::size_t{1} << bit)) reversed |= std::size_t{1} << (bits - 1 - bit);
            }
            reverse_[i] = static_cast<std::uint16_t>(reversed);
        }
    }

    void forward(std::complex<float>* data) const { transform(data, false); }

    /// Inverse transform, already divided by the size.
    void inverse(std::complex<float>* data) const {
        transform(data, true);
        for (std::size_t i = 0; i < size_; ++i) data[i] /= static_cast<float>(size_);
    }

    std::size_t size() const { return size_; }

private:
    void transform(std::complex<float>* data, bool isInverse) const {
        for (std::size_t i = 0; i < size_; ++i) {
            if (i < reverse_[i]) std::swap(data[i], data[reverse_[i]]);
        }
        for (std::size_t span = 2; span <= size_; span *= 2) {
            std::size_t step = size_ / span;
            for (std::size_t start = 0; start < size_; start += span) {
                for (std::size_t k = 0; k < span / 2; ++k) {
                    std::complex<float> w = twiddle_[k * step];
                    if (isInverse) w = std::conj(w);
                    std::complex<float> odd = data[start + k + span / 2] * w;
                    data[start + k + span / 2] = data[start + k] - odd;
                    data[start + k] += odd;
                }
            }
        }
    }

    std::size_t size_ = 0;
    std::array<std::complex<float>, kMaxSize / 2> twiddle_{};
    std::array<std::uint16_t, kMaxSize> reverse_{};
};

}  // namespace ufo
