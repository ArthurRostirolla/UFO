using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Ufo.Clips;

/// <summary>A way of getting screen frames into a hardware encoder that works on this machine.</summary>
public sealed record CapturePipeline(string Filter, string Encoder);

/// <summary>
/// The replay buffer. One ffmpeg child captures the screen on the GPU (Windows Graphics Capture), encodes
/// it with the hardware encoder and writes one-second MPEG-TS segments, sound included, into a
/// scratch folder; segments older than the retroactive time are deleted. Saving a clip joins the
/// last N segments into an MP4 without re-encoding.
/// </summary>
public sealed class ClipRecorder : IDisposable
{
    private static readonly string BufferFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UFO", "clip-buffer");

    private readonly ClipAudio audio;
    private readonly Dictionary<string, CapturePipeline> pipelines = [];
    private readonly ChildProcessJob job = new();
    private Process? ffmpeg;
    private Timer? pruning;
    private ClipSettings settings = new();
    private int? recordingFrom;

    public ClipRecorder(ClipAudio audio) => this.audio = audio;

    public bool IsRunning => ffmpeg is { HasExited: false };

    public bool IsRecording => recordingFrom is not null;

    /// <summary>How far back the buffer reaches; changes on the fly.</summary>
    public int RetroSeconds
    {
        set => settings = settings with { RetroSeconds = value };
    }

    /// <summary>"h264_qsv · 1920×1080 · 60 fps" for the status line, once running.</summary>
    public string Description { get; private set; } = "";

    /// <summary>The bundled ffmpeg beside the app, or the one on PATH.</summary>
    public static string FfmpegPath
    {
        get
        {
            string bundled = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe");
            return File.Exists(bundled) ? bundled : "ffmpeg";
        }
    }

    /// <summary>
    /// Starts (or restarts) the buffer. Slow the first time for a given codec: it tries the capture
    /// pipelines until one encodes. Throws <see cref="InvalidOperationException"/> with a message for
    /// the user when ffmpeg is missing or no pipeline works.
    /// </summary>
    /// <param name="monitorWidth">Width of what is captured: one monitor, or several side by side.</param>
    /// <param name="monitorHeight">Height of what is captured (the tallest monitor when there are several).</param>
    /// <param name="monitors">Windows handles (see <see cref="MonitorAt"/>) of the monitors to capture, left to right.</param>
    public void Start(ClipSettings wanted, int monitorWidth, int monitorHeight, ulong[] monitors)
    {
        Stop();
        settings = wanted;
        (int width, int height) = wanted.OutputSize(monitorWidth, monitorHeight);
        bool isScaled = (width, height) != (monitorWidth, monitorHeight);
        string key = $"{wanted.Codec}|{string.Join('+', monitors)}|{wanted.FramesPerSecond}|{(isScaled ? $"{width}x{height}" : "")}";
        if (!pipelines.TryGetValue(key, out CapturePipeline? pipeline))
        {
            pipeline = Candidates(wanted, isScaled ? (width, height) : null, monitors, monitorHeight).FirstOrDefault(Works);
            if (pipeline is not null)
            {
                pipelines[key] = pipeline; // a failure is not remembered: the next try probes again
            }
        }
        if (pipeline is null)
        {
            throw new InvalidOperationException("Nenhum encoder de vídeo desta máquina aceitou a captura da tela. Atualize o driver da placa de vídeo.");
        }

        Directory.CreateDirectory(BufferFolder);
        foreach (string old in Directory.EnumerateFiles(BufferFolder))
        {
            File.Delete(old);
        }
        ffmpeg = Run(RecordArguments(wanted, pipeline, width, height, Path.Combine(BufferFolder, "seg%06d.ts")), hasInput: true);
        job.Add(ffmpeg);
        audio.SystemVolume = wanted.SystemVolume;
        audio.MicrophoneVolume = wanted.MicrophoneVolume;
        audio.Start(ffmpeg.StandardInput.BaseStream);
        pruning = new Timer(_ => Prune(), null, 2000, 2000);
        Description = $"{pipeline.Encoder} · {width}×{height} · {wanted.FramesPerSecond} fps";
    }

    public void Stop()
    {
        pruning?.Dispose();
        pruning = null;
        recordingFrom = null;
        audio.Stop();
        if (ffmpeg is { } process)
        {
            ffmpeg = null;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(); // MPEG-TS segments survive being cut off
                    process.WaitForExit(2000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                // already gone
            }
            process.Dispose();
        }
    }

