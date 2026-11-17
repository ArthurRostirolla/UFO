using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ufo.Audio;

/// <summary>Something the watch noticed about the microphone. Kinds: see <see cref="MicrophoneEvent"/> constants.</summary>
public sealed record MicrophoneEvent(DateTime At, string Kind, string Text)
{
    public const string Volume = "volume", Stall = "travada", Drop = "queda", Recovered = "voltou", App = "programa", Device = "aparelho";
}

/// <summary>
/// Finds the two failures of a voice signal from its level alone, fed about ten times a second:
/// a stall (the audio stopped arriving for a moment) and a drop (still speaking, but suddenly much quieter).
/// </summary>
/// <remarks>
/// ponytail: a drop is "12 dB or more under the usual speaking level, yet clearly above the room's
/// noise, for 1.5 s". Whispering on purpose looks the same; the log shows the numbers so a wrong
/// call is easy to spot. Tune the three constants below if it cries wolf.
/// </remarks>
public sealed class MicrophoneAnalyzer
{
    public const double DropDb = 12, AboveNoiseDb = 8, StallMilliseconds = 150;
    private static readonly TimeSpan DropTime = TimeSpan.FromSeconds(1.5), StallQuiet = TimeSpan.FromSeconds(2);

    private double speechDb = double.NaN, noiseDb = double.NaN;
    private TimeSpan spoken, lastSpeech = TimeSpan.MinValue / 2, lowSince, lastStall = TimeSpan.MinValue / 2, previous;
    private bool isLow, isDropped;

    /// <summary>The usual level while speaking, in dBFS (NaN until enough speech was heard).</summary>
    public double SpeechDb => speechDb;

    public IEnumerable<(string Kind, string Text)> Feed(double levelDb, double gapMilliseconds, TimeSpan now)
    {
        TimeSpan step = previous == default ? TimeSpan.Zero : now - previous;
        previous = now;
        if (gapMilliseconds >= StallMilliseconds && now - lastStall > StallQuiet)
        {
            lastStall = now;
            yield return (MicrophoneEvent.Stall, $"o som do microfone parou de chegar por {gapMilliseconds:0} ms");
        }

        // Room noise: the quietest the signal gets, allowed to creep up slowly.
        noiseDb = double.IsNaN(noiseDb) ? levelDb : Math.Min(levelDb, noiseDb + 0.5 * step.TotalSeconds);
        bool isVoice = levelDb > noiseDb + AboveNoiseDb && levelDb > -55;
        bool isUsual = isVoice && (double.IsNaN(speechDb) || levelDb > speechDb - DropDb);
        if (isUsual)
        {
            spoken += step;
            lastSpeech = now;
            // Slow average of the speaking level, so one loud word does not move it.
            speechDb = double.IsNaN(speechDb) ? levelDb : speechDb + 0.05 * (levelDb - speechDb);
            if (isDropped)
            {
                isDropped = false;
                yield return (MicrophoneEvent.Recovered, $"a voz voltou ao normal ({levelDb:0} dB)");
            }
            isLow = false;
        }
        else if (isVoice && spoken > TimeSpan.FromSeconds(3))
        {
            // Voice-like, but far under the usual level: a drop if it started mid-speech and lasts.
            if (!isLow)
            {
                isLow = true;
                lowSince = now;
            }
            if (!isDropped && lowSince - lastSpeech < TimeSpan.FromSeconds(1) && now - lowSince >= DropTime)
            {
                isDropped = true;
                yield return (MicrophoneEvent.Drop, $"a voz caiu de {speechDb:0} dB para {levelDb:0} dB no meio da fala");
            }
        }
        else
        {
            isLow = false; // a pause
        }
    }
}

