using System.Numerics;

namespace Ufo.Audio;

/// <summary>Filter shapes the engine knows (values match BandType in engine/dsp/engine.h).</summary>
public enum BandKind
{
    Peak = 0,
    LowShelf = 1,
    HighShelf = 2,
    HighPass = 3,
}

/// <summary>One filter. Gain in dB (ignored by HighPass); Q is the bandwidth (1.41 ≈ one octave).</summary>
public sealed record EqBand(double Frequency, double Gain, double Q = Equalizer.OctaveQ, BandKind Kind = BandKind.Peak);

public sealed record EqualizerSettings(bool IsEnabled, string PresetName, IReadOnlyList<EqBand> Bands);

public static class Equalizer
{
    public const double OctaveQ = 1.41;
    public const double MaxGain = 12;
    public const int MaxUserBands = 16;

    public static readonly double[] GraphicFrequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public const string CustomPresetName = "Personalizado";

    /// <summary>Graphic presets, one gain per <see cref="GraphicFrequencies"/> entry.</summary>
    public static readonly IReadOnlyDictionary<string, double[]> Presets = new Dictionary<string, double[]>
    {
        ["Plano"] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        ["Graves"] = [6, 5, 4, 2, 0, 0, 0, 0, 1, 1],
        ["FPS"] = [-3, -2, -1, 0, 1, 3, 5, 6, 4, 2],
        ["Voz"] = [-4, -3, -1, 1, 3, 4, 4, 2, 0, -2],
        ["Música"] = [4, 3, 1, 0, -1, 0, 1, 2, 3, 3],
        ["Cinema"] = [5, 4, 2, 0, 0, 1, 2, 2, 3, 2],
    };

    public static EqualizerSettings Default => FromPreset("Plano");

    public static EqualizerSettings FromPreset(string presetName) =>
        new(true, presetName, Presets[presetName].Select((gain, index) => new EqBand(GraphicFrequencies[index], gain)).ToList());

    /// <summary>True when the bands are exactly the ten graphic ones (the simple sliders can show them).</summary>
    public static bool IsGraphic(IReadOnlyList<EqBand> bands) =>
        bands.Count == GraphicFrequencies.Length &&
        bands.Select((band, index) => band.Kind == BandKind.Peak && band.Frequency == GraphicFrequencies[index] && band.Q == OctaveQ).All(isMatch => isMatch);

    /// <summary>
    /// Headroom (≤ 0 dB) that keeps the curve from boosting above full scale: the real peak of the
    /// combined curve, like the engine computes it (engine/dsp/engine.cpp), so stacked bands count.
    /// </summary>
    public static double Preamp(IReadOnlyList<EqBand> bands)
    {
        double peak = 0;
        for (int step = 0; step <= 240; step++)
        {
            peak = Math.Max(peak, CurveDb(bands, 20 * Math.Pow(2, step / 24.0)));
        }
        foreach (EqBand band in bands)
        {
            peak = Math.Max(peak, CurveDb(bands, band.Frequency)); // the grid can step past a band's exact centre
        }
        return -peak;
    }

    /// <summary>Total response in dB at a frequency, headroom included: what the engine outputs.</summary>
    public static double ResponseDb(IReadOnlyList<EqBand> bands, double frequency) => Preamp(bands) + CurveDb(bands, frequency);

    /// <summary>
    /// Sum of the bands' analog responses, headroom excluded. The engine's filters follow this
    /// curve to within about 0.35 dB up to 20 kHz, so the graph shows what is actually heard.
    /// </summary>
    public static double CurveDb(IReadOnlyList<EqBand> bands, double frequency)
    {
        double total = 0;
        foreach (EqBand band in bands)
        {
            double a = Math.Pow(10, band.Gain / 40), root = Math.Sqrt(a);
            var s = new Complex(0, frequency / band.Frequency);
            Complex response = band.Kind switch
            {
                BandKind.LowShelf => a * (s * s + root / band.Q * s + a) / (a * s * s + root / band.Q * s + 1),
                BandKind.HighShelf => a * (a * s * s + root / band.Q * s + 1) / (s * s + root / band.Q * s + a),
                BandKind.HighPass => s * s / (s * s + s / band.Q + 1),
                _ => (s * s + s * a / band.Q + 1) / (s * s + s / (a * band.Q) + 1),
            };
            total += 20 * Math.Log10(Math.Max(response.Magnitude, 1e-6));
        }
        return total;
    }
}

/// <summary>Processing beyond the equalizer. Amounts run from 0 (off) to 1.</summary>
public sealed record SoundSettings(
    double Crossfeed = 0,
    double Night = 0,
    double Footsteps = 0,
    double BassEnhance = 0,
    bool IsBassProtected = true,
    bool IsLoudnessOn = false,
    bool IsLimiterOn = true,
    double TiltDb = 0,
    double BassDb = 0,
    double TrimDb = 0,
    IReadOnlyList<EqBand>? Correction = null)
{
    /// <summary>
    /// Bands the app adds on top of the user's equalizer: the headphone correction from the ear
    /// calibration, the tone controls, and loudness compensation for the current Windows volume
    /// (the quieter the volume, the more bass and a little treble, after the equal-loudness curves).
    /// </summary>
    public IEnumerable<EqBand> ExtraBands(double volumeDb)
    {
        foreach (EqBand band in Correction ?? [])
        {
            yield return band;
        }
        if (BassDb != 0)
        {
            yield return new EqBand(105, BassDb, 0.71, BandKind.LowShelf);
        }
        if (TiltDb != 0)
        {
            // A tilt is a low shelf one way and a high shelf the other, pivoting around 1 kHz.
            yield return new EqBand(300, -TiltDb / 2, 0.5, BandKind.LowShelf);
            yield return new EqBand(3000, TiltDb / 2, 0.5, BandKind.HighShelf);
        }
        if (IsLoudnessOn && volumeDb < -3)
        {
            // ponytail: a gentle linear stand-in for ISO 226 (+1 dB of bass per 10 dB of attenuation,
            // 4 dB at most). The Windows volume is not the listening level (a loud headset is kept
            // low), so the full contour piled +11 dB of bass on music that already had plenty.
            yield return new EqBand(120, Math.Min(4, -volumeDb * 0.1), 0.71, BandKind.LowShelf);
            yield return new EqBand(9000, Math.Min(1.5, -volumeDb * 0.04), 0.71, BandKind.HighShelf);
        }
    }
}

/// <summary>Microphone chain. Amounts run from 0 (off) to 1; tone controls are in dB.</summary>
public sealed record VoiceSettings(
    bool IsEnabled = false,
    bool IsHighPassOn = true,
    double Denoise = 0,
    double Gate = 0,
    double DeEss = 0,
    double Compress = 0,
    double Leveler = 0,
    double BodyDb = 0,
    double PresenceDb = 0,
    double AirDb = 0,
    double TrimDb = 0)
{
    public IEnumerable<EqBand> Bands()
    {
        if (BodyDb != 0) yield return new EqBand(180, BodyDb, 0.71, BandKind.LowShelf);
        if (PresenceDb != 0) yield return new EqBand(3000, PresenceDb, 1.0);
        if (AirDb != 0) yield return new EqBand(6500, AirDb, 0.71, BandKind.HighShelf);
    }
}
