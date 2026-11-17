using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ufo.Audio;

namespace Ufo;

/// <summary>
/// Microphone diagnosis: while some program uses the microphone, UFO follows its Windows volume and
/// its signal, writes down every failure (voice dropping, audio stalling, volume being changed) and
/// says in plain words what they point to. One fix is applied by itself: when a program turns the
/// microphone volume down during a call, UFO puts it back and keeps it there.
/// The boom microphone is checked every two seconds during a call: when it drops out, a notice shows
/// on screen and the voice is raised by what UFO has measured it loses without the boom.
/// </summary>
public partial class MainWindow
{
    private const int KeptEvents = 500;
    private static readonly string MicLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UFO", "microfone.log");

    private readonly List<MicrophoneEvent> micEvents = [];
    private readonly DispatcherTimer micWatchTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MicrophoneWatch micWatch = null!;
    private bool? wasBoomAttached, wasLinkedForMic;
    private BoomCompensation boomCompensation = new();
    private double appliedBoomDb;
    private bool isAskingBoom;
    private int micWatchTicks;

    private void InitializeMicWatch()
    {
        micEvents.AddRange(ReadMicLog());
        micWatch = new MicrophoneWatch { HeldVolume = (float?)preferences.MicHeldVolume, HoldsWhenTurnedDown = true };
        micWatch.Happened += found => Dispatcher.BeginInvoke(() => AddMicEvent(found));
        boomCompensation = new BoomCompensation(preferences.MicWithBoomDb, preferences.MicWithoutBoomDb);
        micWatchTimer.Tick += async (_, _) =>
        {
            ShowMicWatchState();
            LearnBoomCompensation();
            // During a call the boom is asked about every two seconds (the regular poll is every ten).
            if (micWatch.Users.Count > 0 && ++micWatchTicks % 2 == 0 && !isAskingBoom)
            {
                isAskingBoom = true;
                bool? isBoomAttached = await Task.Run(headset.IsBoomMicAttached);
                isAskingBoom = false;
                NoteHeadsetForMicWatch(null, isBoomAttached);
            }
        };
        micWatchTimer.Start();
        ShowMicWatch();
    }

    private void CloseMicWatch()
    {
        micWatchTimer.Stop();
        micWatch.Dispose();
    }

