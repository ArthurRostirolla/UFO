using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ufo.Clips;
using Forms = System.Windows.Forms;

namespace Ufo;

/// <summary>
/// Clips screen: the replay buffer (always holding the last moments of the screen and sound), the
/// global hotkeys and automatic triggers that turn it into a clip, the on-screen notice, and the
/// gallery with a simple trimmer. Every choice applies and saves itself at once.
/// </summary>
public partial class MainWindow
{
    private const int ClipHotkeyId = 1, RecordHotkeyId = 2;
    private const string Desktop = "Área de trabalho";

    private readonly ClipAudio clipAudio = new();
    private readonly DispatcherTimer clipWatch = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer clipRestart = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly List<Action> clipRefreshers = [];
    private readonly Stopwatch clipClock = Stopwatch.StartNew();
    private readonly TriggerGate shoutGate = new(checksInARow: 1, ClipTriggers.Cooldown);
    private readonly TriggerGate laughterGate = new(checksInARow: 2, ClipTriggers.Cooldown); // a full second of laughing
    private LaughterDetector? laughter;
    private bool isHearingLaughter;
    private ClipRecorder clipRecorder = null!;
    private ClipOverlay? clipOverlay;
    private readonly List<ClipTicker> clipTickers = [];
    private string? clipGame;
    private TimeSpan lastInGame = TimeSpan.FromDays(-1), lastClipStart = TimeSpan.FromDays(-1), autoClipQuietUntil;
    private bool isClipBusy, isClipRestartPending;
    private string clipError = "", hotkeyError = "";
    private int capturingHotkey;
    private ClipFile? openClip;

    private WrapPanel clipGallery = null!;
    private TextBlock clipGalleryNote = null!;
    private StackPanel clipPlayer = null!;
    private MediaElement clipMedia = null!;
    private TextBox clipNameBox = null!;
    private Slider clipStartSlider = null!, clipEndSlider = null!;
    private TextBlock clipTrimText = null!;
    private Button clipPlayButton = null!;
    private bool isClipPlaying;

    private ClipSettings Clips => preferences.Clips ?? new ClipSettings();

    private void InitializeClips()
    {
        clipRecorder = new ClipRecorder(clipAudio);
        laughter = LaughterDetector.TryLoad(LaughterDetector.DefaultPath);
        BuildClipPanel();
        ShowClipRows();
        clipRestart.Tick += (_, _) =>
        {
            clipRestart.Stop();
            _ = SyncClipBufferAsync(isRestart: true);
        };
        clipWatch.Tick += (_, _) => OnClipWatch();
        clipWatch.Start();
        ShowClipState();
    }

    private void CloseClips()
    {
        clipWatch.Stop();
        clipRecorder.Dispose();
        clipAudio.Dispose();
        laughter?.Dispose();
        clipOverlay?.Close();
        ShowClipTickers([]);
    }

    /// <summary>Applies and saves a change. <paramref name="restarts"/>: the capture has to start over for it (it empties the buffer).</summary>
    private void ChangeClips(Func<ClipSettings, ClipSettings> change, bool restarts = false)
    {
        UpdatePreferences(preferences with { Clips = change(Clips) });
        clipAudio.SystemVolume = Clips.SystemVolume;
        clipAudio.MicrophoneVolume = Clips.MicrophoneVolume;
        clipRecorder.RetroSeconds = Clips.RetroSeconds;
        if (restarts)
        {
            clipError = "";
            clipRestart.Stop();
            clipRestart.Start(); // sliders settle first
        }
        ShowClipState();
    }

    // ── Buffer ──────────────────────────────────────────────────────────────────────────────────

    private bool IsClipBufferWanted => Clips.IsEnabled
        && (!Clips.OnlyInGames || clipClock.Elapsed - lastInGame < TimeSpan.FromSeconds(30)); // survives a short alt-tab