/// <summary>What the log of events means, in plain words, most important first.</summary>
public static class MicrophoneDiagnosis
{
    public static IReadOnlyList<string> Conclude(IReadOnlyList<MicrophoneEvent> events)
    {
        var conclusions = new List<string>();
        int Count(string kind) => events.Count(found => found.Kind == kind);
        int volumeChanges = Count(MicrophoneEvent.Volume), stalls = Count(MicrophoneEvent.Stall), drops = Count(MicrophoneEvent.Drop);
        // A drop right after a volume change is explained by it; the others come from before Windows.
        int unexplained = events.Count(drop => drop.Kind == MicrophoneEvent.Drop
            && !events.Any(change => change.Kind == MicrophoneEvent.Volume && drop.At - change.At is { TotalSeconds: >= 0 and < 5 }));
        if (volumeChanges > 0)
        {
            conclusions.Add($"Algum programa mexeu no volume do microfone no Windows {Times(volumeChanges)}. É o ajuste automático de ganho de apps de chamada (Discord, Teams, navegador). O UFO pode segurar o volume no lugar.");
        }
        if (unexplained > 0)
        {
            conclusions.Add($"A voz caiu {Times(unexplained)} sem ninguém mexer no volume: a queda já vem do fone (microfone de haste mal encaixado, processamento do próprio fone ou sinal sem fio).");
        }
        if (stalls > 0)
        {
            conclusions.Add($"O som do microfone travou {Times(stalls)}: o sinal sem fio ou a porta USB do dongle falhou por um instante. Dongle perto do fone, longe de roteador e de portas USB 3 ocupadas, costuma resolver.");
        }
        if (Count(MicrophoneEvent.Device) > 0)
        {
            conclusions.Add("O fone ou a haste do microfone desconectou e voltou durante o uso: confira o encaixe da haste.");
        }
        if (conclusions.Count == 0)
        {
            conclusions.Add(events.Count == 0 ? "Nenhuma falha registrada ainda. Use o microfone normalmente; o UFO acompanha sozinho." : "Nenhuma falha de voz registrada até agora.");
        }
        return conclusions;

        static string Times(int count) => count == 1 ? "1 vez" : $"{count} vezes";
    }
}

/// <summary>
/// Follows the microphone while some program is using it: its Windows volume (and who else is
/// listening), and the signal's level and continuity. Only numbers are kept, never the sound.
/// </summary>
public sealed class MicrophoneWatch : IDisposable
{
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Timer poll;
    private readonly object gate = new();
    private readonly bool isAlwaysListening;
    private MMDevice? device;
    private WasapiRecorder? recorder;
    private MicrophoneAnalyzer analyzer = new();
    private HashSet<string> users = [];
    private float? lastVolume;
    private bool? lastMute;
    private double sumOfSquares;
    private int samples;
    private long lastPacketEnd; // QPC, 100 ns
    private double worstGap;

    /// <summary>Raised on a background thread.</summary>
    public event Action<MicrophoneEvent>? Happened;

    /// <summary>When set (0–1), the Windows volume of the microphone is put back whenever something changes it.</summary>
    public float? HeldVolume { get; set; }

    /// <summary>
    /// Starts holding the volume by itself the first time a program turns it down while the microphone
    /// is in use (the automatic gain of call apps does that, and the voice gets quiet for no reason).
    /// </summary>
    public bool HoldsWhenTurnedDown { get; set; }

    /// <summary>Level of the last tenth of a second in dBFS, while listening.</summary>
    public double LevelDb { get; private set; } = double.NegativeInfinity;

    public bool IsListening => recorder is not null;

    /// <summary>Other programs capturing the microphone right now.</summary>
    public IReadOnlyCollection<string> Users => users;

    public float? Volume => lastVolume;

    /// <param name="isAlwaysListening">Listen even when no other program uses the microphone (diagnostic runs).</param>
    public MicrophoneWatch(bool isAlwaysListening = false)
    {
        this.isAlwaysListening = isAlwaysListening;
        poll = new Timer(_ => Poll(), null, 500, 2000);
    }

