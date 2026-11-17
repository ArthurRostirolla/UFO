namespace Ufo.Audio;

/// <summary>What the analysis proposes: a few small bands, a profile volume and a night-mode amount.</summary>
public sealed record PlaylistProposal(IReadOnlyList<EqBand> Bands, double TrimDb, double Night, string Summary);

/// <summary>
/// Listens to what is playing (the engine's spectrum and peak meters, read while the engine plays
/// flat) and proposes a few small moves that serve the music's own character instead of flattening
/// it: bass-heavy material keeps its bass and gains clarity around it, thin material gains a little
/// warmth, dull or harsh treble is nudged. Every move is 2.5 dB or less.
/// Not thread-safe: feed it from one thread.
/// </summary>
public sealed class PlaylistAnalysis
{
    public const double MinimumSeconds = 30;
    private const double SilenceDb = -70;

    // ponytail: thresholds for the engine's "strongest bin per band" reading, where a bass note
    // (one bin) reads far above mids spread over many bins. Set from one bass-heavy measurement
    // (+25 dB bass over mids, -24 dB treble under mids) and the reference tilt below; tune with more
    // playlists if the classification turns out wrong.
    private const double HeavyBassDb = 18, LightBassDb = 6, DullTrebleDb = -30, BrightTrebleDb = -14;

    private readonly double[] power = new double[EngineClient.SpectrumBands];
    private readonly List<double> levels = [];
    private int frames;

    /// <summary>Seconds of non-silent audio heard so far, given the sampling interval used.</summary>
    public double SecondsHeard(double frameSeconds) => frames * frameSeconds;

    /// <summary>Adds one reading. Silence (pauses, gaps between tracks) is ignored.</summary>
    public void Add(float[] spectrumDb, double peak)
    {
        if (spectrumDb.Length != power.Length || spectrumDb.Max() < SilenceDb)
        {
            return;
        }
        for (int band = 0; band < power.Length; band++)
        {
            power[band] += Math.Pow(10, spectrumDb[band] / 10);
        }
        levels.Add(20 * Math.Log10(Math.Max(peak, 1e-5)));
        frames++;
    }

    public PlaylistProposal Propose()
    {
        if (frames == 0)
        {
            return new PlaylistProposal([], 0, 0, "nada foi ouvido");
        }
        double mids = Level(200, 2000);
        double bass = Level(40, 160) - mids, treble = Level(6000, 14000) - mids;

        var bands = new List<EqBand>();
        var notes = new List<string>();
        if (bass > HeavyBassDb)
        {
            // The bass is the point of this music: leave it alone and clear the space around it.
            bands.Add(new EqBand(300, -2, 1.0));
            bands.Add(new EqBand(3200, 1.5, 1.0));
            notes.Add("graves fortes mantidos, menos embolado em 300 Hz, mais presença em 3 kHz");
        }
        else if (bass < LightBassDb)
        {
            bands.Add(new EqBand(100, 2.5, 0.71, BandKind.LowShelf));
            notes.Add("pouco grave: +2,5 dB de corpo");
        }
        if (treble < DullTrebleDb)
        {
            bands.Add(new EqBand(8000, 2, 0.71, BandKind.HighShelf));
            notes.Add("agudos apagados: +2 dB de brilho");
        }
        else if (treble > BrightTrebleDb)
        {
            bands.Add(new EqBand(8000, -1.5, 0.71, BandKind.HighShelf));
            notes.Add("agudos fortes: −1,5 dB");
        }

        // Level and dynamics from the peak meter. The volume makes up for the headroom the boosts
        // cost and lifts quiet material (the limiter guards the top); a wide spread between soft
        // and loud passages gets some night mode.
        levels.Sort();
        double loud = levels[(int)(levels.Count * 0.95)], soft = levels[(int)(levels.Count * 0.2)];
        double makeup = bands.Count == 0 ? 0 : -Equalizer.Preamp(bands);
        double trim = Math.Round(Math.Clamp(makeup + Math.Max(0, -6 - loud), 0, 6) * 2) / 2;
        double night = (loud - soft) switch { > 24 => 0.4, > 16 => 0.2, _ => 0 };
        if (trim > 0)
        {
            notes.Add($"volume +{trim:0.#} dB");
        }
        if (night > 0)
        {
            notes.Add($"Noite {night * 100:0}%");
        }
        return new PlaylistProposal(bands, trim, night, notes.Count == 0 ? "já soa bem como está: nada a mudar" : string.Join("; ", notes));
    }

    private static double Centre(int band) => 20 * Math.Pow(1000, (band + 0.5) / EngineClient.SpectrumBands);

    /// <summary>Mean level in dB of the bands whose centre falls in a frequency range.</summary>
    private double Level(double low, double high)
    {
        double sum = 0;
        int count = 0;
        for (int band = 0; band < power.Length; band++)
        {
            if (Centre(band) >= low && Centre(band) < high)
            {
                sum += 10 * Math.Log10(Math.Max(power[band] / frames, 1e-12));
                count++;
            }
        }
        return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// Typical tilt of mastered music in dB relative to 1 kHz, for the engine's "strongest bin per
    /// band" reading: what the thresholds above treat as neither bass-heavy nor thin, dull nor bright.
    /// </summary>
    public static double ReferenceDb(double frequency)
    {
        double octaves = Math.Log2(frequency / 1000);
        double tilt = frequency <= 2000 ? -3 * octaves : -3 - 5 * Math.Log2(frequency / 2000);
        return frequency < 60 ? tilt - 9 * Math.Log2(60 / frequency) : tilt;
    }
}
