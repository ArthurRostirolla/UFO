using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ufo.Audio;

/// <summary>
/// Windows-side audio of the headset (through WASAPI): its volume for loudness compensation, test
/// tones for the ear calibration, and a record-and-play microphone check. Everything here goes
/// through the normal Windows audio path, so it is processed by the UFO Engine like any other app.
/// </summary>
public sealed class HeadsetAudio : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private MMDevice? output;
    private WasapiPlayer? player;
    private ToneProvider? tone;

    /// <summary>Raised (on a background thread) when the headset's Windows volume changes.</summary>
    public event Action? VolumeChanged;

    /// <summary>Windows volume of the headset output in dB (0 = full); 0 when the headset is absent.</summary>
    public double VolumeDb
    {
        get
        {
            try
            {
                return Output() is { } device ? device.AudioEndpointVolume.MasterVolumeLevel : 0;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                output = null; // the endpoint went away (unplugged, audio service restarting)
                return 0;
            }
        }
    }

    /// <summary>"16 kHz" / "48 kHz" of the active AWPRO microphone, or null when there is none.</summary>
    public int? MicrophoneSampleRate() => Find(DataFlow.Capture, "AWPRO") is { } microphone ? MixSampleRate(microphone) : null;

    private static int MixSampleRate(MMDevice device)
    {
        using AudioClient client = device.CreateAudioClient();
        return client.MixFormat.SampleRate;
    }

    /// <summary>The headset output as Windows sees it, or null when it is absent.</summary>
    public EndpointInfo? OutputInfo() => Output() is { } device ? Describe(device, DataFlow.Render) : null;

    /// <summary>The headset microphone as Windows sees it, or null when it is absent.</summary>
    public EndpointInfo? MicrophoneInfo() => Find(DataFlow.Capture, "AWPRO") is { } device ? Describe(device, DataFlow.Capture) : null;

    private EndpointInfo Describe(MMDevice device, DataFlow flow)
    {
        bool isDefault;
        try
        {
            isDefault = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia).ID == device.ID;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            isDefault = false; // Windows has no default device for this direction
        }
        using AudioClient client = device.CreateAudioClient();
        WaveFormat format = client.MixFormat;
        AudioEndpointVolume volume = device.AudioEndpointVolume;
        // The mix format is always 32-bit float; the device format holds the real bit depth.
        int bits = device.Properties.Contains(PropertyKeys.PKEY_AudioEngine_DeviceFormat)
            && device.Properties[PropertyKeys.PKEY_AudioEngine_DeviceFormat].Value is byte[] { Length: >= 16 } blob
                ? BitConverter.ToInt16(blob, 14)
                : format.BitsPerSample;
        return new EndpointInfo(device.FriendlyName, isDefault, format.SampleRate, bits, volume.MasterVolumeLevel, volume.Mute);
    }

    /// <summary>Listens to the headset microphone for a while and reports how loud it was.</summary>
    public async Task<MicrophoneLevel> MeasureMicrophoneAsync(TimeSpan duration)
    {
        (byte[] data, WaveFormat format) = await RecordAsync(duration, CancellationToken.None);
        int bytesPerSample = format.BitsPerSample / 8;
        double peak = 0, sumOfSquares = 0;
        int count = 0;
        for (int at = 0; at + bytesPerSample <= data.Length; at += bytesPerSample, count++)
        {
            // Shared-mode capture delivers 32-bit float or 16/24-bit integers.
            double sample = bytesPerSample switch
            {
                4 => BitConverter.ToSingle(data, at),
                3 => ((data[at + 2] << 24) | (data[at + 1] << 16) | (data[at] << 8)) / 2147483648.0,
                _ => BitConverter.ToInt16(data, at) / 32768.0,
            };
            peak = Math.Max(peak, Math.Abs(sample));
            sumOfSquares += sample * sample;
        }
        return new MicrophoneLevel(Decibels(peak), Decibels(Math.Sqrt(sumOfSquares / Math.Max(count, 1))));

        static double Decibels(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-6));
    }

    private async Task<(byte[] Data, WaveFormat Format)> RecordAsync(TimeSpan duration, CancellationToken cancellation)
    {
        MMDevice microphone = Find(DataFlow.Capture, "AWPRO") ?? throw new InvalidOperationException("Microfone do fone não encontrado.");
        using var recorded = new MemoryStream();
        using WasapiRecorder capture = new WasapiRecorderBuilder().WithDevice(microphone).WithSharedMode().Build();
        capture.DataAvailable += (buffer, _, _, _) => recorded.Write(buffer);
        capture.StartRecording();
        WaveFormat format = capture.WaveFormat; // known only once the recorder has opened the device
        await Task.Delay(duration, cancellation);
        capture.StopRecording();
        return (recorded.ToArray(), format);
    }

    /// <summary>Names of the programs producing sound on the headset right now.</summary>
    public IReadOnlyList<string> AudibleProcesses()
    {
        var names = new List<string>();
        try
        {
            if (Output() is not { } device)
            {
                return names;
            }
            AudioSessionManager manager = device.AudioSessionManager;
            manager.RefreshSessions();
            for (int index = 0; index < manager.Sessions.Count; index++)
            {
                AudioSessionControl session = manager.Sessions[index];
                if (session.AudioMeterInformation.MasterPeakValue > 0.001f && session.GetProcessID != 0)
                {
                    names.Add(System.Diagnostics.Process.GetProcessById((int)session.GetProcessID).ProcessName);
                }
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            // a session or its process ended while being read: report what was found
        }
        return names;
    }

    /// <summary>Starts (or retunes) a steady test tone on the headset. Level in dBFS.</summary>
    public void PlayTone(double frequency, double levelDb)
    {
        if (Output() is not { } device)
        {
            return;
        }
        if (player is null)
        {
            tone = new ToneProvider(MixSampleRate(device));
            player = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithLatency(60).Build();
            player.Init(tone);
            player.Play();
        }
        tone?.Set(frequency, Math.Pow(10, levelDb / 20));
    }

    public void StopTone()
    {
        player?.Stop();
        player?.Dispose();
        player = null;
        tone = null;
    }

    /// <summary>Records the headset microphone for a few seconds and plays it back on the headset.</summary>
    public async Task RecordAndPlayAsync(TimeSpan duration, CancellationToken cancellation = default)
    {
        MMDevice speaker = Output() ?? throw new InvalidOperationException("Saída do fone não encontrada.");
        (byte[] data, WaveFormat format) = await RecordAsync(duration, cancellation);
        using var recorded = new MemoryStream(data);
        using WasapiPlayer playback = new WasapiPlayerBuilder().WithDevice(speaker).WithSharedMode().WithEventSync().WithLatency(100).Build();
        var finished = new TaskCompletionSource();
        playback.PlaybackStopped += (_, _) => finished.TrySetResult();
        playback.Init(new RawSourceWaveStream(recorded, format));
        playback.Play();
        await finished.Task.WaitAsync(cancellation);
    }

    private MMDevice? Output()
    {
        if (output is { State: DeviceState.Active })
        {
            return output;
        }
        output = Find(DataFlow.Render, "AWPRO H Wireless Game") ?? Find(DataFlow.Render, "AWPRO");
        if (output is not null)
        {
            output.AudioEndpointVolume.OnVolumeNotification += _ => VolumeChanged?.Invoke();
        }
        return output;
    }

    private MMDevice? Find(DataFlow flow, string nameFragment) =>
        enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .FirstOrDefault(device => device.FriendlyName.Contains(nameFragment, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        StopTone();
        enumerator.Dispose();
    }

    /// <summary>Stereo sine whose frequency and level can change while it plays (level glides, no clicks).</summary>
    private sealed class ToneProvider(int sampleRate) : ISampleProvider
    {
        private double frequency = 1000, targetLevel, level, phase;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 2);

        public void Set(double newFrequency, double newLevel)
        {
            frequency = newFrequency;
            targetLevel = newLevel;
        }

        public int Read(Span<float> buffer)
        {
            for (int index = 0; index + 1 < buffer.Length; index += 2)
            {
                level += (targetLevel - level) * 0.002;
                phase += 2 * Math.PI * frequency / sampleRate;
                if (phase > 2 * Math.PI)
                {
                    phase -= 2 * Math.PI;
                }
                float sample = (float)(level * Math.Sin(phase));
                buffer[index] = sample;
                buffer[index + 1] = sample;
            }
            return buffer.Length;
        }
    }
}