    /// <summary>The log on disk is one event per line: time, kind, text, separated by tabs.</summary>
    private static List<MicrophoneEvent> ReadMicLog()
    {
        var events = new List<MicrophoneEvent>();
        try
        {
            foreach (string line in File.Exists(MicLogPath) ? File.ReadLines(MicLogPath).TakeLast(KeptEvents) : [])
            {
                string[] parts = line.Split('\t', 3);
                if (parts.Length == 3 && DateTime.TryParse(parts[0], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime at))
                {
                    events.Add(new MicrophoneEvent(at, parts[1], parts[2]));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // an unreadable log only means starting with a clean slate
        }
        return events;
    }

    private void AddMicEvent(MicrophoneEvent found)
    {
        micEvents.Add(found);
        if (micEvents.Count > KeptEvents)
        {
            micEvents.RemoveAt(0);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MicLogPath) ?? ".");
            File.AppendAllText(MicLogPath, $"{found.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}\t{found.Kind}\t{found.Text}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the log is a convenience; the event still shows on screen
        }
        // The watch may have started holding the volume by itself: remember it.
        if ((double?)micWatch.HeldVolume != preferences.MicHeldVolume)
        {
            UpdatePreferences(preferences with { MicHeldVolume = micWatch.HeldVolume });
        }
        ShowMicWatch();
    }

    /// <summary>Headset facts from the regular poll: the link or the boom microphone dropping during a call is a finding.</summary>
    private void NoteHeadsetForMicWatch(bool? isLinked, bool? isBoomAttached)
    {
        bool isInUse = micWatch.Users.Count > 0;
        if (isInUse && wasLinkedForMic == true && isLinked == false)
        {
            AddMicEvent(new MicrophoneEvent(DateTime.Now, MicrophoneEvent.Device, "o fone perdeu a conexão com o dongle"));
        }
        if (isInUse && wasBoomAttached is { } before && isBoomAttached is { } now && before != now)
        {
            AddMicEvent(new MicrophoneEvent(DateTime.Now, MicrophoneEvent.Device, now ? "a haste do microfone foi reconhecida de novo" : "a haste do microfone deixou de ser reconhecida"));
            Overlay().Toast(now ? "haste de volta" : "a haste do microfone caiu", now ? "microfone normal" : "confira o encaixe");
        }
        wasLinkedForMic = isLinked ?? wasLinkedForMic;
        wasBoomAttached = isBoomAttached ?? wasBoomAttached;
        ApplyBoomCompensation();
        if (isBoomAttached is { } isAttached)
        {
            BoomMicText.Text = isAttached ? "Conectado" : "Desconectado";
        }
    }

    /// <summary>Once a second during a call: how loud the voice is with the boom, and without it.</summary>
    private void LearnBoomCompensation()
    {
        if (!micWatch.IsListening || wasBoomAttached is not { } isAttached || double.IsNegativeInfinity(micWatch.LevelDb))
        {
            return;
        }
        double before = boomCompensation.GainDb;
        boomCompensation.Learn(isAttached, micWatch.IsVoice, micWatch.LevelDb, micWatch.SpeechDb, appliedBoomDb);
        // Saved when it moves by half a decibel, not every second.
        if (Math.Abs((boomCompensation.WithBoomDb ?? 0) - (preferences.MicWithBoomDb ?? 0)) > 0.5
            || Math.Abs((boomCompensation.WithoutBoomDb ?? 0) - (preferences.MicWithoutBoomDb ?? 0)) > 0.5)
        {
            UpdatePreferences(preferences with { MicWithBoomDb = boomCompensation.WithBoomDb, MicWithoutBoomDb = boomCompensation.WithoutBoomDb });
        }
        if (Math.Abs(boomCompensation.GainDb - before) > 0.5)
        {
            ApplyBoomCompensation();
            ShowMicWatch();
        }
    }

    /// <summary>Raises the microphone through the M274 while the boom is out during a call; back to normal otherwise.</summary>
    private void ApplyBoomCompensation()
    {
        double wanted = wasBoomAttached == false && micWatch.Users.Count > 0 ? Math.Round(boomCompensation.GainDb, 1) : 0;
        if (Math.Abs(wanted - appliedBoomDb) < 0.5 && (wanted > 0) == (appliedBoomDb > 0))
        {
            return;
        }
        bool isStarting = appliedBoomDb <= 0 && wanted > 0;
        appliedBoomDb = wanted;
        SendVoiceToM274();
        if (isStarting)
        {
            AddMicEvent(new MicrophoneEvent(DateTime.Now, MicrophoneEvent.Device, $"o UFO aumentou o microfone em {wanted:0} dB enquanto a haste está fora"));
        }
        ShowMicWatch();
    }

    private void ShowMicWatchState()
    {
        string users = string.Join(", ", micWatch.Users);
        MicWatchStateText.Text = micWatch.IsListening
            ? $"Acompanhando: {users} está usando o microfone · nível {(double.IsNegativeInfinity(micWatch.LevelDb) ? "—" : $"{micWatch.LevelDb:0} dB")} · volume no Windows {micWatch.Volume:P0}"
            : "Nenhum programa está usando o microfone agora. O UFO começa a acompanhar sozinho quando uma chamada começar.";
    }

    private void ShowMicWatch()
    {
        ShowMicWatchState();
        // Only what happened in the last week counts: an old problem that went away should stop being reported.
        List<MicrophoneEvent> recent = [.. micEvents.Where(found => found.At > DateTime.Now.AddDays(-7))];
        MicWatchConclusions.Children.Clear();
        foreach (string conclusion in MicrophoneDiagnosis.Conclude(recent))
        {
            MicWatchConclusions.Children.Add(new TextBlock { Text = conclusion, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        }
        MicBoomText.Text = boomCompensation.GainDb > 0
            ? $"Sem a haste a sua voz chega {boomCompensation.GainDb:0} dB mais baixa. Quando a haste cair durante uma chamada, o UFO aumenta o microfone nessa medida até ela voltar.{(appliedBoomDb > 0 ? " Compensando agora." : "")}"
            : boomCompensation.WithoutBoomDb is null
                ? "Ainda não medi a sua voz sem a haste. A compensação começa sozinha depois da primeira queda em que você estiver falando."
                : "Sem a haste a sua voz chega quase no mesmo volume: não há o que compensar.";
        MicHoldVolumeCheck.IsChecked = micWatch.HeldVolume is not null;
        MicHoldVolumeText.Text = micWatch.HeldVolume is { } held ? $"Segurar o volume do microfone em {held:P0}" : "Segurar o volume do microfone";
        MicWatchLogText.Text = string.Join(Environment.NewLine, recent
            .Where(found => found.Kind != MicrophoneEvent.App)
            .TakeLast(8).Reverse()
            .Select(found => $"{found.At:dd/MM HH:mm:ss}  {found.Text}"));
        MicWatchLogText.Visibility = MicWatchLogText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnMicHoldVolumeClick(object sender, RoutedEventArgs e)
    {
        micWatch.HeldVolume = MicHoldVolumeCheck.IsChecked == true ? micWatch.Volume ?? 1 : null;
        UpdatePreferences(preferences with { MicHeldVolume = micWatch.HeldVolume });
        ShowMicWatch();
    }

    private void OnMicLogClearClick(object sender, RoutedEventArgs e)
    {
        micEvents.Clear();
        try
        {
            File.Delete(MicLogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still cleared on screen
        }
        ShowMicWatch();
    }
}
