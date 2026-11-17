namespace Ufo.Clips;

/// <summary>
/// Everything the clips screen can change. Kept inside the app preferences, so every choice is
/// saved at once. Hotkeys are stored as Win32 modifier flags and a virtual-key code.
/// </summary>
public sealed record ClipSettings(
    bool IsEnabled = false,
    bool OnlyInGames = false,
    int Monitor = 0, // ClipSettings.AllMonitors records every screen side by side
    int Height = 0, // 0 = the monitor's own resolution
    int FramesPerSecond = 60,
    int Quality = 2, // 0 low … 3 maximum
    string Codec = "h264",
    int RetroSeconds = 30,
    double SystemVolume = 1, // 0 leaves the computer's sound out
    double MicrophoneVolume = 1, // 0 leaves the microphone out
    int SyncMilliseconds = 0,
    uint ClipModifiers = ClipSettings.Control | ClipSettings.Alt,
    int ClipKey = 'X',
    uint RecordModifiers = ClipSettings.Control | ClipSettings.Alt,
    int RecordKey = 'R',
    bool ClipOnLaughter = false,
    double LaughterSensitivity = 0.5,
    bool ClipOnShout = false,
    double ShoutDb = -8,
    bool ShowOverlay = true,
    bool ShowRecordingDot = false,
    int OverlayCorner = 1, // 0 top-left, 1 top-right, 2 bottom-left, 3 bottom-right
    double StorageLimitGb = 0) // 0 = no limit
{
    public const int AllMonitors = -1;

    public const uint Alt = 1, Control = 2, Shift = 4, Windows = 8;

    public const int MinRetroSeconds = 15, MaxRetroSeconds = 600;

    /// <summary>Output size for a monitor: its own, or scaled down to <see cref="Height"/> keeping the shape (even numbers).</summary>
    public (int Width, int Height) OutputSize(int monitorWidth, int monitorHeight)
    {
        if (Height <= 0 || Height >= monitorHeight)
        {
            return (monitorWidth, monitorHeight);
        }
        int width = (int)Math.Round((double)monitorWidth * Height / monitorHeight / 2) * 2;
        return (width, Height / 2 * 2);
    }

    /// <summary>
    /// Target bitrate: 8/16/30/50 Mbit/s at 1080p60 H.264 for the four quality steps, growing slower
    /// than the pixel count and frame rate (bigger frames compress better), less for HEVC and AV1.
    /// </summary>
    public int BitrateKbps(int width, int height)
    {
        double atFullHd = new[] { 8000.0, 16000, 30000, 50000 }[Math.Clamp(Quality, 0, 3)];
        double size = Math.Pow(width * (double)height / (1920 * 1080), 0.75);
        double rate = Math.Sqrt(FramesPerSecond / 60.0);
        double codec = Codec switch { "hevc" => 0.7, "av1" => 0.6, _ => 1 };
        return (int)Math.Round(atFullHd * size * rate * codec / 100) * 100;
    }
}
