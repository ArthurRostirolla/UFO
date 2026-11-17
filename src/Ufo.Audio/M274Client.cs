using System.IO.MemoryMappedFiles;
using Microsoft.Win32;

namespace Ufo.Audio;

/// <summary>What the M274 reports back while audio flows. Spectrum is 32 log bands (20 Hz–20 kHz) in dBFS.</summary>
public sealed record M274Meters(uint Blocks, float PeakLeft, float PeakRight, float LimiterGain, float HeadroomDb, float SampleRate, uint LatencyFrames, float[] Spectrum);

/// <summary>
/// Talks to the M274 (m274/apo/M274Apo.cpp) through files both sides memory-map in
/// C:\ProgramData\UFO; the byte layouts mirror m274/apo/ipc.h. Settings are written as a seqlock
/// (sequence odd while writing, even when complete), so the audio thread never sees half an update,
/// and because they are files the M274 finds the last settings again after a restart.
/// </summary>
public sealed class M274Client : IDisposable
{
    public const string DataDirectory = @"C:\ProgramData\UFO";
    public const string RenderClsid = "{7C1F2D3A-5B6E-4A91-9C0D-55464F454E01}";
    public const string CaptureClsid = "{7C1F2D3A-5B6E-4A91-9C0D-55464F454E02}";
    public const string GameEndpointName = "AWPRO H Wireless Game";
    public const int SpectrumBands = 32;

    private const uint Magic = 0x324F4655; // "UFO2"
    private const int MaxBands = 32, MaxVoiceBands = 8;
    private const int ParamsSize = 8 + 48 + MaxBands * 16;
    private const int VoiceSize = 8 + 48 + MaxVoiceBands * 16;
    private const int MetersSize = 40 + SpectrumBands * 4;
    private const string ModeEffectValue = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6";
    private const string DevicesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio";
    private const string FriendlyNameValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";

    private readonly SettingsFile render = new(Path.Combine(DataDirectory, "m274.bin"), ParamsSize);
    private readonly SettingsFile capture = new(Path.Combine(DataDirectory, "mic.bin"), VoiceSize);

    /// <summary>True when Windows has the M274 attached to the headset's game output.</summary>
    public static bool IsInstalled() => IsAttached("Render", GameEndpointName, RenderClsid);

    /// <summary>True when the M274 is attached to at least one AWPRO microphone.</summary>
    public static bool IsMicrophoneInstalled() => IsAttached("Capture", "AWPRO", CaptureClsid);

    /// <summary>True when the installer's backup exists, i.e. the user installed the M274 at some point.</summary>
    public static bool WasInstalled() =>
        File.Exists(Path.Combine(DataDirectory, "m274-backup.json")) || File.Exists(Path.Combine(DataDirectory, "engine-backup.json")); // engine-*: before the M274 rename