/// <summary>An audio endpoint as Windows reports it. Volume in dB (0 = full).</summary>
public sealed record EndpointInfo(string Name, bool IsDefault, int SampleRate, int Bits, double VolumeDb, bool IsMuted);

/// <summary>Microphone loudness over a stretch of time, in dBFS.</summary>
public sealed record MicrophoneLevel(double PeakDb, double RmsDb);

/// <summary>
/// Ear calibration: the listener matches the loudness of test tones against a 1 kHz reference. The
/// level each tone needed, compared with the equal-loudness contour, tells how far this headphone
/// (on this head, with this seal) is from neutral at that frequency. Approximate and personal by nature.
/// </summary>
public static class EarCalibration
{
    public const double ReferenceFrequency = 1000;
    public const double ReferenceLevelDb = -32;

    /// <summary>Test frequencies and, for each, how much louder (dB SPL) a tone must be to sound as
    /// loud as 1 kHz at a comfortable level (rounded from the ISO 226 70-phon contour).</summary>
    public static readonly (double Frequency, double ContourDb)[] Points =
    [
        (63, 15.5), (125, 9), (250, 4), (500, 1), (2000, -1), (4000, -3.5), (8000, 7), (12000, 9),
    ];

    /// <summary>
    /// Correction bands from the matched levels (dB relative to the reference, one per point).
    /// A tone that needed more level than the contour predicts is weak on this headphone, so it is
    /// boosted, and vice versa. Corrections are softened (70%) and capped at ±8 dB.
    /// </summary>
    public static IReadOnlyList<EqBand> Correction(IReadOnlyList<double> matchedLevelsDb) =>
        Points.Select((point, index) =>
        {
            double shortfall = matchedLevelsDb[index] - point.ContourDb;
            return new EqBand(point.Frequency, Math.Round(Math.Clamp(shortfall * 0.7, -8, 8), 1), 1.0);
        }).Where(band => Math.Abs(band.Gain) >= 0.5).ToList();
}
