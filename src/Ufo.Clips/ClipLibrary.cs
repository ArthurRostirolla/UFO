using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace Ufo.Clips;

/// <summary>A saved clip. <see cref="Trigger"/> is "atalho", "risada", "grito", "gravação" or "corte".</summary>
public sealed record ClipFile(string Path, string Game, string Trigger, DateTime Created, long Bytes)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>
/// The clips on disk: Videos\UFO\&lt;game&gt;\&lt;game&gt; &lt;date&gt; &lt;time&gt; &lt;trigger&gt;.mp4. The folder is the
/// whole database, so clips moved, renamed or deleted in Explorer just show up that way.
/// </summary>
public static class ClipLibrary
{
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "UFO");

    private static readonly string ThumbnailFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UFO", "clip-thumbnails");

    private static readonly string[] Triggers = ["atalho", "risada", "grito", "gravação", "corte"];

    /// <summary>Where a new clip goes. Characters Windows forbids in names are dropped from the game.</summary>
    public static string NewPath(string game, string trigger, DateTime now)
    {
        string safe = string.Concat(game.Split(Path.GetInvalidFileNameChars())).Trim(' ', '.');
        safe = safe.Length == 0 ? "Área de trabalho" : safe;
        return Path.Combine(Root, safe, $"{safe} {now.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)} {trigger}.mp4");
    }

    /// <summary>Every clip, newest first.</summary>
    public static List<ClipFile> List() =>
        Directory.Exists(Root)
            ? [.. new DirectoryInfo(Root).EnumerateFiles("*.mp4", System.IO.SearchOption.AllDirectories)
                .OrderByDescending(file => file.CreationTime)
                .Select(file => new ClipFile(
                    file.FullName,
                    file.Directory?.Name ?? "",
                    Triggers.FirstOrDefault(trigger => Path.GetFileNameWithoutExtension(file.Name).EndsWith(" " + trigger, StringComparison.Ordinal)) ?? "",
                    file.CreationTime,
                    file.Length))]
            : [];

    /// <summary>A small still from one second in, made once and kept; null when ffmpeg could not make it.</summary>
    public static async Task<string?> ThumbnailAsync(ClipFile clip)
    {
        string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"{clip.Path}|{clip.Bytes}"))) + ".jpg";
        string thumbnail = Path.Combine(ThumbnailFolder, name);
        if (File.Exists(thumbnail))
        {
            return thumbnail;
        }
        Directory.CreateDirectory(ThumbnailFolder);
        return await RunAsync("-ss", "1", "-i", clip.Path, "-frames:v", "1", "-vf", "scale=320:-2", thumbnail) && File.Exists(thumbnail)
            ? thumbnail
            : null;
    }

    /// <summary>Copies the stretch between two instants into a new clip beside the original; null on failure.</summary>
    /// <remarks>ponytail: stream copy, so the start snaps to the previous keyframe (one per second). Re-encode for frame accuracy.</remarks>
    public static async Task<string?> TrimAsync(ClipFile clip, TimeSpan start, TimeSpan end)
    {
        string folder = Path.GetDirectoryName(clip.Path) ?? Root;
        string baseName = Triggers.Aggregate(clip.Name, (name, trigger) => name.EndsWith(" " + trigger, StringComparison.Ordinal) ? name[..^(trigger.Length + 1)] : name);
        string output = Path.Combine(folder, $"{baseName} corte.mp4");
        for (int copy = 2; File.Exists(output); copy++)
        {
            output = Path.Combine(folder, $"{baseName} {copy} corte.mp4");
        }
        string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        return await RunAsync("-ss", Seconds(start), "-to", Seconds(end), "-i", clip.Path, "-c", "copy", "-movflags", "+faststart", output) ? output : null;
    }

    /// <summary>Renames a clip, keeping its folder and extension. Throws <see cref="IOException"/> when the name is taken.</summary>
    public static string Rename(ClipFile clip, string newName)
    {
        string safe = string.Concat(newName.Split(Path.GetInvalidFileNameChars())).Trim(' ', '.');
        if (safe.Length == 0 || safe == clip.Name)
        {
            return clip.Path;
        }
        string target = Path.Combine(Path.GetDirectoryName(clip.Path) ?? Root, safe + ".mp4");
        File.Move(clip.Path, target);
        return target;
    }

    /// <summary>Sends a clip to the Recycle Bin.</summary>
    public static void Delete(ClipFile clip) =>
        FileSystem.DeleteFile(clip.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    /// <summary>Which clips must go, oldest first, for the library to fit in <paramref name="limitBytes"/>.</summary>
    public static IEnumerable<ClipFile> OverLimit(IReadOnlyList<ClipFile> newestFirst, long limitBytes)
    {
        long total = newestFirst.Sum(clip => clip.Bytes);
        for (int i = newestFirst.Count - 1; i >= 0 && total > limitBytes; i--)
        {
            total -= newestFirst[i].Bytes;
            yield return newestFirst[i];
        }
    }

    /// <summary>Deletes the oldest clips (for good: the point is freeing space) until the library fits the limit.</summary>
    public static void EnforceLimit(double limitGb)
    {
        if (limitGb <= 0)
        {
            return;
        }
        foreach (ClipFile clip in OverLimit(List(), (long)(limitGb * 1024 * 1024 * 1024)).ToList())
        {
            try
            {
                File.Delete(clip.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // open in a player; goes next time
            }
        }
    }

    private static async Task<bool> RunAsync(params string[] arguments)
    {
        try
        {
            using System.Diagnostics.Process process = ClipRecorder.Run(arguments, hasInput: false);
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // ffmpeg missing
        }
    }
}