    /// <summary>Starts or stops the capture to match the settings. Starting probes encoders, so it runs off the UI thread.</summary>
    private async Task SyncClipBufferAsync(bool isRestart = false)
    {
        if (isClipBusy)
        {
            isClipRestartPending |= isRestart;
            return;
        }
        bool isWanted = IsClipBufferWanted;
        if (!isRestart)
        {
            // On its own (game came or went, ffmpeg died) it does not insist on something that just failed.
            if (isWanted == clipRecorder.IsRunning || (isWanted && (clipError.Length > 0 || clipClock.Elapsed - lastClipStart < TimeSpan.FromSeconds(10))))
            {
                return;
            }
        }
        isClipBusy = true;
        clipError = "";
        ShowClipState();
        ClipSettings settings = Clips;
        Forms.Screen[] screens = Forms.Screen.AllScreens;
        bool isAll = settings.Monitor == ClipSettings.AllMonitors && screens.Length > 1;
        // All monitors: side by side in the order they sit on the desk, as tall as the tallest.
        Forms.Screen[] captured = isAll ? [.. screens.OrderBy(screen => screen.Bounds.X)] : [ClipScreen()];
        var capture = new System.Drawing.Size(captured.Sum(screen => screen.Bounds.Width), captured.Max(screen => screen.Bounds.Height));
        ulong[] monitors = [.. captured.Select(screen => ClipRecorder.MonitorAt(screen.Bounds.X + screen.Bounds.Width / 2, screen.Bounds.Y + screen.Bounds.Height / 2))];
        try
        {
            ShowClipTickers(isWanted ? captured : []);
            if (isWanted)
            {
                lastClipStart = clipClock.Elapsed;
                await Task.Run(() => clipRecorder.Start(settings, capture.Width, capture.Height, monitors));
            }
            else
            {
                await Task.Run(clipRecorder.Stop);
            }
        }
        catch (InvalidOperationException ex)
        {
            clipError = ex.Message;
        }
        isClipBusy = false;
        ShowClipState();
        if (isClipRestartPending)
        {
            isClipRestartPending = false;
            await SyncClipBufferAsync(isRestart: true);
        }
    }

    /// <summary>Keeps one ticking pixel on each monitor being recorded (none when the buffer is off).</summary>
    private void ShowClipTickers(Forms.Screen[] screens)
    {
        clipTickers.ForEach(ticker => ticker.Close());
        clipTickers.Clear();
        foreach (Forms.Screen screen in screens)
        {
            var ticker = new ClipTicker(screen.Bounds);
            ticker.Show();
            clipTickers.Add(ticker);
        }
    }

    /// <summary>The chosen monitor; the first one when every monitor is recorded (it hosts the on-screen notice).</summary>
    private Forms.Screen ClipScreen()
    {
        Forms.Screen[] screens = Forms.Screen.AllScreens;
        return screens[Math.Clamp(Clips.Monitor, 0, screens.Length - 1)];
    }

    private void OnClipWatch()
    {
        FollowClipGame();
        _ = SyncClipBufferAsync();
        ShowClipState();
        if (isClipPlaying && clipMedia.Position.TotalSeconds >= clipEndSlider.Value)
        {
            PauseClip();
        }
        if (!clipRecorder.IsRunning || isClipBusy || clipClock.Elapsed < autoClipQuietUntil)
        {
            return;
        }
        float[] heard = clipAudio.LastMicrophoneSecond();
        if (Clips.ClipOnShout && shoutGate.Update(ClipTriggers.LevelDb(heard, 0.5), Clips.ShoutDb, clipClock.Elapsed))
        {
            _ = SaveClipAfterTriggerAsync("grito");
        }
        if (Clips.ClipOnLaughter && laughter is not null && !isHearingLaughter)
        {
            _ = HearLaughterAsync(heard);
        }
    }

    /// <summary>Asks the model about the last second of the microphone, off the UI thread.</summary>
    private async Task HearLaughterAsync(float[] heard)
    {
        isHearingLaughter = true;
        try
        {
            LaughterDetector detector = laughter!;
            double score = await Task.Run(() => detector.Score(heard));
            // Sensitivity 10–90% asks for a score of 0.9 down to 0.1.
            if (laughterGate.Update(score, 1 - Clips.LaughterSensitivity, clipClock.Elapsed) && clipClock.Elapsed >= autoClipQuietUntil)
            {
                _ = SaveClipAfterTriggerAsync("risada");
            }
        }
        catch (Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
        {
            // one bad inference is not worth a message; the next half second tries again
        }
        finally
        {
            isHearingLaughter = false;
        }
    }

    /// <summary>
    /// The game in front, if any: a full-screen program that is not the desktop, a browser or a player
    /// (same rule as the automatic equalizer profile). Its name files the clip.
    /// </summary>
    private void FollowClipGame()
    {
        nint window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint processId);
        string? name = null;
        try
        {
            name = processId == 0 ? null : Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // the process ended between the two calls
        }
        bool isGame = name is not null
            && !name.Equals("UFO", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
            && !VideoApps.Contains(name, StringComparer.OrdinalIgnoreCase)
            && IsFullScreen(window);
        clipGame = isGame ? name : null;
        if (isGame)
        {
            lastInGame = clipClock.Elapsed;
        }
    }

    private void ShowClipState()
    {
        bool isRunning = clipRecorder.IsRunning && !isClipBusy;
        string text =
            clipError.Length > 0 ? clipError
            : isClipBusy ? "Preparando a captura…"
            : isRunning ? $"Guardando os últimos {Span(Clips.RetroSeconds)} · {clipRecorder.Description}{(clipRecorder.IsRecording ? " · gravando" : "")}"
            : Clips.IsEnabled && Clips.OnlyInGames ? "Esperando um jogo em tela cheia."
            : $"Desligado. Ligue o replay e use {HotkeyText(Clips.ClipModifiers, Clips.ClipKey)} para guardar um clip.";
        if (hotkeyError.Length > 0)
        {
            text += " " + hotkeyError;
        }
        if (ClipStatusText.Text != text)
        {
            ClipStatusText.Text = text;
            ClipStatusText.SetResourceReference(ForegroundProperty, clipError.Length > 0 || hotkeyError.Length > 0 ? "ErrorBrush" : "MutedBrush");
            ClipStateDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, isRunning ? "AccentBrush" : "MutedBrush");
        }
        bool hasDot = isRunning && Clips.ShowOverlay && Clips.ShowRecordingDot;
        if (hasDot || clipOverlay is not null)
        {
            Overlay().ShowDot(hasDot);
        }
    }

