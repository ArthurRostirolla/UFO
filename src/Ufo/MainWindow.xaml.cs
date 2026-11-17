using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Ufo.Device;

namespace Ufo;

public partial class MainWindow : Window
{
    private const string NoAnswerMessage = "O fone não respondeu. Confira se ele está ligado no modo 2.4 GHz.";

    private readonly Headset headset = new();
    // Also how fast a headset that comes back gets its saved profile again.
    private readonly DispatcherTimer batteryTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer sliderDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private Func<bool>? pendingSliderWrite;
    private bool isShowingDeviceState;

    private sealed record DeviceSnapshot(
        DeviceInfo? Info,
        bool? IsLinked,
        BatteryState? Battery,
        AncState? Anc,
        MicProcessingState? MicProcessing,
        bool? IsMicEnabled,
        bool? IsBoomMicAttached,
        SleepState? Sleep,
        EnduranceState? Endurance);

    public MainWindow()
    {
        InitializeComponent();
        InitializeEqualizer();
        InitializeBeam();
        InitializeM274();
        InitializeSettings();
        InitializeClips();
        StartFloating();
        WindowsStartup.Sync();
        SyncTrayIcon();
        if (WindowsStartup.IsStartupLaunch)
        {
            // Launched at sign-in: wait in the tray (background mode) or the taskbar, never over the desktop.
            WindowState = WindowState.Minimized;
            Loaded += (_, _) =>
            {
                FinishSplash();
                if (preferences.RunInBackground)
                {
                    Hide();
                }
            };
        }
        else
        {
            Loaded += (_, _) => PlaySplash();
        }
        Loaded += async (_, _) => await RefreshAllAsync();
        batteryTimer.Tick += async (_, _) => await RefreshBatteryAsync();
        batteryTimer.Start();
        sliderDebounce.Tick += OnSliderDebounceElapsed;
    }

    /// <summary>Puts the saved profile back on the headset, then shows what the headset reports.</summary>
    private async Task RefreshAllAsync()
    {
        if (!await ApplySavedHeadsetAsync())
        {
            StatusText.Text = NoAnswerMessage;
        }
        DeviceSnapshot snapshot = await Task.Run(() => new DeviceSnapshot(
            headset.GetDeviceInfo(),
            headset.IsHeadsetLinked(),
            headset.GetBattery(),
            headset.GetAnc(),
            headset.GetMicProcessing(),
            headset.IsMicEnabled(),
            headset.IsBoomMicAttached(),
            headset.GetSleep(),
            headset.GetEndurance()));
        ShowSnapshot(snapshot);
    }

    private async Task RefreshBatteryAsync()
    {
        bool wasLinked = isHeadsetLinked;
        (bool? isLinked, BatteryState? battery) = await Task.Run(() => (headset.IsHeadsetLinked(), headset.GetBattery()));
        if (!wasLinked && isLinked == true)
        {
            // Headset came back (switched on or back in range): restore the saved profile on it.
            await RefreshAllAsync();
            return;
        }
        ShowLink(isLinked, battery);
    }

    private void ShowSnapshot(DeviceSnapshot snapshot)
    {
        isShowingDeviceState = true;
        try
        {
            FirmwareText.Text = snapshot.Info is { } info
                ? $"fone {info.HeadsetFirmware} · dongle {info.DongleFirmware}"
                : "—";
            ShowLink(snapshot.IsLinked, snapshot.Battery);

            if (snapshot.Anc is { } anc)
            {
                RadioButtonFor(anc.Mode).IsChecked = true;
                AncLevelSlider.Value = Math.Clamp(anc.Level, (byte)1, HeadsetProtocol.MaxAncLevel);
            }
            if (snapshot.MicProcessing is { } mic)
            {
                NoiseReductionCheck.IsChecked = mic.IsNoiseReductionOn;
                SidetoneSlider.Value = mic.SidetoneLevel;
            }
            MicEnabledCheck.IsChecked = snapshot.IsMicEnabled;
            BoomMicText.Text = snapshot.IsBoomMicAttached switch
            {
                true => "Conectado",
                false => "Desconectado",
                null => "—",
            };
            if (snapshot.Sleep is { } sleep)
            {
                SleepCheck.IsChecked = sleep.IsEnabled;
                foreach (RadioButton option in SleepMinutesPanel.Children.OfType<RadioButton>())
                {
                    option.IsChecked = Equals(option.Tag, sleep.Minutes.ToString());
                }
            }
            if (snapshot.Endurance is { } endurance)
            {
                EnduranceCheck.IsChecked = endurance.IsEnabled;
                EnduranceSlider.Value = endurance.ThresholdPercent;
            }
        }
        finally
        {
            isShowingDeviceState = false;
        }
        RefreshSaveState();
    }

