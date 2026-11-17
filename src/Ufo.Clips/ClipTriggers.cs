namespace Ufo.Clips;

/// <summary>
/// Decides when a stream of scores becomes one trigger: the score must stay at or above the
/// threshold for a number of checks in a row, and after firing it stays quiet for a cooldown.
/// </summary>
public sealed class TriggerGate(int checksInARow, TimeSpan cooldown)
{
    private int streak;
    private TimeSpan quietUntil = TimeSpan.MinValue;

    public bool Update(double score, double threshold, TimeSpan now)
    {
        streak = score >= threshold ? streak + 1 : 0;
        if (streak < checksInARow || now < quietUntil)
        {
            return false;
        }
        streak = 0;
        quietUntil = now + cooldown;
        return true;
    }
}

public static class ClipTriggers
{
    /// <summary>How long after a trigger the clip is cut, so the laugh itself is in it.</summary>
    public static readonly TimeSpan PostRoll = TimeSpan.FromSeconds(3);

    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(30);

    /// <summary>RMS level in dBFS of the last <paramref name="seconds"/> of a 16 kHz signal.</summary>
    public static double LevelDb(ReadOnlySpan<float> signal, double seconds)
    {
        ReadOnlySpan<float> tail = signal[^Math.Min(signal.Length, (int)(seconds * ClipAudio.TriggerRate))..];
        double sum = 0;
        foreach (float sample in tail)
        {
            sum += sample * sample;
        }
        return 10 * Math.Log10(Math.Max(sum / Math.Max(tail.Length, 1), 1e-12));
    }
}

/// <summary>
/// Hears laughter with YAMNet (Google's AudioSet classifier, converted to ONNX; runs on the CPU in a
/// few milliseconds). The model takes 16 kHz mono sound and scores 521 kinds of sound per ~1 s frame.
/// </summary>
public sealed class LaughterDetector : IDisposable
{
    // AudioSet classes 13–18: Laughter, Baby laughter, Giggle, Snicker, Belly laugh, Chuckle.
    private const int FirstLaughClass = 13, LastLaughClass = 18;

    private readonly Microsoft.ML.OnnxRuntime.InferenceSession session;

    private LaughterDetector(string modelPath) => session = new Microsoft.ML.OnnxRuntime.InferenceSession(modelPath);

    /// <summary>The model that travels beside the app.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "yamnet", "yamnet.onnx");

    /// <summary>Null when the model file is missing or unreadable: laughter clips are simply unavailable.</summary>
    public static LaughterDetector? TryLoad(string modelPath)
    {
        try
        {
            return File.Exists(modelPath) ? new LaughterDetector(modelPath) : null;
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            return null;
        }
    }

    /// <summary>How sure the model is that there is laughter in the sound, 0 to 1 (its best frame and best laugh class).</summary>
    public double Score(float[] mono16k)
    {
        var waveform = new Microsoft.ML.OnnxRuntime.Tensors.DenseTensor<float>(mono16k, [mono16k.Length]);
        using var results = session.Run([Microsoft.ML.OnnxRuntime.NamedOnnxValue.CreateFromTensor("waveform", waveform)]);
        var scores = results[0].AsTensor<float>();
        double best = 0;
        for (int frame = 0; frame < scores.Dimensions[0]; frame++)
        {
            for (int kind = FirstLaughClass; kind <= LastLaughClass; kind++)
            {
                best = Math.Max(best, scores[frame, kind]);
            }
        }
        return best;
    }

    public void Dispose() => session.Dispose();
}