    private ClipOverlay Overlay()
    {
        clipOverlay ??= new ClipOverlay();
        clipOverlay.MoveTo(Clips.OverlayCorner, ClipScreen().WorkingArea);
        return clipOverlay;
    }

    private void ToastClip(string title, string meta)
    {
        if (Clips.ShowOverlay)
        {
            Overlay().Toast(title, meta);
        }
    }

    // ── Saving ──────────────────────────────────────────────────────────────────────────────────

    private async Task SaveClipAsync(string trigger)
    {
        if (!clipRecorder.IsRunning || isClipBusy)
        {
            ToastClip("replay desligado", "ligue em clips");
            return;
        }
        string path = ClipLibrary.NewPath(clipGame ?? Desktop, trigger, DateTime.Now);
        ShowSavedClip(await clipRecorder.SaveClipAsync(Clips.RetroSeconds, path), "clip salvo", trigger);
    }

    /// <summary>Automatic triggers wait a little, so the laugh or shout itself ends up inside the clip.</summary>
    private async Task SaveClipAfterTriggerAsync(string trigger)
    {
        autoClipQuietUntil = clipClock.Elapsed + ClipTriggers.Cooldown;
        await Task.Delay(ClipTriggers.PostRoll);
        await SaveClipAsync(trigger);
    }

    private async Task ToggleClipRecordingAsync()
    {
        if (!clipRecorder.IsRunning || isClipBusy)
        {
            ToastClip("replay desligado", "ligue em clips");
        }
        else if (!clipRecorder.IsRecording)
        {
            clipRecorder.StartRecording();
            ToastClip("gravando", $"{HotkeyText(Clips.RecordModifiers, Clips.RecordKey)} para parar");
        }
        else
        {
            string path = ClipLibrary.NewPath(clipGame ?? Desktop, "gravação", DateTime.Now);
            ShowSavedClip(await clipRecorder.StopRecordingAsync(path), "gravação salva", "gravação");
        }
        ShowClipState();
    }

    private void ShowSavedClip(SavedClip? saved, string title, string trigger)
    {
        if (saved is null)
        {
            ToastClip("nada para salvar", "o buffer ainda está vazio");
            return;
        }
        ToastClip(title, $"{Span(saved.Seconds)} · {trigger}");
        double limit = Clips.StorageLimitGb;
        _ = Task.Run(() => ClipLibrary.EnforceLimit(limit));
        if (ClipsPanel.Visibility == Visibility.Visible)
        {
            ShowClipGallery();
        }
    }

    // ── Hotkeys ─────────────────────────────────────────────────────────────────────────────────

