using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ufo;

/// <summary>
/// Settings screen (gear in the title bar): accent colour, background theme, start with Windows and
/// background mode. Every choice applies at once and is kept in %AppData%\UFO\preferences.json.
/// </summary>
public partial class MainWindow
{
    private AppPreferences preferences = Appearance.Load();
    private string currentSection = "fone";
    private bool isShowingPreferences;

    private void InitializeSettings()
    {
        foreach ((string name, string hex) in Appearance.Accents)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                GroupName = "Accent",
                Tag = new SolidColorBrush(Appearance.Rgb(hex)),
                ToolTip = name,
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, name);
            swatch.Checked += (_, _) => ChooseAccent(hex);
            AccentPanel.Children.Insert(AccentPanel.Children.Count - 1, swatch);
        }
        foreach (AppTheme theme in Appearance.Themes)
        {
            var card = new RadioButton
            {
                Style = (Style)FindResource("ThemeCard"),
                GroupName = "Theme",
                Tag = new SolidColorBrush(theme.Background),
                Content = new TextBlock { Text = theme.Name, FontSize = 14, FontWeight = FontWeights.Medium },
            };
            card.Checked += (_, _) => ChooseTheme(theme.Name);
            ThemePanel.Children.Add(card);
        }
        Appearance.Apply(preferences);
        ShowPreferences();
    }

    /// <summary>Marks the controls that match the current preferences without re-triggering them.</summary>
    private void ShowPreferences()
    {
        isShowingPreferences = true;
        try
        {
            var accentNames = Appearance.Accents.ToDictionary(accent => accent.Hex, accent => accent.Name, StringComparer.OrdinalIgnoreCase);
            foreach (RadioButton swatch in AccentPanel.Children.OfType<RadioButton>())
            {
                swatch.IsChecked = accentNames.TryGetValue(preferences.AccentHex, out string? name) && Equals(swatch.ToolTip, name);
            }
            foreach (RadioButton card in ThemePanel.Children.OfType<RadioButton>())
            {
                card.IsChecked = card.Content is TextBlock { Text: var text } && text == Appearance.ThemeNamed(preferences.ThemeName).Name;
            }
            StartWithWindowsCheck.IsChecked = WindowsStartup.IsWanted;
            RunInBackgroundCheck.IsChecked = preferences.RunInBackground;
        }
        finally
        {
            isShowingPreferences = false;
        }
    }

    private void UpdatePreferences(AppPreferences changed)
    {
        preferences = changed;
        Appearance.Apply(preferences);
        try
        {
            Appearance.Save(preferences);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"Não foi possível guardar as preferências: {ex.Message}";
        }
    }

    private void ChooseAccent(string hex)
    {
        if (!isShowingPreferences)
        {
            UpdatePreferences(preferences with { AccentHex = hex });
        }
    }

    private void ChooseTheme(string themeName)
    {
        if (!isShowingPreferences)
        {
            UpdatePreferences(preferences with { ThemeName = themeName });
        }
    }

    private void OnCustomAccentClick(object sender, RoutedEventArgs e)
    {
        Color current = Appearance.TryRgb(preferences.AccentHex) ?? Appearance.Rgb(AppPreferences.Default.AccentHex);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
            FullOpen = true,
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }
        UpdatePreferences(preferences with { AccentHex = Appearance.Hex(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B)) });
        ShowPreferences();
    }

    // Checked/Unchecked rather than Click, so keyboard and accessibility tools toggle them too.
    private void OnRunInBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (isShowingPreferences)
        {
            return;
        }
        UpdatePreferences(preferences with { RunInBackground = RunInBackgroundCheck.IsChecked == true });
        SyncTrayIcon();
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (!isShowingPreferences)
        {
            WindowsStartup.SetWanted(StartWithWindowsCheck.IsChecked == true);
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (SettingsPanel.Visibility == Visibility.Visible)
        {
            CloseSettings();
            return;
        }
        // Release the moons so clicking any of them (even the current one) leaves the settings.
        foreach (RadioButton moon in new[] { FoneMoonButton, MicMoonButton, EqMoonButton, ClipsMoonButton })
        {
            moon.IsChecked = false;
        }
        ShowPreferences();
        ShowPanel("settings");
        SetSettingsGear(isOpen: true);
    }

    private void OnSettingsBackClick(object sender, RoutedEventArgs e) => CloseSettings();

    private void CloseSettings()
    {
        RadioButton moon = currentSection switch { "mic" => MicMoonButton, "eq" => EqMoonButton, "clips" => ClipsMoonButton, _ => FoneMoonButton };
        moon.IsChecked = true;
    }

    private void SetSettingsGear(bool isOpen)
    {
        if (isOpen)
        {
            SettingsButton.SetResourceReference(ForegroundProperty, "AccentBrush");
        }
        else
        {
            SettingsButton.ClearValue(ForegroundProperty); // back to the caption style (and its hover)
        }
        SettingsGearTurn.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(isOpen ? 90 : 0, Motion(300)) { EasingFunction = EaseOut });
    }
}