    private static bool IsAttached(string flow, string nameFragment, string clsid)
    {
        using RegistryKey? devices = Registry.LocalMachine.OpenSubKey($@"{DevicesKey}\{flow}");
        foreach (string id in devices?.GetSubKeyNames() ?? [])
        {
            using RegistryKey? properties = devices?.OpenSubKey($@"{id}\Properties");
            if (properties?.GetValue(FriendlyNameValue) is not string name || !name.Contains(nameFragment, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            using RegistryKey? effects = devices?.OpenSubKey($@"{id}\FxProperties");
            if (string.Equals(effects?.GetValue(ModeEffectValue) as string, clsid, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Sends the output chain; heard within one audio block (about 10 ms). <paramref name="volumeDb"/>
    /// is the Windows volume of the headset (0 = full), used by loudness compensation.
    /// </summary>
    public void Send(EqualizerSettings equalizer, SoundSettings sound, double volumeDb = 0)
    {
        List<EqBand> bands = equalizer.Bands.Concat(sound.ExtraBands(volumeDb)).Take(MaxBands).ToList();
        render.Write(accessor =>
        {
            accessor.Write(8, equalizer.IsEnabled ? 1u : 0u);
            accessor.Write(12, (uint)bands.Count);
            accessor.Write(16, (float)sound.TrimDb);
            accessor.Write(20, sound.IsLimiterOn ? 1u : 0u);
            accessor.Write(24, (float)sound.Crossfeed);
            accessor.Write(28, (float)sound.Night);
            accessor.Write(32, (float)sound.Footsteps);
            accessor.Write(36, (float)sound.BassEnhance);
            accessor.Write(40, sound.IsBassProtected ? 1u : 0u);
            WriteBands(accessor, 56, bands);
        });
    }

    /// <summary>Sends the microphone chain.</summary>
    public void SendVoice(VoiceSettings voice)
    {
        List<EqBand> bands = voice.Bands().Take(MaxVoiceBands).ToList();
        capture.Write(accessor =>
        {
            accessor.Write(8, voice.IsEnabled ? 1u : 0u);
            accessor.Write(12, voice.IsHighPassOn ? 1u : 0u);
            accessor.Write(16, (float)voice.Denoise);
            accessor.Write(20, (float)voice.Gate);
            accessor.Write(24, (float)voice.DeEss);
            accessor.Write(28, (float)voice.Compress);
            accessor.Write(32, (float)voice.Leveler);
            accessor.Write(36, (float)voice.TrimDb);
            accessor.Write(40, 1u); // limiter always on for the microphone
            accessor.Write(44, (uint)bands.Count);
            WriteBands(accessor, 56, bands);
        });
    }

    private static void WriteBands(MemoryMappedViewAccessor accessor, int offset, List<EqBand> bands)
    {
        for (int index = 0; index < bands.Count; index++)
        {
            EqBand band = bands[index];
            int at = offset + index * 16;
            accessor.Write(at, (uint)band.Kind);
            accessor.Write(at + 4, (float)band.Frequency);
            accessor.Write(at + 8, (float)band.Gain);
            accessor.Write(at + 12, (float)band.Q);
        }
    }

    /// <summary>Latest output meters, or null when the M274 has never run.</summary>
    public M274Meters? ReadMeters() => ReadMeters("m274-meters.bin");

    /// <summary>Latest microphone meters, or null when the M274 has never run on the microphone.</summary>
    public M274Meters? ReadMicrophoneMeters() => ReadMeters("mic-meters.bin");

    private static M274Meters? ReadMeters(string fileName)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(DataDirectory, fileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] data = new byte[MetersSize];
            if (stream.Read(data, 0, MetersSize) < MetersSize || BitConverter.ToUInt32(data, 0) != Magic)
            {
                return null;
            }
            float[] spectrum = new float[SpectrumBands];
            Buffer.BlockCopy(data, 40, spectrum, 0, SpectrumBands * 4);
            return new M274Meters(
                BitConverter.ToUInt32(data, 4), BitConverter.ToSingle(data, 8), BitConverter.ToSingle(data, 12),
                BitConverter.ToSingle(data, 16), BitConverter.ToSingle(data, 20), BitConverter.ToSingle(data, 24),
                BitConverter.ToUInt32(data, 32), spectrum);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        render.Dispose();
        capture.Dispose();
    }

    /// <summary>One mapped settings file written under the seqlock protocol.</summary>
    private sealed class SettingsFile(string path, int size) : IDisposable
    {
        private MemoryMappedFile? mapping;
        private MemoryMappedViewAccessor? view;
        private uint sequence;

        public void Write(Action<MemoryMappedViewAccessor> writePayload)
        {
            MemoryMappedViewAccessor accessor = Open();
            sequence = (sequence | 1) + 2;   // odd: the M274 ignores the payload while it changes
            accessor.Write(4, sequence);
            writePayload(accessor);
            accessor.Write(0, Magic);
            sequence++;                       // even: complete
            accessor.Write(4, sequence);
            accessor.Flush();
        }

        private MemoryMappedViewAccessor Open()
        {
            if (view is not null)
            {
                return view;
            }
            Directory.CreateDirectory(DataDirectory);
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            mapping = MemoryMappedFile.CreateFromFile(stream, null, size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
            view = mapping.CreateViewAccessor(0, size);
            sequence = view.ReadUInt32(4);    // continue from what the M274 may already have applied
            return view;
        }

        public void Dispose()
        {
            view?.Dispose();
            mapping?.Dispose();
        }
    }
}
