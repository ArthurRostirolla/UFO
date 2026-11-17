using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Ufo.Audio;

namespace Ufo;

/// <summary>
/// Equalizer screen: sound profiles, presets, a parametric curve edited by dragging points over a
/// live spectrum, and the ten simple sliders when the curve is the plain graphic one.
/// Every change goes to the UFO Engine at once; "Salvar" decides what is kept.
/// </summary>
public partial class MainWindow
{
    private const double MinFrequency = 20, MaxFrequency = 20000;
    private const double GraphRangeDb = 15;      // the graph shows ±15 dB; gains are limited to ±12
    private const int CurvePoints = 200;
    private const double PointHitRadius = 16;

    private static readonly string[] DefaultProfileNames = ["Padrão", "Jogo", "Música", "Filme"];
    private static readonly (BandKind Kind, string Label)[] BandKinds =
        [(BandKind.Peak, "Pico"), (BandKind.LowShelf, "Graves"), (BandKind.HighShelf, "Agudos"), (BandKind.HighPass, "Corte")];

    private readonly DispatcherTimer eqDebounce = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer profileWatch = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly List<Slider> bandSliders = [];
    private readonly List<TextBlock> bandGainTexts = [];
    private readonly EngineClient engine = new();
    private readonly HeadsetAudio headsetAudio = new();

    private List<SoundProfile> profiles = [];
    private int activeProfile;
    private EqualizerSettings equalizer = Equalizer.Default;
    private SoundSettings sound = new();
    private bool isShowingEqualizer, isComparing, isDraggingBand;
    private int selectedBand = -1;
    private int baseProfile;                 // the profile picked by hand, where automatic switching returns to
    private bool isAutoProfile = true, isAutoSwitching, isHoldingManualPick;
    private string? heldSituation;

    private static readonly string[] VideoApps =
        ["chrome", "msedge", "firefox", "opera", "brave", "vlc", "mpc-hc64", "mpc-hc", "mpv", "PotPlayerMini64", "ApplicationFrameHost"];

    private static readonly Dictionary<string, string> ProfileTips = new()
    {
        ["Padrão"] = "Uso geral: som neutro. É para onde a troca automática volta quando nada especial está acontecendo.",
        ["Jogo"] = "Realça passos e vozes, segura explosões. Entra sozinho com um jogo em tela cheia.",
        ["Música"] = "O som do fone sem coloração, para ouvir todas as notas; a análise de playlist ajusta a partir daí. Entra sozinho quando um player de música está tocando.",
        ["Filme"] = "Diálogos claros e impacto nos graves, sem sustos de volume. Entra sozinho com vídeo em tela cheia.",
    };

    private static readonly Dictionary<string, string> PresetTips = new()
    {
        ["Plano"] = "Sem alteração: o som como o fone entrega.",
        ["Graves"] = "Mais peso nos graves.",
        ["FPS"] = "Menos graves, mais 1–8 kHz: passos, recargas e vozes ficam mais fáceis de localizar.",
        ["Voz"] = "Destaca a faixa da fala: chamadas, podcasts, aulas.",
        ["Música"] = "Leve \"sorriso\": graves e agudos um pouco acima do meio.",
        ["Cinema"] = "Graves fortes e agudos com brilho, para filmes e séries.",
        [Equalizer.CustomPresetName] = "A curva que você desenhou. Fica marcado sozinho quando você mexe em um ponto.",
    };

    private static readonly Dictionary<BandKind, string> BandKindTips = new()
    {
        [BandKind.Peak] = "Sobe ou desce só em volta da frequência do ponto.",
        [BandKind.LowShelf] = "Sobe ou desce tudo abaixo da frequência do ponto.",
        [BandKind.HighShelf] = "Sobe ou desce tudo acima da frequência do ponto.",
        [BandKind.HighPass] = "Corta tudo abaixo da frequência do ponto.",
    };