    public void Dispose()
    {
        Stop();
        job.Dispose();
    }

    /// <summary>Starts a manual recording: from now on nothing is pruned until <see cref="StopRecordingAsync"/>.</summary>
    public void StartRecording() => recordingFrom = Segments().LastOrDefault().Index;

    /// <summary>Ends the manual recording and saves it; null when nothing was captured.</summary>
    public async Task<SavedClip?> StopRecordingAsync(string outputPath)
    {
        if (recordingFrom is not { } from)
        {
            return null;
        }
        SavedClip? clip = await SaveAsync(outputPath, segments => segments.Where(segment => segment.Index >= from));
        recordingFrom = null;
        return clip;
    }

    /// <summary>Saves the last <paramref name="seconds"/> seconds; null when the buffer is empty.</summary>
    public Task<SavedClip?> SaveClipAsync(int seconds, string outputPath) =>
        SaveAsync(outputPath, segments => LastSegments(segments, seconds));

    /// <summary>The newest <paramref name="seconds"/> finished segments (each lasts one second).</summary>
    public static IEnumerable<T> LastSegments<T>(IReadOnlyList<T> finished, int seconds) =>
        finished.Skip(Math.Max(0, finished.Count - seconds));

    private async Task<SavedClip?> SaveAsync(string outputPath, Func<IReadOnlyList<(int Index, string Path)>, IEnumerable<(int Index, string Path)>> choose)
    {
        if (!IsRunning)
        {
            return null;
        }
        await Task.Delay(1100); // lets the segment being written close, so the clip reaches the moment it was asked for
        List<(int Index, string Path)> all = Segments();
        // The newest file is still being written.
        List<(int Index, string Path)> chosen = [.. choose(all.GetRange(0, Math.Max(0, all.Count - 1)))];
        if (chosen.Count == 0)
        {
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
        string list = Path.Combine(BufferFolder, $"clip-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(list, chosen.Select(segment => $"file '{segment.Path.Replace('\\', '/')}'"));
        try
        {
            using Process join = Run(["-f", "concat", "-safe", "0", "-i", list, "-c", "copy", "-movflags", "+faststart", outputPath], hasInput: false);
            await join.WaitForExitAsync();
            return join.ExitCode == 0 && File.Exists(outputPath) ? new SavedClip(outputPath, chosen.Count) : null;
        }
        finally
        {
            File.Delete(list);
        }
    }

    private static List<(int Index, string Path)> Segments() =>
        Directory.Exists(BufferFolder)
            ? [.. Directory.EnumerateFiles(BufferFolder, "seg*.ts")
                .Select(path => (Index: int.Parse(Path.GetFileNameWithoutExtension(path).AsSpan(3), CultureInfo.InvariantCulture), Path: path))
                .OrderBy(segment => segment.Index)]
            : [];

    private void Prune()
    {
        try
        {
            List<(int Index, string Path)> all = Segments();
            if (all.Count == 0)
            {
                return;
            }
            // Five spare seconds, so a clip being joined never loses its first segment.
            int keepFrom = Math.Min(all[^1].Index - settings.RetroSeconds - 5, recordingFrom ?? int.MaxValue);
            foreach ((int index, string path) in all)
            {
                if (index < keepFrom)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // in use by a clip being saved; gone at the next pass
        }
    }

    /// <summary>
    /// Pipelines to try, best first: for each vendor, the path that keeps every frame on the GPU (the
    /// capture itself scales there); then the same encoders fed through system memory (the screen and
    /// the encoder can sit on different GPUs on laptops).
    /// </summary>
    /// <remarks>
    /// Several monitors are joined side by side in system memory (frames of different screens cannot be
    /// stacked on the GPU), each padded to the tallest one. Heavier than a single monitor.
    /// </remarks>
    public static IEnumerable<CapturePipeline> Candidates(ClipSettings settings, (int Width, int Height)? scaleTo, ulong[] monitors, int fullHeight)
    {
        string[] vendors = ["nvenc", "amf", "qsv", "mf"];
        string Grab(ulong monitor) => $"gfxcapture=hmonitor={monitor}:max_framerate={settings.FramesPerSecond}";
        if (monitors.Length > 1)
        {
            string screens = string.Concat(monitors.Select((monitor, index) => $"{Grab(monitor)},hwdownload,format=bgra,pad=iw:{fullHeight}[m{index}];"));
            string joined = $"{screens}{string.Concat(monitors.Select((_, index) => $"[m{index}]"))}hstack=inputs={monitors.Length}";
            string resize = scaleTo is { } size ? $",scale={size.Width}:{size.Height}:flags=fast_bilinear" : "";
            foreach (string vendor in vendors)
            {
                yield return new CapturePipeline($"{joined}{resize},format=nv12", $"{settings.Codec}_{vendor}");
            }
            yield break;
        }
        string grab = Grab(monitors[0]) + (scaleTo is { } to ? $":width={to.Width}:height={to.Height}:resize_mode=scale" : "");
        foreach (string vendor in vendors)
        {
            yield return new CapturePipeline(vendor == "qsv" ? $"{grab},hwmap=derive_device=qsv,format=qsv" : grab, $"{settings.Codec}_{vendor}");
        }
        foreach (string vendor in vendors)
        {
            yield return new CapturePipeline($"{grab},hwdownload,format=bgra,format=nv12", $"{settings.Codec}_{vendor}");
        }
    }

    private static bool Works(CapturePipeline pipeline)
    {
        try
        {
            using Process probe = Run(["-f", "lavfi", "-i", pipeline.Filter, "-frames:v", "1", "-c:v", pipeline.Encoder, "-f", "null", "-"], hasInput: false);
            if (!probe.WaitForExit(8000))
            {
                probe.Kill();
                return false;
            }
            return probe.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("O ffmpeg não foi encontrado. Ele deve estar na pasta ffmpeg ao lado do UFO.");
        }
    }

    public static string[] RecordArguments(ClipSettings settings, CapturePipeline pipeline, int width, int height, string segmentPattern)
    {
        int bitrate = settings.BitrateKbps(width, height);
        string fps = settings.FramesPerSecond.ToString(CultureInfo.InvariantCulture);
        return
        [
            "-thread_queue_size", "1024",
            // Positive delays the sound, negative the picture.
            "-itsoffset", (settings.SyncMilliseconds / 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
            "-f", "f32le", "-ar", ClipAudio.SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", "2", "-i", "pipe:0",
            "-f", "lavfi", "-i", pipeline.Filter,
            "-map", "1:v", "-map", "0:a",
            "-c:v", pipeline.Encoder, "-b:v", $"{bitrate}k", "-maxrate", $"{bitrate * 3 / 2}k", "-bufsize", $"{bitrate * 2}k",
            // The capture only delivers frames when the screen changes (the app keeps a pixel ticking on the
            // captured monitors, see ClipTicker); a constant rate keeps one keyframe (and so one segment) per second. No B-frames: every segment starts cleanly and can be cut at.
            "-fps_mode", "cfr", "-r", fps, "-g", fps, "-bf", "0",
            "-c:a", "aac", "-b:a", "160k",
            "-f", "segment", "-segment_time", "1", "-segment_format", "mpegts", segmentPattern,
        ];
    }

    /// <summary>The Windows handle of the monitor that shows a point of the desktop (0 when there is none).</summary>
    public static ulong MonitorAt(int x, int y) => (ulong)MonitorFromPoint(new ScreenPoint { X = x, Y = y }, 0);

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(ScreenPoint point, uint flags);

    internal static Process Run(IEnumerable<string> arguments, bool hasInput)
    {
        var start = new ProcessStartInfo(FfmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true, // also when unused: ffmpeg must not read the app's console
        };
        foreach (string argument in (string[])["-hide_banner", "-loglevel", "error", "-y", .. hasInput ? [] : (string[])["-nostdin"], .. arguments])
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start) ?? throw new Win32Exception();
    }
}

public sealed record SavedClip(string Path, int Seconds);

/// <summary>A Windows job that kills its processes when the app ends, however it ends.</summary>
internal sealed class ChildProcessJob : IDisposable
{
    private readonly nint handle = CreateJobObject(0, null);

    public ChildProcessJob()
    {
        const int ExtendedLimitInformation = 9;
        const uint KillOnJobClose = 0x2000;
        var limits = new ExtendedLimits { Basic = { LimitFlags = KillOnJobClose } };
        SetInformationJobObject(handle, ExtendedLimitInformation, ref limits, Marshal.SizeOf<ExtendedLimits>());
    }

    public void Add(Process process) => AssignProcessToJobObject(handle, process.Handle);

    public void Dispose() => CloseHandle(handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadTransfers, WriteTransfers, OtherTransfers;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimits info, int length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