    private void Poll()
    {
        lock (gate)
        {
            try
            {
                MMDevice? found = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
                    .FirstOrDefault(endpoint => endpoint.FriendlyName.Contains("AWPRO", StringComparison.OrdinalIgnoreCase));
                if (found?.ID != device?.ID)
                {
                    StopListening();
                    device = found;
                    lastVolume = null;
                    lastMute = null;
                    if (device is not null)
                    {
                        device.AudioEndpointVolume.OnVolumeNotification += _ => ThreadPool.QueueUserWorkItem(_ => CheckVolume());
                    }
                }
                if (device is null)
                {
                    return;
                }
                CheckVolume();

                var now = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                AudioSessionManager manager = device.AudioSessionManager;
                manager.RefreshSessions();
                for (int index = 0; index < manager.Sessions.Count; index++)
                {
                    AudioSessionControl session = manager.Sessions[index];
                    if (session.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive && session.GetProcessID is not 0 and var id && id != Environment.ProcessId)
                    {
                        now.Add(Process.GetProcessById((int)id).ProcessName);
                    }
                }
                foreach (string joined in now.Except(users))
                {
                    Raise(MicrophoneEvent.App, $"{joined} começou a usar o microfone");
                }
                foreach (string left in users.Except(now))
                {
                    Raise(MicrophoneEvent.App, $"{left} parou de usar o microfone");
                }
                users = now;

                if ((users.Count > 0 || isAlwaysListening) && recorder is null)
                {
                    StartListening();
                }
                else if (users.Count == 0 && !isAlwaysListening)
                {
                    StopListening();
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
            {
                StopListening(); // the device or a session went away mid-read; the next poll starts over
                device = null;
            }
        }
    }

    private void CheckVolume()
    {
        lock (gate)
        {
            try
            {
                if (device is null)
                {
                    return;
                }
                AudioEndpointVolume volume = device.AudioEndpointVolume;
                float level = volume.MasterVolumeLevelScalar;
                bool isMuted = volume.Mute;
                if (lastVolume is { } before && (Math.Abs(before - level) > 0.005f || lastMute != isMuted))
                {
                    if (HoldsWhenTurnedDown && HeldVolume is null && users.Count > 0 && level < before - 0.005f)
                    {
                        HeldVolume = before;
                    }
                    bool isHeld = HeldVolume is { } held && Math.Abs(held - level) > 0.005f;
                    Raise(MicrophoneEvent.Volume, lastMute != isMuted
                        ? isMuted ? "o microfone foi silenciado no Windows" : "o microfone voltou a ter som no Windows"
                        : $"o volume do microfone no Windows mudou de {before:P0} para {level:P0}{(isHeld ? "; o UFO colocou de volta" : "")}");
                }
                lastVolume = level;
                lastMute = isMuted;
                if (HeldVolume is { } wanted && Math.Abs(wanted - level) > 0.005f)
                {
                    lastVolume = wanted; // our own change is not news
                    volume.MasterVolumeLevelScalar = wanted;
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // the endpoint went away; the next poll finds it again
            }
        }
    }

    private void StartListening()
    {
        WasapiRecorder opened = new WasapiRecorderBuilder().WithDevice(device!).WithSharedMode().Build();
        analyzer = new MicrophoneAnalyzer();
        lastPacketEnd = 0;
        sumOfSquares = samples = 0;
        opened.DataAvailable += (buffer, flags, _, qpcPosition) =>
        {
            WaveFormat format = opened.WaveFormat;
            int bytesPerSample = format.BitsPerSample / 8, frames = buffer.Length / format.BlockAlign;
            // A gap is time between the end of the last packet and the start of this one.
            if (lastPacketEnd != 0)
            {
                worstGap = Math.Max(worstGap, (qpcPosition - lastPacketEnd) / 10000.0);
            }
            if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0 && lastPacketEnd != 0)
            {
                worstGap = Math.Max(worstGap, MicrophoneAnalyzer.StallMilliseconds);
            }
            lastPacketEnd = qpcPosition + frames * 10_000_000L / format.SampleRate;
            for (int at = 0; at + bytesPerSample <= buffer.Length; at += format.BlockAlign)
            {
                double sample = bytesPerSample == 4 ? BitConverter.ToSingle(buffer[at..]) : BitConverter.ToInt16(buffer[at..]) / 32768.0;
                sumOfSquares += sample * sample;
                samples++;
            }
            if (samples >= format.SampleRate / 10)
            {
                LevelDb = 10 * Math.Log10(Math.Max(sumOfSquares / samples, 1e-12));
                foreach ((string kind, string text) in analyzer.Feed(LevelDb, worstGap, clock.Elapsed))
                {
                    Raise(kind, text);
                }
                sumOfSquares = samples = 0;
                worstGap = 0;
            }
        };
        opened.StartRecording();
        recorder = opened;
    }

    private void StopListening()
    {
        try
        {
            recorder?.Dispose();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // the device was already gone
        }
        recorder = null;
        LevelDb = double.NegativeInfinity;
    }

    private void Raise(string kind, string text) => Happened?.Invoke(new MicrophoneEvent(DateTime.Now, kind, text));

    public void Dispose()
    {
        poll.Dispose();
        lock (gate)
        {
            StopListening();
        }
        enumerator.Dispose();
    }
}
