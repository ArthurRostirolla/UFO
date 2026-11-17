using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ufo.Audio;

namespace Ufo;

/// <summary>
/// "Som" rows (tone, loudness, crossfeed, footsteps, night mode, bass extras) and the ear
/// calibration. Rows are built in code so the microphone screen reuses the same builders.
/// </summary>
public partial class MainWindow
{
    private readonly List<Action> soundRefreshers = [];
    private readonly DispatcherTimer calibrationTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    private readonly List<double> calibrationLevels = [];
    private bool isShowingRows, isCalibrating, isReferenceTone;

    private void InitializeSound()
    {
        void Change(SoundSettings changed)
        {
            sound = changed;
            ScheduleM274Send();
        }
        AddSliderRow(QuickSoundRows, soundRefreshers, "Graves", -6, 9, false, () => sound.BassDb, value => Change(sound with { BassDb = value }),
            "Sobe ou desce os graves como um todo, sem mexer na curva.");
        AddSliderRow(QuickSoundRows, soundRefreshers, "Timbre (quente ↔ claro)", -6, 6, false, () => sound.TiltDb, value => Change(sound with { TiltDb = value }),
            "Inclina o som inteiro: para a esquerda fica mais quente e grave, para a direita mais claro e agudo.");
        AddSliderRow(SoundRows, soundRefreshers, "Volume do perfil", -12, 6, false, () => sound.TrimDb, value => Change(sound with { TrimDb = value }),
            "Deixa este perfil mais alto ou mais baixo que os outros. Acima de 0 o limitador segura os picos.");
        AddSliderRow(SoundRows, soundRefreshers, "Crossfeed", 0, 1, true, () => sound.Crossfeed, value => Change(sound with { Crossfeed = value }),
            "Mistura um pouco de cada lado no outro, como caixas de som: música menos cansativa.");
        AddSliderRow(SoundRows, soundRefreshers, "Passos", 0, 1, true, () => sound.Footsteps, value => Change(sound with { Footsteps = value }),
            "Realça 2–4 kHz só nos sons fracos; explosões não ficam mais altas.");
        AddSliderRow(SoundRows, soundRefreshers, "Noite", 0, 1, true, () => sound.Night, value => Change(sound with { Night = value }),
            "Aproxima o volume dos sons fracos e fortes.");
        AddSliderRow(SoundRows, soundRefreshers, "Graves harmônicos", 0, 1, true, () => sound.BassEnhance, value => Change(sound with { BassEnhance = value }),
            "Dá peso aos graves com harmônicos, sem forçar o alto-falante.");
        AddSwitchRow(SoundRows, soundRefreshers, "Proteção de graves", () => sound.IsBassProtected, value => Change(sound with { IsBassProtected = value }),
            "Só age se os seus reforços empurrarem os graves acima do máximo digital; música normal passa intacta.");
        AddSwitchRow(SoundRows, soundRefreshers, "Loudness", () => sound.IsLoudnessOn, value => Change(sound with { IsLoudnessOn = value }),
            "Reforça de leve graves e agudos quando o volume do Windows está baixo (até +4 dB de grave).");
        AddSwitchRow(SoundRows, soundRefreshers, "Limitador", () => sound.IsLimiterOn, value => Change(sound with { IsLimiterOn = value }),
            "Segura picos acima do máximo sem estourar. Deixe ligado.");

        calibrationTimer.Tick += (_, _) =>
        {
            isReferenceTone = !isReferenceTone;
            PlayCalibrationTone();
        };
        ShowSound();
    }

    private void ShowSound()
    {
        isShowingRows = true;
        soundRefreshers.ForEach(refresh => refresh());
        isShowingRows = false;
        ShowCalibration();
    }

    // ── Simple and advanced ─────────────────────────────────────────────────────────────────────
    // By default each screen shows only the choices that decide the sound (profile, style, bass,
    // tone; voice style). Everything else is one click away and the choice is remembered.

    private void OnAdvancedClick(object sender, RoutedEventArgs e)
    {
        preferences = preferences with { ShowAdvanced = !preferences.ShowAdvanced };
        Appearance.Save(preferences);
        ShowAdvancedState();
    }

    private void ShowAdvancedState()
    {
        Visibility advanced = preferences.ShowAdvanced ? Visibility.Visible : Visibility.Collapsed;
        ProfileAppsRow.Visibility = BandDetailRow.Visibility = SoundAdvanced.Visibility = CalibrationAdvanced.Visibility = VoiceRows.Visibility = advanced;
        AdvancedButton.Content = VoiceAdvancedButton.Content = preferences.ShowAdvanced ? "Ocultar ajustes avançados" : "Mostrar ajustes avançados";
        EqCurveCanvas.Height = preferences.ShowAdvanced ? 220 : 150; // editing by points needs the room; a preview does not
        ShowEqualizer(); // the ten band sliders follow the same switch
    }

    // ── Row builders (shared with the microphone screen) ────────────────────────────────────────

