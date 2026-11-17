using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ufo.Audio;
using Ufo.Device;

namespace Ufo;

/// <summary>Everything the user tunes on the headset itself.</summary>
public sealed record HeadsetSettings(
    SoundMode SoundMode,
    byte AncLevel,
    bool IsMicEnabled,
    bool IsNoiseReductionOn,
    byte SidetoneLevel,
    bool IsSleepEnabled,
    int SleepMinutes,
    bool IsEnduranceEnabled,
    byte EnduranceThreshold);

/// <summary>
/// A named sound setup (equalizer plus extras). <c>Apps</c> lists program names, comma-separated,
/// that switch to this profile when one of them is in the foreground.
/// </summary>
public sealed record SoundProfile(string Name, EqualizerSettings Equalizer, SoundSettings Sound, string Apps = "");

/// <summary>
/// What "Salvar" stores, re-applied on every start. <c>Equalizer</c> is the active profile's curve,
/// kept at the top level so files saved before profiles existed still load.
/// </summary>
public sealed record SavedProfile(
    HeadsetSettings? Headset,
    EqualizerSettings Equalizer,
    VoiceSettings? Voice = null,
    List<SoundProfile>? Profiles = null,
    string? ActiveProfile = null,
    bool IsAutoProfile = true);

/// <summary>Keeps the saved profile in %AppData%\UFO\profile.json.</summary>
public static class ProfileStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UFO", "profile.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static SavedProfile? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<SavedProfile>(File.ReadAllText(FilePath), Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(SavedProfile profile)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath) ?? ".");
        // Write then swap, so a crash mid-write never leaves a half-written profile.
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, Serialize(profile));
        File.Move(temporary, FilePath, overwrite: true);
    }

    /// <summary>Stable text form, used both to store and to compare (records with lists don't compare by value).</summary>
    public static string Serialize(SavedProfile profile) => JsonSerializer.Serialize(profile, Options);
}