    private void ShowLink(bool? isLinked, BatteryState? battery)
    {
        bool isOnline = isLinked == true;
        isHeadsetLinked = isOnline;
        FonePanel.IsEnabled = isOnline;
        MicPanel.IsEnabled = isOnline;
        if (isOnline)
        {
            ShowBeam(battery); // offline the beam keeps its charge while it retracts (see ShowCrash)
        }
        ShowCrash(!isOnline);
        LinkText.Text = isLinked switch
        {
            true => battery switch
            {
                { IsCharging: true } => "aw pro · carregando",
                { Level: <= LowBatteryPercent } => "aw pro · bateria baixa",
                _ => "aw pro · conectado",
            },
            false => "fone desligado ou fora de alcance",
            null => "dongle não encontrado",
        };
        BatteryText.Text = isOnline && battery is { } state ? $"{state.Level}%" : "—";
        ConnectionText.Text = isLinked switch
        {
            true => "dongle 2.4 GHz",
            false => "fone fora de alcance",
            null => "sem dongle",
        };
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, isOnline ? "AccentBrush" : "MutedBrush");
    }

    private RadioButton RadioButtonFor(SoundMode mode) => mode switch
    {
        SoundMode.NoiseCancelling => ModeAnc,
        SoundMode.Transparency => ModeTransparency,
        _ => ModeOff,
    };

    private SoundMode SelectedSoundMode() =>
        ModeAnc.IsChecked == true ? SoundMode.NoiseCancelling
        : ModeTransparency.IsChecked == true ? SoundMode.Transparency
        : SoundMode.Off;

    private async void ApplyNow(Func<bool> write)
    {
        RefreshSaveState();
        bool isApplied = await Task.Run(write);
        StatusText.Text = isApplied ? "" : NoAnswerMessage;
    }

    private void ApplyAfterSliderSettles(Func<bool> write)
    {
        pendingSliderWrite = write;
        sliderDebounce.Stop();
        sliderDebounce.Start();
    }

    private void OnSliderDebounceElapsed(object? sender, EventArgs e)
    {
        sliderDebounce.Stop();
        if (pendingSliderWrite is { } write)
        {
            pendingSliderWrite = null;
            ApplyNow(write);
        }
    }

    private void OnSoundModeChecked(object sender, RoutedEventArgs e)
    {
        if (isShowingDeviceState || sender is not RadioButton { Tag: SoundMode mode })
        {
            return;
        }
        byte level = (byte)AncLevelSlider.Value;
        ApplyNow(() => headset.SetAnc(mode, level));
    }

    private void OnAncLevelChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isShowingDeviceState || !IsLoaded)
        {
            return;
        }
        SoundMode mode = SelectedSoundMode();
        byte level = (byte)e.NewValue;
        ApplyAfterSliderSettles(() => headset.SetAnc(mode, level));
    }

    private void OnMicEnabledClick(object sender, RoutedEventArgs e)
    {
        bool isEnabled = MicEnabledCheck.IsChecked == true;
        ApplyNow(() => headset.SetMicEnabled(isEnabled));
    }

    private void OnNoiseReductionClick(object sender, RoutedEventArgs e)
    {
        bool isOn = NoiseReductionCheck.IsChecked == true;
        ApplyNow(() => headset.SetNoiseReduction(isOn));
    }

    private void OnSidetoneChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isShowingDeviceState || !IsLoaded)
        {
            return;
        }
        byte level = (byte)e.NewValue;
        ApplyAfterSliderSettles(() => headset.SetSidetone(level));
    }

    private void OnSleepChanged(object sender, RoutedEventArgs e)
    {
        RadioButton? chosen = SleepMinutesPanel.Children.OfType<RadioButton>().FirstOrDefault(option => option.IsChecked == true);
        if (isShowingDeviceState || !IsLoaded || chosen?.Tag is not string tag)
        {
            return;
        }
        bool isEnabled = SleepCheck.IsChecked == true;
        int minutes = int.Parse(tag);
        ApplyNow(() => headset.SetSleep(isEnabled, minutes));
    }

    private void OnEnduranceClick(object sender, RoutedEventArgs e) => ApplyEndurance(ApplyNow);

    private void OnEnduranceThresholdChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (isShowingDeviceState || !IsLoaded)
        {
            return;
        }
        ApplyEndurance(ApplyAfterSliderSettles);
    }

    private void ApplyEndurance(Action<Func<bool>> apply)
    {
        bool isEnabled = EnduranceCheck.IsChecked == true;
        byte threshold = (byte)EnduranceSlider.Value;
        apply(() => headset.SetEndurance(isEnabled, threshold));
    }
}