    /// <summary>Label, slider and value. Percent rows run 0–1 and read "desligado" at zero; the others are dB.</summary>
    private void AddSliderRow(Panel rows, List<Action> refreshers, string label, double minimum, double maximum, bool isPercent,
        Func<double> read, Action<double> write, string? tip = null)
    {
        var grid = new Grid { MinHeight = 44, ToolTip = tip };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        var name = new TextBlock { Text = label, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            TickFrequency = isPercent ? 0.05 : 0.5,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        System.Windows.Automation.AutomationProperties.SetName(slider, label);
        var value = new TextBlock { Style = (Style)FindResource("Value"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        void ShowValue() => value.Text = isPercent
            ? slider.Value <= 0 ? "desligado" : $"{slider.Value * 100:0}%"
            : $"{slider.Value:+0.0;-0.0;0} dB";
        slider.ValueChanged += (_, _) =>
        {
            ShowValue();
            if (!isShowingRows)
            {
                write(slider.Value);
            }
        };
        refreshers.Add(() =>
        {
            slider.Value = read();
            ShowValue();
        });
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        grid.Children.Add(name);
        grid.Children.Add(slider);
        grid.Children.Add(value);
        rows.Children.Add(grid);
    }

    private void AddSwitchRow(Panel rows, List<Action> refreshers, string label, Func<bool> read, Action<bool> write, string? tip = null)
    {
        var check = new CheckBox
        {
            Style = (Style)FindResource("Switch"),
            Content = new TextBlock { Text = label, FontSize = 14 },
            MinHeight = 44,
            ToolTip = tip,
        };
        System.Windows.Automation.AutomationProperties.SetName(check, label);
        check.Click += (_, _) => write(check.IsChecked == true);
        refreshers.Add(() => check.IsChecked = read());
        rows.Children.Add(check);
    }

    // ── Ear calibration ─────────────────────────────────────────────────────────────────────────
    // A 1 kHz reference alternates with a test tone; the listener sets the test tone's level until
    // both sound equally loud. The M274 plays flat meanwhile, so the headphone itself is measured.

    private void ShowCalibration()
    {
        int step = calibrationLevels.Count;
        CalibrationStep.Visibility = CalibrationCancelButton.Visibility = isCalibrating ? Visibility.Visible : Visibility.Collapsed;
        CalibrationClearButton.Visibility = !isCalibrating && sound.Correction is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
        if (isCalibrating)
        {
            bool isLast = step == EarCalibration.Points.Length - 1;
            CalibrationButton.Content = isLast ? "Concluir" : "Próximo tom";
            CalibrationToneText.Text = $"{FrequencyLabel(EarCalibration.Points[step].Frequency)} Hz · {step + 1} de {EarCalibration.Points.Length}";
            CalibrationText.Text = "Dois tons se alternam: o de referência e o de teste. Mova o controle até os dois parecerem ter o mesmo volume.";
            return;
        }
        CalibrationButton.Content = sound.Correction is { Count: > 0 } ? "Calibrar de novo" : "Calibrar";
        CalibrationText.Text = sound.Correction is { Count: > 0 } correction
            ? $"Calibração ativa: {correction.Count} ajustes aplicados em todos os perfis. É aproximada e pessoal: inclui a sua audição e o encaixe do fone."
            : "Compara o volume de oito tons com um tom de referência e corrige o fone para o seu ouvido. Leva uns dois minutos, em volume confortável e lugar silencioso.";
    }

    private void OnCalibrationClick(object sender, RoutedEventArgs e)
    {
        if (isAnalyzing)
        {
            return;
        }
        if (!isCalibrating)
        {
            isCalibrating = true;
            calibrationLevels.Clear();
            SendToM274(); // flat while measuring
            StartCalibrationStep();
            return;
        }
        calibrationLevels.Add(CalibrationSlider.Value);
        if (calibrationLevels.Count < EarCalibration.Points.Length)
        {
            StartCalibrationStep();
            return;
        }
        SetCorrection(EarCalibration.Correction(calibrationLevels));
    }

    private void StartCalibrationStep()
    {
        // Start where a neutral headphone would land, so the listener only has to trim.
        CalibrationSlider.Value = EarCalibration.Points[calibrationLevels.Count].ContourDb;
        isReferenceTone = true;
        PlayCalibrationTone();
        calibrationTimer.Start();
        ShowCalibration();
    }

    private void PlayCalibrationTone()
    {
        if (!isCalibrating || calibrationLevels.Count >= EarCalibration.Points.Length)
        {
            return;
        }
        try
        {
            if (isReferenceTone)
            {
                headsetAudio.PlayTone(EarCalibration.ReferenceFrequency, EarCalibration.ReferenceLevelDb);
            }
            else
            {
                headsetAudio.PlayTone(EarCalibration.Points[calibrationLevels.Count].Frequency, EarCalibration.ReferenceLevelDb + CalibrationSlider.Value);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            EndCalibration();
            CalibrationText.Text = $"Não foi possível tocar o tom no fone: {ex.Message}";
        }
    }

    private void OnCalibrationLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isCalibrating && !isReferenceTone)
        {
            PlayCalibrationTone();
        }
    }

    private void OnCalibrationCancelClick(object sender, RoutedEventArgs e) => EndCalibration();

    private void OnCalibrationClearClick(object sender, RoutedEventArgs e) => SetCorrection(null);

    /// <summary>The correction belongs to the headphone, not to one profile: it goes into all of them.</summary>
    private void SetCorrection(IReadOnlyList<EqBand>? correction)
    {
        sound = sound with { Correction = correction };
        profiles = profiles.Select(profile => profile with { Sound = profile.Sound with { Correction = correction } }).ToList();
        EndCalibration();
        RefreshSaveState();
    }

    private void EndCalibration()
    {
        calibrationTimer.Stop();
        headsetAudio.StopTone();
        isCalibrating = false;
        calibrationLevels.Clear();
        ShowCalibration();
        SendToM274();
    }
}