    private void InitializeEqualizer()
    {
        SavedProfile? saved = ProfileStore.Load();
        profiles = saved?.Profiles is { Count: > 0 } list
            ? list
            : DefaultProfileNames.Select((name, index) => index == 0 && saved is not null
                ? Recommended(name, null) with { Equalizer = saved.Equalizer } // keep the curve saved before profiles existed
                : Recommended(name, null)).ToList();
        activeProfile = baseProfile = Math.Max(0, profiles.FindIndex(profile => profile.Name == saved?.ActiveProfile));
        isAutoProfile = saved?.IsAutoProfile ?? true;
        AutoProfileCheck.IsChecked = isAutoProfile;
        equalizer = profiles[activeProfile].Equalizer;
        sound = profiles[activeProfile].Sound;

        foreach (double frequency in Equalizer.GraphicFrequencies)
        {
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Height = 120,
                Minimum = -Equalizer.MaxGain,
                Maximum = Equalizer.MaxGain,
                TickFrequency = 0.5,
                IsSnapToTickEnabled = true,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            System.Windows.Automation.AutomationProperties.SetName(slider, $"{frequency:0} Hz");
            slider.ValueChanged += OnBandSliderChanged;
            var gainText = new TextBlock { Style = (Style)FindResource("Value"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
            var frequencyText = new TextBlock
            {
                Style = (Style)FindResource("Kicker"),
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Text = FrequencyLabel(frequency),
            };
            var column = new StackPanel();
            column.Children.Add(gainText);
            column.Children.Add(slider);
            column.Children.Add(frequencyText);
            EqBandsGrid.Children.Add(column);
            bandSliders.Add(slider);
            bandGainTexts.Add(gainText);
        }

        foreach (string presetName in Equalizer.Presets.Keys.Append(Equalizer.CustomPresetName))
        {
            var chip = new RadioButton { Content = presetName, Tag = presetName, GroupName = "EqPreset", Style = (Style)FindResource("Chip"), ToolTip = PresetTips.GetValueOrDefault(presetName) };
            chip.Checked += OnEqPresetChecked;
            EqPresetPanel.Children.Add(chip);
        }
        foreach ((BandKind kind, string label) in BandKinds)
        {
            var chip = new RadioButton { Content = label, Tag = kind, GroupName = "BandKind", Style = (Style)FindResource("Chip"), Margin = new Thickness(0, 0, 14, 0), ToolTip = BandKindTips[kind] };
            chip.Checked += OnBandKindChecked;
            BandKindPanel.Children.Add(chip);
        }
        BuildProfileChips();

        eqDebounce.Tick += (_, _) =>
        {
            eqDebounce.Stop();
            SendToEngine();
        };
        profileWatch.Tick += (_, _) => FollowActivity();
        profileWatch.Start();
        headsetAudio.VolumeChanged += () => Dispatcher.BeginInvoke(ScheduleEngineSend); // loudness follows the volume

        InitializeSound();
        InitializeVoice();
        InitializeAnalysis();
        ShowAdvancedState();
        ShowEqualizer();
        SendToEngine(); // put the saved setup back in the engine, discarding any unsaved one left from last time
        ShowEngineState();
    }

    // ── Profiles ────────────────────────────────────────────────────────────────────────────────

    private void BuildProfileChips()
    {
        EqProfilePanel.Children.Clear();
        for (int index = 0; index < profiles.Count; index++)
        {
            int profileIndex = index;
            var chip = new RadioButton
            {
                Content = profiles[index].Name,
                GroupName = "SoundProfile",
                Style = (Style)FindResource("SegOption"),
                IsChecked = index == activeProfile,
                ToolTip = ProfileTips.GetValueOrDefault(profiles[index].Name),
            };
            chip.Checked += (_, _) => SwitchProfile(profileIndex);
            EqProfilePanel.Children.Add(chip);
        }
    }

    /// <summary>Keeps the working copy in the list, then makes another profile the working one.</summary>
    private void SwitchProfile(int index)
    {
        if (isShowingEqualizer || index == activeProfile)
        {
            return;
        }
        if (!isAutoSwitching)
        {
            // Picked by hand: this is the new resting profile, and it holds until the situation changes.
            baseProfile = index;
            isHoldingManualPick = true;
            heldSituation = null;
            AutoProfileText.Text = "";
        }
        StoreActiveProfile();
        activeProfile = index;
        equalizer = profiles[index].Equalizer;
        sound = profiles[index].Sound;
        selectedBand = -1;
        ShowEqualizer();
        ShowSound();
        ScheduleEngineSend();
    }

    private void StoreActiveProfile() =>
        profiles[activeProfile] = profiles[activeProfile] with { Equalizer = equalizer, Sound = sound };

    private void OnProfileAppsChanged(object sender, RoutedEventArgs e)
    {
        profiles[activeProfile] = profiles[activeProfile] with { Apps = ProfileAppsBox.Text.Trim() };
        RefreshSaveState();
    }

    private void OnAutoProfileClick(object sender, RoutedEventArgs e)
    {
        isAutoProfile = AutoProfileCheck.IsChecked == true;
        AutoProfileText.Text = "";
        RefreshSaveState();
    }

    /// <summary>Puts the tuned setup back on the active profile; the ear calibration is kept.</summary>
    private void OnRecommendedClick(object sender, RoutedEventArgs e)
    {
        SoundProfile recommended = Recommended(profiles[activeProfile].Name, sound.Correction);
        equalizer = recommended.Equalizer;
        sound = recommended.Sound;
        if (profiles[activeProfile].Apps.Length == 0)
        {
            profiles[activeProfile] = profiles[activeProfile] with { Apps = recommended.Apps };
        }
        selectedBand = -1;
        ShowEqualizer();
        ShowSound();
        ScheduleEngineSend();
    }

    /// <summary>The tuned starting point of each built-in profile.</summary>
    private static SoundProfile Recommended(string name, IReadOnlyList<EqBand>? correction) => name switch
    {
        "Jogo" => new(name, Equalizer.FromPreset("FPS"), new SoundSettings(Footsteps: 0.5, Night: 0.25, Correction: correction)),
        // Music starts from the headphone's own sound: every note audible, nothing coloured. The
        // playlist analysis and the style chips add from there, by choice.
        "Música" => new(name, Equalizer.Default, new SoundSettings(Correction: correction), "spotify, tidal, deezer, foobar2000, musicbee, AppleMusic"),
        "Filme" => new(name, Equalizer.FromPreset("Cinema"), new SoundSettings(Night: 0.3, Correction: correction), "vlc, mpc-hc64, mpv"),
        _ => new(name, Equalizer.Default, new SoundSettings(Correction: correction)),
    };

    /// <summary>
    /// Automatic profile, in order: a program listed by a profile is in front; something is in
    /// full screen (a browser or player means a film, anything else a game); a listed program is
    /// the one making sound on the headset; otherwise the profile the user picked by hand.
    /// A manual pick holds until the situation changes.
    /// </summary>
    private void FollowActivity()
    {
        if (!isAutoProfile || isCalibrating || isAnalyzing)
        {
            return;
        }
        nint window = GetForegroundWindow();
        GetWindowThreadProcessId(window, out uint processId);
        string? foreground;
        try
        {
            foreground = processId == 0 ? null : Process.GetProcessById((int)processId).ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return; // the process ended between the two calls
        }
        if (foreground is null || foreground.Equals("UFO", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        int target = ProfileListing(foreground);
        string reason = foreground;
        if (target < 0 && !foreground.Equals("explorer", StringComparison.OrdinalIgnoreCase) && IsFullScreen(window))
        {
            // ponytail: full screen that is not a browser/player counts as a game (a slideshow would too);
            // list the program in another profile to override.
            bool isVideo = VideoApps.Contains(foreground, StringComparer.OrdinalIgnoreCase);
            target = profiles.FindIndex(profile => profile.Name == (isVideo ? "Filme" : "Jogo"));
            reason = $"{foreground} em tela cheia";
        }
        if (target < 0)
        {
            foreach (string playing in headsetAudio.AudibleProcesses())
            {
                target = ProfileListing(playing);
                if (target >= 0)
                {
                    reason = $"{playing} tocando";
                    break;
                }
            }
        }
        if (target < 0)
        {
            target = baseProfile;
            reason = "";
        }

        string situation = $"{target}|{reason}";
        if (isHoldingManualPick)
        {
            heldSituation ??= situation;
            if (situation == heldSituation)
            {
                return;
            }
            isHoldingManualPick = false;
        }
        AutoProfileText.Text = reason.Length == 0 ? "" : $"Ativado por {reason}.";
        if (target != activeProfile)
        {
            isAutoSwitching = true;
            ((RadioButton)EqProfilePanel.Children[target]).IsChecked = true;
            isAutoSwitching = false;
        }
    }

    private int ProfileListing(string processName) => profiles.FindIndex(profile => profile.Apps
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Any(app => processName.Equals(System.IO.Path.GetFileNameWithoutExtension(app), StringComparison.OrdinalIgnoreCase)));

    private static bool IsFullScreen(nint window)
    {
        var monitor = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        return GetWindowRect(window, out NativeMethods.Rect bounds)
            && NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(window, 2), ref monitor)
            && bounds.Left <= monitor.Monitor.Left && bounds.Top <= monitor.Monitor.Top
            && bounds.Right >= monitor.Monitor.Right && bounds.Bottom >= monitor.Monitor.Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeMethods.Rect bounds);

    // ── Showing ─────────────────────────────────────────────────────────────────────────────────

    private void ShowEqualizer()
    {
        isShowingEqualizer = true;
        try
        {
            EqEnabledCheck.IsChecked = equalizer.IsEnabled;
            ((RadioButton)EqProfilePanel.Children[activeProfile]).IsChecked = true;
            ProfileAppsBox.Text = profiles[activeProfile].Apps;
            foreach (RadioButton chip in EqPresetPanel.Children.OfType<RadioButton>())
            {
                chip.IsChecked = Equals(chip.Tag, equalizer.PresetName);
            }
            // The ten sliders only make sense while the curve is the plain graphic one.
            bool isGraphic = Equalizer.IsGraphic(equalizer.Bands);
            EqBandsGrid.Visibility = isGraphic && preferences.ShowAdvanced ? Visibility.Visible : Visibility.Collapsed;
            for (int index = 0; isGraphic && index < bandSliders.Count; index++)
            {
                bandSliders[index].Value = equalizer.Bands[index].Gain;
                double gain = equalizer.Bands[index].Gain;
                bandGainTexts[index].Text = gain > 0 ? $"+{gain:0.#}" : $"{gain:0.#}";
            }
            ShowSelectedBand();
        }
        finally
        {
            isShowingEqualizer = false;
        }
        DrawEqualizerCurve();
        RefreshSaveState();
    }

    private void ShowSelectedBand()
    {
        bool hasSelection = selectedBand >= 0 && selectedBand < equalizer.Bands.Count;
        BandKindPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        if (!hasSelection)
        {
            BandDetailText.Text = "Arraste um ponto para mudar frequência e ganho. Roda do mouse: largura. Duplo clique: novo ponto. Botão direito: remover.";
            return;
        }
        EqBand band = equalizer.Bands[selectedBand];
        BandDetailText.Text = band.Kind == BandKind.HighPass
            ? $"{FrequencyLabel(band.Frequency)} Hz · corta abaixo · largura {band.Q:0.00}"
            : $"{FrequencyLabel(band.Frequency)} Hz · {band.Gain:+0.0;-0.0;0.0} dB · largura {band.Q:0.00}";
        foreach (RadioButton chip in BandKindPanel.Children.OfType<RadioButton>())
        {
            chip.IsChecked = Equals(chip.Tag, band.Kind);
        }
    }

    private static string FrequencyLabel(double frequency) => frequency >= 1000 ? $"{frequency / 1000:0.#}k" : $"{frequency:0}";

    // ── Editing ─────────────────────────────────────────────────────────────────────────────────

    private void ChangeBands(List<EqBand> bands)
    {
        equalizer = equalizer with { PresetName = Equalizer.CustomPresetName, Bands = bands };
        ShowEqualizer();
        ScheduleEngineSend();
    }

    private void OnBandSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isShowingEqualizer)
        {
            return;
        }
        ChangeBands(bandSliders.Select((slider, index) => new EqBand(Equalizer.GraphicFrequencies[index], slider.Value)).ToList());
    }

    private void OnEqPresetChecked(object sender, RoutedEventArgs e)
    {
        if (isShowingEqualizer || sender is not RadioButton { Tag: string presetName } || !Equalizer.Presets.ContainsKey(presetName))
        {
            return;
        }
        equalizer = Equalizer.FromPreset(presetName) with { IsEnabled = equalizer.IsEnabled };
        selectedBand = -1;
        ShowEqualizer();
        ScheduleEngineSend();
    }

    private void OnBandKindChecked(object sender, RoutedEventArgs e)
    {
        if (isShowingEqualizer || sender is not RadioButton { Tag: BandKind kind } || selectedBand < 0)
        {
            return;
        }
        List<EqBand> bands = equalizer.Bands.ToList();
        bands[selectedBand] = bands[selectedBand] with { Kind = kind, Q = kind == BandKind.Peak ? Equalizer.OctaveQ : 0.71 };
        ChangeBands(bands);
    }

    private void OnEqEnabledClick(object sender, RoutedEventArgs e)
    {
        equalizer = equalizer with { IsEnabled = EqEnabledCheck.IsChecked == true };
        DrawEqualizerCurve();
        ScheduleEngineSend();
    }

    // Curve interaction: drag a point (frequency, gain), wheel (width), double-click (add), right-click (remove).

    private int BandAt(Point position)
    {
        int nearest = -1;
        double best = PointHitRadius;
        for (int index = 0; index < equalizer.Bands.Count; index++)
        {
            double distance = (PointFor(equalizer.Bands[index]) - position).Length;
            if (distance < best)
            {
                best = distance;
                nearest = index;
            }
        }
        return nearest;
    }

    private void OnCurveMouseDown(object sender, MouseButtonEventArgs e)
    {
        Point position = e.GetPosition(EqCurveCanvas);
        int hit = BandAt(position);
        if (hit < 0 && e.ClickCount == 2 && equalizer.Bands.Count < Equalizer.MaxUserBands)
        {
            List<EqBand> bands = equalizer.Bands.ToList();
            bands.Add(new EqBand(Math.Round(FrequencyAt(position.X)), Math.Round(GainAt(position.Y) * 2) / 2));
            selectedBand = bands.Count - 1;
            ChangeBands(bands);
            return;
        }
        selectedBand = hit;
        isDraggingBand = hit >= 0;
        if (isDraggingBand)
        {
            EqCurveCanvas.CaptureMouse();
        }
        ShowEqualizer();
    }

    private void OnCurveMouseMove(object sender, MouseEventArgs e)
    {
        if (!isDraggingBand || selectedBand < 0)
        {
            return;
        }
        Point position = e.GetPosition(EqCurveCanvas);
        List<EqBand> bands = equalizer.Bands.ToList();
        EqBand band = bands[selectedBand];
        bands[selectedBand] = band with
        {
            Frequency = Math.Round(Math.Clamp(FrequencyAt(position.X), MinFrequency, MaxFrequency)),
            Gain = band.Kind == BandKind.HighPass ? 0 : Math.Round(Math.Clamp(GainAt(position.Y), -Equalizer.MaxGain, Equalizer.MaxGain) * 2) / 2,
        };
        ChangeBands(bands);
    }

    private void OnCurveMouseUp(object sender, MouseButtonEventArgs e) => EndBandDrag();

    private void OnCurveMouseLeave(object sender, MouseEventArgs e)
    {
        if (!EqCurveCanvas.IsMouseCaptured)
        {
            EndBandDrag();
        }
    }

    private void EndBandDrag()
    {
        isDraggingBand = false;
        EqCurveCanvas.ReleaseMouseCapture();
    }

    private void OnCurveRightClick(object sender, MouseButtonEventArgs e)
    {
        int hit = BandAt(e.GetPosition(EqCurveCanvas));
        if (hit < 0)
        {
            return;
        }
        List<EqBand> bands = equalizer.Bands.ToList();
        bands.RemoveAt(hit);
        selectedBand = -1;
        ChangeBands(bands);
    }

    private void OnCurveWheel(object sender, MouseWheelEventArgs e)
    {
        int hit = selectedBand >= 0 ? selectedBand : BandAt(e.GetPosition(EqCurveCanvas));
        if (hit < 0)
        {
            return; // let the page scroll
        }
        List<EqBand> bands = equalizer.Bands.ToList();
        bands[hit] = bands[hit] with { Q = Math.Round(Math.Clamp(bands[hit].Q * (e.Delta > 0 ? 1.15 : 1 / 1.15), 0.3, 10), 2) };
        selectedBand = hit;
        ChangeBands(bands);
        e.Handled = true;
    }

    // "Segure para comparar": while held, the engine plays without the curve at the same loudness.

    private void OnCompareDown(object sender, MouseButtonEventArgs e) => SetComparing(true);

    private void OnCompareUp(object sender, MouseButtonEventArgs e) => SetComparing(false);

    private void OnCompareLeave(object sender, MouseEventArgs e) => SetComparing(false);

    private void SetComparing(bool isOn)
    {
        if (isComparing == isOn)
        {
            return;
        }
        isComparing = isOn;
        SendToEngine();
    }

    // ── Engine ──────────────────────────────────────────────────────────────────────────────────

    private void ScheduleEngineSend()
    {
        eqDebounce.Stop();
        eqDebounce.Start();
        RefreshSaveState();
    }

    /// <summary>Sends the working setup to the engine (heard right away); keeping it is up to "Salvar".</summary>
    private void SendToEngine()
    {
        try
        {
            double volumeDb = headsetAudio.VolumeDb;
            if (isCalibrating || isAnalyzing)
            {
                engine.Send(equalizer with { Bands = [] }, new SoundSettings(IsBassProtected: false));
            }
            else if (isComparing)
            {
                // Same headroom as the full chain, nothing else: a fair, level-matched reference.
                List<EqBand> all = equalizer.Bands.Concat(sound.ExtraBands(volumeDb)).ToList();
                double matched = equalizer.IsEnabled ? Equalizer.Preamp(all) : 0;
                engine.Send(equalizer with { Bands = [] }, new SoundSettings(IsBassProtected: false, TrimDb: sound.TrimDb + matched));
            }
            else
            {
                engine.Send(equalizer, sound, volumeDb);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            EqEngineText.Text = $"Não foi possível enviar o equalizador ao motor: {ex.Message}";
        }
    }

    // ── Drawing ─────────────────────────────────────────────────────────────────────────────────

    private double XFor(double frequency) => Math.Log(frequency / MinFrequency) / Math.Log(MaxFrequency / MinFrequency) * EqCurveCanvas.ActualWidth;

    private double YFor(double gainDb) => EqCurveCanvas.ActualHeight / 2 * (1 - Math.Clamp(gainDb, -GraphRangeDb, GraphRangeDb) / GraphRangeDb);

    private double FrequencyAt(double x) => MinFrequency * Math.Pow(MaxFrequency / MinFrequency, Math.Clamp(x / Math.Max(1, EqCurveCanvas.ActualWidth), 0, 1));

    private double GainAt(double y) => (1 - y / (EqCurveCanvas.ActualHeight / 2)) * GraphRangeDb;

    private Point PointFor(EqBand band) => new(XFor(band.Frequency), YFor(band.Kind == BandKind.HighPass ? 0 : band.Gain));

    private void OnEqCurveResized(object sender, SizeChangedEventArgs e) => DrawEqualizerCurve();

    /// <summary>The curve (headroom excluded, so points sit on their own gain) and one handle per band.</summary>
    private void DrawEqualizerCurve()
    {
        double width = EqCurveCanvas.ActualWidth, height = EqCurveCanvas.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }
        double middle = height / 2;
        EqZeroLine.X1 = 0;
        EqZeroLine.X2 = width;
        EqZeroLine.Y1 = EqZeroLine.Y2 = middle;

        var points = new PointCollection(CurvePoints + 1);
        for (int step = 0; step <= CurvePoints; step++)
        {
            double frequency = MinFrequency * Math.Pow(MaxFrequency / MinFrequency, (double)step / CurvePoints);
            double gain = equalizer.IsEnabled ? Equalizer.CurveDb(equalizer.Bands, frequency) : 0;
            points.Add(new Point((double)step / CurvePoints * width, YFor(gain)));
        }
        EqCurveLine.Points = points;
        EqCurveLine.Opacity = equalizer.IsEnabled ? 1 : 0.35;
        EqCurveFill.Points = new PointCollection(points) { new Point(width, middle), new Point(0, middle) };
        EqCurveFill.Opacity = EqCurveLine.Opacity;

        EqPoints.Children.Clear();
        for (int index = 0; index < equalizer.Bands.Count; index++)
        {
            bool isSelected = index == selectedBand;
            var handle = new Ellipse { Width = isSelected ? 16 : 12, Height = isSelected ? 16 : 12, StrokeThickness = 2, IsHitTestVisible = false };
            handle.SetResourceReference(Shape.FillProperty, "AccentBrush");
            handle.SetResourceReference(Shape.StrokeProperty, isSelected ? "InkBrush" : "BgBrush");
            Point centre = PointFor(equalizer.Bands[index]);
            Canvas.SetLeft(handle, centre.X - handle.Width / 2);
            Canvas.SetTop(handle, centre.Y - handle.Height / 2);
            EqPoints.Children.Add(handle);
        }
    }

    /// <summary>Live output spectrum behind the curve: -84 dBFS at the bottom, 0 at the top.</summary>
    private void DrawSpectrum(float[]? spectrum)
    {
        double width = EqCurveCanvas.ActualWidth, height = EqCurveCanvas.ActualHeight;
        if (spectrum is null || width <= 0 || height <= 0)
        {
            EqSpectrum.Points = null;
            return;
        }
        var points = new PointCollection(spectrum.Length + 2) { new Point(0, height) };
        for (int band = 0; band < spectrum.Length; band++)
        {
            double level = Math.Clamp((spectrum[band] + 84) / 84, 0, 1);
            points.Add(new Point((band + 0.5) / spectrum.Length * width, height - level * height));
        }
        points.Add(new Point(width, height));
        EqSpectrum.Points = points;
    }
}
