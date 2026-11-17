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
