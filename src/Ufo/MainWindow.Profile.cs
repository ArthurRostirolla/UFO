using System.IO;
using System.Windows;
using System.Windows.Controls;
using Ufo.Audio;
using Ufo.Device;

namespace Ufo;

/// <summary>
/// Saved profile: changes apply to the headset right away, but only "Salvar" keeps them. The saved
/// profile is written back to the headset when the app starts and whenever the headset reconnects
/// (powering it off can reset it), and the saved equalizer is restored on start.
/// </summary>
public partial class MainWindow
{
    private SavedProfile? savedProfile = ProfileStore.Load();
    private bool isHeadsetLinked;

    private HeadsetSettings? CurrentHeadsetSettings()
    {
        if (!FonePanel.IsEnabled)
        {
            // Headset offline: the controls show nothing real, so keep what was saved.
            return savedProfile?.Headset;
        }
        RadioButton? sleepOption = SleepMinutesPanel.Children.OfType<RadioButton>().FirstOrDefault(option => option.IsChecked == true);
        return new HeadsetSettings(
            SelectedSoundMode(),
            (byte)AncLevelSlider.Value,
            MicEnabledCheck.IsChecked == true,
            NoiseReductionCheck.IsChecked == true,
            (byte)SidetoneSlider.Value,
            SleepCheck.IsChecked == true,
            sleepOption?.Tag is string tag ? int.Parse(tag) : 30,
            EnduranceCheck.IsChecked == true,
            (byte)EnduranceSlider.Value);
    }

    private SavedProfile CurrentProfile()
    {
        StoreActiveProfile();
        return new(CurrentHeadsetSettings(), equalizer, voice, profiles.ToList(), profiles[baseProfile].Name, isAutoProfile);
    }

    /// <summary>Enables "Salvar" only when something differs from the saved profile.</summary>
    private void RefreshSaveState()
    {
        if (SaveButton is null || profiles.Count == 0)
        {
            return;
        }
        bool hasChanges = savedProfile is null || ProfileStore.Serialize(CurrentProfile()) != ProfileStore.Serialize(savedProfile);
        SaveButton.IsEnabled = hasChanges;
        SaveStateText.Text = savedProfile is null ? "nada salvo ainda" : hasChanges ? "alterações não salvas" : "tudo salvo";
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        SavedProfile profile = CurrentProfile();
        try
        {
            ProfileStore.Save(profile);
            savedProfile = profile;
            StatusText.Text = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Não foi possível salvar: {ex.Message}";
        }
        RefreshSaveState();
    }

    /// <summary>Writes the saved headset settings to the device; false if any write was refused.</summary>
    private async Task<bool> ApplySavedHeadsetAsync()
    {
        if (savedProfile?.Headset is not { } settings)
        {
            return true;
        }
        return await Task.Run(() =>
            headset.SetAnc(settings.SoundMode, settings.AncLevel)
            & headset.SetMicEnabled(settings.IsMicEnabled)
            & headset.SetNoiseReduction(settings.IsNoiseReductionOn)
            & headset.SetSidetone(settings.SidetoneLevel)
            & headset.SetSleep(settings.IsSleepEnabled, settings.SleepMinutes)
            & headset.SetEndurance(settings.IsEnduranceEnabled, settings.EnduranceThreshold));
    }
}