    private nint OnClipHotkeyMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int Hotkey = 0x0312;
        if (message != Hotkey)
        {
            return 0;
        }
        handled = true;
        _ = (int)wParam == ClipHotkeyId ? SaveClipAsync("atalho") : ToggleClipRecordingAsync();
        return 0;
    }

    /// <summary>(Re)registers the two global hotkeys; one taken by another program is reported in the status line.</summary>
    private void RegisterClipHotkeys()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0)
        {
            return;
        }
        const uint NoRepeat = 0x4000;
        NativeMethods.UnregisterHotKey(hwnd, ClipHotkeyId);
        NativeMethods.UnregisterHotKey(hwnd, RecordHotkeyId);
        var taken = new List<string>();
        foreach ((int id, uint modifiers, int key) in new[] { (ClipHotkeyId, Clips.ClipModifiers, Clips.ClipKey), (RecordHotkeyId, Clips.RecordModifiers, Clips.RecordKey) })
        {
            if (!NativeMethods.RegisterHotKey(hwnd, id, modifiers | NoRepeat, (uint)key))
            {
                taken.Add(HotkeyText(modifiers, key));
            }
        }
        hotkeyError = taken.Count == 0 ? "" : $"{string.Join(" e ", taken)} já está em uso por outro programa: escolha outro atalho.";
        ShowClipState();
    }

    private static string HotkeyText(uint modifiers, int virtualKey)
    {
        string key = KeyInterop.KeyFromVirtualKey(virtualKey).ToString();
        key = key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) ? key[1..] : key; // D1 → 1
        return string.Concat(
            (modifiers & ClipSettings.Control) != 0 ? "Ctrl+" : "",
            (modifiers & ClipSettings.Alt) != 0 ? "Alt+" : "",
            (modifiers & ClipSettings.Shift) != 0 ? "Shift+" : "",
            (modifiers & ClipSettings.Windows) != 0 ? "Win+" : "",
            key);
    }

    /// <summary>A row whose button listens for the next key combination. Esc or leaving the button cancels.</summary>
    private void AddHotkeyRow(Panel rows, string label, int id, Func<(uint Modifiers, int Key)> read, Func<ClipSettings, uint, int, ClipSettings> write)
    {
        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var button = new Button { Style = (Style)FindResource("QuietButton"), HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 140, ToolTip = "Clique e pressione a combinação de teclas" };
        System.Windows.Automation.AutomationProperties.SetName(button, label);
        void ShowKey() => button.Content = HotkeyText(read().Modifiers, read().Key);
        void EndCapture()
        {
            if (capturingHotkey == id)
            {
                capturingHotkey = 0;
                ShowKey();
                RegisterClipHotkeys();
            }
        }
        button.Click += (_, _) =>
        {
            // Released while listening, so pressing the current combination does not fire it.
            capturingHotkey = id;
            nint hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.UnregisterHotKey(hwnd, ClipHotkeyId);
            NativeMethods.UnregisterHotKey(hwnd, RecordHotkeyId);
            button.Content = "pressione as teclas…";
        };
        button.LostKeyboardFocus += (_, _) => EndCapture();
        button.PreviewKeyDown += (_, e) =>
        {
            if (capturingHotkey != id)
            {
                return;
            }
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            {
                e.Handled = true; // still holding modifiers
                return;
            }
            e.Handled = true;
            ModifierKeys held = Keyboard.Modifiers;
            // A bare letter would be taken away from every game and text box: function keys may go alone, the rest need a modifier.
            if (key != Key.Escape && (held != ModifierKeys.None || key is >= Key.F1 and <= Key.F24))
            {
                uint modifiers = (held.HasFlag(ModifierKeys.Control) ? ClipSettings.Control : 0)
                    | (held.HasFlag(ModifierKeys.Alt) ? ClipSettings.Alt : 0)
                    | (held.HasFlag(ModifierKeys.Shift) ? ClipSettings.Shift : 0)
                    | (held.HasFlag(ModifierKeys.Windows) ? ClipSettings.Windows : 0);
                ChangeClips(settings => write(settings, modifiers, KeyInterop.VirtualKeyFromKey(key)));
            }
            EndCapture();
        };
        clipRefreshers.Add(ShowKey);
        Grid.SetColumn(button, 1);
        grid.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(button);
        rows.Children.Add(grid);
    }

    // ── Panel ───────────────────────────────────────────────────────────────────────────────────

    private void BuildClipPanel()
    {
        Panel replay = AddClipSection("Replay");
        AddSwitchRow(replay, clipRefreshers, "Guardar os últimos momentos", () => Clips.IsEnabled,
            isOn => ChangeClips(settings => settings with { IsEnabled = isOn }, restarts: true),
            "Mantém os últimos momentos da tela e do som prontos para virar clip. Usa a placa de vídeo, quase nada do processador.");
        AddClipSlider(replay, "Tempo retroativo", ClipSettings.MinRetroSeconds, ClipSettings.MaxRetroSeconds, 15, seconds => Span((int)seconds),
            settings => settings.RetroSeconds, (settings, seconds) => settings with { RetroSeconds = (int)seconds });
        AddSwitchRow(replay, clipRefreshers, "Só durante jogos em tela cheia", () => Clips.OnlyInGames,
            isOn => ChangeClips(settings => settings with { OnlyInGames = isOn }, restarts: true));

        BuildClipGallery(AddClipSection("Galeria"));

        Panel video = AddClipSection("Vídeo");
        if (Forms.Screen.AllScreens.Length > 1)
        {
            AddClipChoice(video, "Monitor", [.. Forms.Screen.AllScreens.Select((_, index) => ($"{index + 1}", index)), ("Ambos", ClipSettings.AllMonitors)],
                settings => settings.Monitor, (settings, monitor) => settings with { Monitor = monitor }, decorate: ShowMonitorPreviewOnHover);
        }
        AddClipChoice(video, "Resolução", [("Nativa", 0), ("1440p", 1440), ("1080p", 1080), ("720p", 720)],
            settings => settings.Height, (settings, height) => settings with { Height = height });
        AddClipChoice(video, "Quadros por segundo", [("30", 30), ("60", 60), ("120", 120)],
            settings => settings.FramesPerSecond, (settings, fps) => settings with { FramesPerSecond = fps });
        AddClipChoice(video, "Qualidade", [("Baixa", 0), ("Média", 1), ("Alta", 2), ("Máxima", 3)],
            settings => settings.Quality, (settings, quality) => settings with { Quality = quality });
        AddClipChoice(video, "Formato", [("H.264", "h264"), ("HEVC", "hevc"), ("AV1", "av1")],
            settings => settings.Codec, (settings, codec) => settings with { Codec = codec });

        Panel sound = AddClipSection("Som");
        AddSliderRow(sound, clipRefreshers, "Som do computador", 0, 1, isPercent: true, () => Clips.SystemVolume,
            volume => ChangeClips(settings => settings with { SystemVolume = volume }));
        AddSliderRow(sound, clipRefreshers, "Microfone", 0, 1, isPercent: true, () => Clips.MicrophoneVolume,
            volume => ChangeClips(settings => settings with { MicrophoneVolume = volume }));
        AddClipSlider(sound, "Sincronia", -300, 300, 10, offset => offset == 0 ? "0 ms" : $"{offset:+0;-0} ms",
            settings => settings.SyncMilliseconds, (settings, offset) => settings with { SyncMilliseconds = (int)offset }, restarts: true,
            tip: "Se o som chega antes da imagem no clip, aumente; se chega depois, diminua.");

        Panel hotkeys = AddClipSection("Atalhos");
        AddHotkeyRow(hotkeys, "Guardar clip", ClipHotkeyId, () => (Clips.ClipModifiers, Clips.ClipKey),
            (settings, modifiers, key) => settings with { ClipModifiers = modifiers, ClipKey = key });
        AddHotkeyRow(hotkeys, "Gravar e parar", RecordHotkeyId, () => (Clips.RecordModifiers, Clips.RecordKey),
            (settings, modifiers, key) => settings with { RecordModifiers = modifiers, RecordKey = key });

        Panel triggers = AddClipSection("Clip automático");
        if (laughter is null)
        {
            triggers.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Hint"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
                Text = "O clip por risada precisa do modelo em yamnet\\yamnet.onnx ao lado do UFO (veja o README).",
            });
        }
        else
        {
            AddSwitchRow(triggers, clipRefreshers, "Ao rir no microfone", () => Clips.ClipOnLaughter,
                isOn => ChangeClips(settings => settings with { ClipOnLaughter = isOn }),
                "Um modelo de IA roda no próprio computador e reconhece risada na sua voz. Nada sai da máquina.");
            AddSliderRow(triggers, clipRefreshers, "Sensibilidade da risada", 0.1, 0.9, isPercent: true, () => Clips.LaughterSensitivity,
                sensitivity => ChangeClips(settings => settings with { LaughterSensitivity = sensitivity }),
                "Mais alta guarda risadinhas; mais baixa, só gargalhadas claras.");
        }
        AddSwitchRow(triggers, clipRefreshers, "Ao gritar no microfone", () => Clips.ClipOnShout,
            isOn => ChangeClips(settings => settings with { ClipOnShout = isOn }));
        AddSliderRow(triggers, clipRefreshers, "Volume do grito", -30, 0, isPercent: false, () => Clips.ShoutDb,
            level => ChangeClips(settings => settings with { ShoutDb = level }),
            "Quão alto o microfone precisa chegar. Mais perto de zero, só gritos de verdade.");

        Panel overlay = AddClipSection("Aviso na tela");
        AddSwitchRow(overlay, clipRefreshers, "Mostrar quando um clip é salvo", () => Clips.ShowOverlay,
            isOn => ChangeClips(settings => settings with { ShowOverlay = isOn }));
        AddSwitchRow(overlay, clipRefreshers, "Ponto enquanto o replay está ligado", () => Clips.ShowRecordingDot,
            isOn => ChangeClips(settings => settings with { ShowRecordingDot = isOn }));
        AddClipChoice(overlay, "Canto", [("Superior esquerdo", 0), ("Superior direito", 1), ("Inferior esquerdo", 2), ("Inferior direito", 3)],
            settings => settings.OverlayCorner, (settings, corner) => settings with { OverlayCorner = corner }, restarts: false);
    }

    private void ShowClipRows()
    {
        isShowingRows = true;
        clipRefreshers.ForEach(refresh => refresh());
        isShowingRows = false;
    }

    private Panel AddClipSection(string heading)
    {
        var rows = new StackPanel();
        rows.Children.Add(new TextBlock { Text = heading, Style = (Style)FindResource("SectionHeading") });
        ClipSections.Children.Add(new Border { Style = (Style)FindResource("Section"), Child = rows });
        return rows;
    }

    /// <summary>Label and a few named options; only one is on.</summary>
    private void AddClipChoice<T>(Panel rows, string label, (string Text, T Value)[] options, Func<ClipSettings, T> read,
        Func<ClipSettings, T, ClipSettings> write, bool restarts = true, Action<RadioButton, T>? decorate = null)
    {
        var grid = new Grid { MinHeight = 44 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        foreach ((string text, T value) in options)
        {
            var chip = new RadioButton { Style = (Style)FindResource("Chip"), GroupName = "Clip" + label, Content = text };
            chip.Checked += (_, _) =>
            {
                if (!isShowingRows)
                {
                    ChangeClips(settings => write(settings, value), restarts);
                }
            };
            clipRefreshers.Add(() => chip.IsChecked = Equals(read(Clips), value));
            decorate?.Invoke(chip, value);
            chips.Children.Add(chip);
        }
        Grid.SetColumn(chips, 1);
        grid.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(chips);
        rows.Children.Add(grid);
    }

    /// <summary>Hovering a monitor option shows what is on that screen right now, so "1" and "2" need no guessing.</summary>
    private static void ShowMonitorPreviewOnHover(RadioButton chip, int monitor)
    {
        var picture = new Image { Width = 360 };
        var caption = new TextBlock { FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        var preview = new StackPanel();
        preview.Children.Add(picture);
        preview.Children.Add(caption);
        chip.ToolTip = new ToolTip { Content = preview };
        ToolTipService.SetInitialShowDelay(chip, 150);
        ToolTipService.SetShowDuration(chip, 60000);
        chip.ToolTipOpening += (_, _) =>
        {
            Forms.Screen[] screens = Forms.Screen.AllScreens;
            Forms.Screen[] shown = monitor == ClipSettings.AllMonitors ? screens : [screens[Math.Clamp(monitor, 0, screens.Length - 1)]];
            System.Drawing.Rectangle area = shown.Select(screen => screen.Bounds).Aggregate(System.Drawing.Rectangle.Union);
            using var full = new System.Drawing.Bitmap(area.Width, area.Height);
            using (System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(full))
            {
                graphics.CopyFromScreen(area.Location, System.Drawing.Point.Empty, area.Size);
            }
            using var small = new System.Drawing.Bitmap(full, 720, Math.Max(1, 720 * area.Height / area.Width));
            using var stream = new MemoryStream();
            small.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            picture.Source = bitmap;
            caption.Text = string.Join("  +  ", shown.Select(screen => $"{screen.Bounds.Width}×{screen.Bounds.Height}{(screen.Primary ? " · principal" : "")}"));
        };
    }

    /// <summary>Like <see cref="AddSliderRow"/>, with the row's own step and wording for the value.</summary>
    private void AddClipSlider(Panel rows, string label, double minimum, double maximum, double step, Func<double, string> format,
        Func<ClipSettings, double> read, Func<ClipSettings, double, ClipSettings> write, bool restarts = false, string? tip = null)
    {
        var grid = new Grid { MinHeight = 44, ToolTip = tip };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        var slider = new Slider { Minimum = minimum, Maximum = maximum, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        var value = new TextBlock { Style = (Style)FindResource("Value") };
        slider.ValueChanged += (_, _) =>
        {
            value.Text = format(slider.Value);
            if (!isShowingRows)
            {
                ChangeClips(settings => write(settings, slider.Value), restarts);
            }
        };
        clipRefreshers.Add(() =>
        {
            slider.Value = read(Clips);
            value.Text = format(slider.Value);
        });
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(slider);
        grid.Children.Add(value);
        rows.Children.Add(grid);
    }

    /// <summary>"45 s", "2 min", "1 min 30 s".</summary>
    private static string Span(int seconds) =>
        seconds < 60 ? $"{seconds} s" : seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds / 60} min {seconds % 60} s";

    // ── Gallery ─────────────────────────────────────────────────────────────────────────────────

    private void BuildClipGallery(Panel rows)
    {
        clipGalleryNote = new TextBlock { Style = (Style)FindResource("Hint"), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed };
        clipGallery = new WrapPanel();

        clipMedia = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, ScrubbingEnabled = true, MaxHeight = 340, Stretch = Stretch.Uniform };
        clipMedia.MediaOpened += (_, _) =>
        {
            double length = clipMedia.NaturalDuration.HasTimeSpan ? clipMedia.NaturalDuration.TimeSpan.TotalSeconds : 0;
            isShowingRows = true;
            clipStartSlider.Maximum = clipEndSlider.Maximum = length;
            clipStartSlider.Value = 0;
            clipEndSlider.Value = length;
            isShowingRows = false;
            ShowClipTrim();
        };
        clipMedia.MediaEnded += (_, _) => PauseClip();
        clipNameBox = new TextBox { FontSize = 14, Margin = new Thickness(0, 12, 0, 4), ToolTip = "Nome do clip. Enter para renomear." };
        System.Windows.Automation.AutomationProperties.SetName(clipNameBox, "Nome do clip");
        clipNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                RenameOpenClip();
            }
        };

        clipStartSlider = TrimSlider("Início do corte");
        clipEndSlider = TrimSlider("Fim do corte");
        clipTrimText = new TextBlock { Style = (Style)FindResource("Value"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 12) };

        var actions = new WrapPanel();
        clipPlayButton = ClipAction("Reproduzir", () =>
        {
            if (isClipPlaying)
            {
                PauseClip();
            }
            else
            {
                if (clipMedia.Position.TotalSeconds >= clipEndSlider.Value - 0.2 || clipMedia.Position.TotalSeconds < clipStartSlider.Value)
                {
                    clipMedia.Position = TimeSpan.FromSeconds(clipStartSlider.Value);
                }
                clipMedia.Play();
                isClipPlaying = true;
                clipPlayButton.Content = "Pausar";
            }
        });
        actions.Children.Add(clipPlayButton);
        actions.Children.Add(ClipAction("Salvar corte", TrimOpenClip, "Guarda só o trecho entre início e fim como um clip novo"));
        actions.Children.Add(ClipAction("Copiar", () =>
        {
            if (openClip is { } clip)
            {
                Clipboard.SetFileDropList([clip.Path]); // pastes straight into Discord, WhatsApp, Explorer
                ShowGalleryNote("Copiado. Cole onde quiser enviar.");
            }
        }, "Copia o arquivo para colar em outro programa"));
        actions.Children.Add(ClipAction("Mostrar na pasta", () =>
        {
            if (openClip is { } clip)
            {
                Process.Start("explorer.exe", $"/select,\"{clip.Path}\"");
            }
        }));
        actions.Children.Add(ClipAction("Apagar", DeleteOpenClip, "Manda o clip para a Lixeira"));
        actions.Children.Add(ClipAction("Fechar", () => OpenClip(null)));

        clipPlayer = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 24) };
        clipPlayer.Children.Add(new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(4), Child = clipMedia });
        clipPlayer.Children.Add(clipNameBox);
        clipPlayer.Children.Add(clipStartSlider);
        clipPlayer.Children.Add(clipEndSlider);
        clipPlayer.Children.Add(clipTrimText);
        clipPlayer.Children.Add(actions);

        var folder = ClipAction("Abrir pasta dos clips", () =>
        {
            Directory.CreateDirectory(ClipLibrary.Root);
            Process.Start("explorer.exe", $"\"{ClipLibrary.Root}\"");
        });
        folder.HorizontalAlignment = HorizontalAlignment.Left;
        folder.Margin = new Thickness(0, 4, 0, 0);

        rows.Children.Add(clipPlayer);
        rows.Children.Add(clipGalleryNote);
        rows.Children.Add(clipGallery);
        AddClipSlider(rows, "Limite de espaço", 0, 200, 5, gigabytes => gigabytes <= 0 ? "sem limite" : $"{gigabytes:0} GB",
            settings => settings.StorageLimitGb, (settings, gigabytes) => settings with { StorageLimitGb = gigabytes },
            tip: "Ao passar do limite, os clips mais antigos são apagados quando um novo é salvo.");
        rows.Children.Add(folder);
    }

    private Slider TrimSlider(string name)
    {
        var slider = new Slider { Minimum = 0, Margin = new Thickness(0, 4, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(slider, name);
        slider.ToolTip = name;
        slider.ValueChanged += (_, _) =>
        {
            if (isShowingRows)
            {
                return;
            }
            // The two ends never cross, and the picture follows the end being moved.
            if (clipStartSlider.Value > clipEndSlider.Value)
            {
                (slider == clipStartSlider ? clipEndSlider : clipStartSlider).Value = slider.Value;
            }
            PauseClip();
            clipMedia.Position = TimeSpan.FromSeconds(slider.Value);
            ShowClipTrim();
        };
        return slider;
    }

    private Button ClipAction(string text, Action act, string? tip = null)
    {
        var button = new Button { Style = (Style)FindResource("QuietButton"), Content = text, Margin = new Thickness(0, 0, 8, 8), ToolTip = tip };
        button.Click += (_, _) => act();
        return button;
    }

    /// <summary>One line of feedback above the gallery; takes no room while empty.</summary>
    private void ShowGalleryNote(string text)
    {
        clipGalleryNote.Text = text;
        clipGalleryNote.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowClipTrim() =>
        clipTrimText.Text = $"{clipStartSlider.Value:0.0} s → {clipEndSlider.Value:0.0} s · {clipEndSlider.Value - clipStartSlider.Value:0.0} s";

    private void PauseClip()
    {
        clipMedia.Pause();
        isClipPlaying = false;
        clipPlayButton.Content = "Reproduzir";
    }

    /// <summary>Lists the newest clips. Thumbnails arrive as ffmpeg makes them.</summary>
    private async void ShowClipGallery()
    {
        List<ClipFile> clips = await Task.Run(ClipLibrary.List);
        clipGallery.Children.Clear();
        if (clips.Count == 0)
        {
            ShowGalleryNote("Nenhum clip ainda. Eles aparecem aqui e ficam em Vídeos\\UFO.");
        }
        else if (clipGalleryNote.Text.StartsWith("Nenhum", StringComparison.Ordinal))
        {
            ShowGalleryNote("");
        }
        const int Shown = 60; // ponytail: newest 60 only; the folder button reaches the rest. Page it if libraries grow.
        foreach (ClipFile clip in clips.Take(Shown))
        {
            var picture = new Image { Stretch = Stretch.UniformToFill, Width = 160, Height = 90, Clip = new RectangleGeometry(new Rect(0, 0, 160, 90), 4, 4) };
            var frame = new Border { Width = 160, Height = 90, CornerRadius = new CornerRadius(4), Child = picture };
            frame.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            var card = new StackPanel { Width = 160 };
            card.Children.Add(frame);
            card.Children.Add(new TextBlock { Text = clip.Game, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) });
            card.Children.Add(new TextBlock
            {
                Style = (Style)FindResource("Kicker"),
                Text = $"{clip.Created:dd/MM HH:mm}{(clip.Trigger.Length > 0 ? " · " + clip.Trigger : "")} · {clip.Bytes / 1048576.0:0} MB",
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var button = new Button { Style = (Style)FindResource("BareButton"), Content = card, Margin = new Thickness(0, 0, 12, 12), ToolTip = clip.Name };
            button.SetResourceReference(ForegroundProperty, "InkBrush");
            System.Windows.Automation.AutomationProperties.SetName(button, clip.Name);
            button.Click += (_, _) => OpenClip(clip);
            clipGallery.Children.Add(button);
            _ = ShowThumbnailAsync(picture, clip);
        }
    }

    private static async Task ShowThumbnailAsync(Image picture, ClipFile clip)
    {
        if (await ClipLibrary.ThumbnailAsync(clip) is not { } path)
        {
            return;
        }
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad; // no lock on the file
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        picture.Source = bitmap;
    }

    /// <summary>Opens a clip in the player above the gallery; null closes the player and lets go of the file.</summary>
    private void OpenClip(ClipFile? clip)
    {
        PauseClip();
        clipMedia.Close();
        clipMedia.Source = null;
        openClip = clip;
        clipPlayer.Visibility = clip is null ? Visibility.Collapsed : Visibility.Visible;
        if (clip is null)
        {
            return;
        }
        ShowGalleryNote("");
        clipNameBox.Text = clip.Name;
        clipMedia.Source = new Uri(clip.Path);
        clipMedia.Play(); // shows the first frame
        clipMedia.Pause();
        clipPlayer.BringIntoView();
    }

    private async void TrimOpenClip()
    {
        if (openClip is not { } clip || clipEndSlider.Value - clipStartSlider.Value < 0.5)
        {
            ShowGalleryNote("Escolha um trecho de pelo menos meio segundo.");
            return;
        }
        string? trimmed = await ClipLibrary.TrimAsync(clip, TimeSpan.FromSeconds(clipStartSlider.Value), TimeSpan.FromSeconds(clipEndSlider.Value));
        ShowGalleryNote(trimmed is null ? "Não foi possível cortar o clip." : $"Corte salvo como {Path.GetFileNameWithoutExtension(trimmed)}.");
        ShowClipGallery();
    }

    private void RenameOpenClip()
    {
        if (openClip is not { } clip)
        {
            return;
        }
        string name = clipNameBox.Text;
        OpenClip(null); // the player holds the file open
        try
        {
            string renamed = ClipLibrary.Rename(clip, name);
            OpenClip(clip with { Path = renamed });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowGalleryNote($"Não foi possível renomear: {ex.Message}");
        }
        ShowClipGallery();
    }

    private void DeleteOpenClip()
    {
        if (openClip is not { } clip)
        {
            return;
        }
        OpenClip(null);
        try
        {
            ClipLibrary.Delete(clip);
            ShowGalleryNote("Clip enviado para a Lixeira.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowGalleryNote($"Não foi possível apagar: {ex.Message}");
        }
        ShowClipGallery();
    }
}
