using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Ufo.Audio;

namespace Ufo;

/// <summary>
/// Voice processing done by the M274 on what Windows receives from the microphone:
/// the chain's amounts, an honest note about the link's sample rate, and a record-and-listen test.
/// </summary>
public partial class MainWindow
{
    private readonly List<Action> voiceRefreshers = [];
    private readonly DispatcherTimer voiceInfoTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private VoiceSettings voice = new();

    /// <summary>Ready-made chains, so nobody has to understand ten sliders to sound good.</summary>
    private static readonly (string Name, string Tip, VoiceSettings Settings)[] VoiceStyles =
    [
        ("Natural", "Limpeza leve: tira ronco e um pouco de ruído, sem mudar o timbre. Bom para o dia a dia.",
            new VoiceSettings(IsEnabled: true, Denoise: 0.25, Gate: 0.2, Compress: 0.3, Leveler: 0.3)),
        ("Ambiente barulhento", "Redução de ruído forte e pausas silenciadas: teclado, ventilador, gente falando ao fundo.",
            new VoiceSettings(IsEnabled: true, Denoise: 0.7, Gate: 0.5, DeEss: 0.3, Compress: 0.4, Leveler: 0.5, PresenceDb: 2)),
        ("Locução", "Voz encorpada e firme, estilo rádio: para stream, gravação e apresentações.",
            new VoiceSettings(IsEnabled: true, Denoise: 0.4, Gate: 0.4, DeEss: 0.5, Compress: 0.7, Leveler: 0.6, BodyDb: 3, PresenceDb: 2, AirDb: 2)),
    ];
    private bool isTestingMicrophone;

    private void InitializeVoice()
    {
        voice = savedProfile?.Voice ?? new VoiceSettings();
        void Change(VoiceSettings changed)
        {
            voice = changed;
            SendVoiceToM274();
            ShowVoiceStyle();
            ShowVoiceInfo();
            RefreshSaveState();
        }
        foreach ((string name, string tip, VoiceSettings settings) in VoiceStyles)
        {
            var chip = new System.Windows.Controls.RadioButton { Content = name, GroupName = "VoiceStyle", Style = (Style)FindResource("Chip"), ToolTip = tip };
            chip.Checked += (_, _) =>
            {
                if (!isShowingRows)
                {
                    voice = settings;
                    SendVoiceToM274();
                    ShowVoice();
                    RefreshSaveState();
                }
            };
            VoiceStylePanel.Children.Add(chip);
        }
        AddSwitchRow(VoiceRows, voiceRefreshers, "Corte de graves (80 Hz)", () => voice.IsHighPassOn, value => Change(voice with { IsHighPassOn = value }),
            "Tira ronco, vento e batidas na mesa.");
        AddSliderRow(VoiceRows, voiceRefreshers, "Redução de ruído", 0, 1, true, () => voice.Denoise, value => Change(voice with { Denoise = value }),
            "Tira ruído constante de fundo: ventilador, ar-condicionado, chiado.");
        AddSliderRow(VoiceRows, voiceRefreshers, "Silenciar pausas", 0, 1, true, () => voice.Gate, value => Change(voice with { Gate = value }),
            "Abaixa o microfone quando você não está falando.");
        AddSliderRow(VoiceRows, voiceRefreshers, "De-esser", 0, 1, true, () => voice.DeEss, value => Change(voice with { DeEss = value }),
            "Suaviza o chiado dos \"s\".");
        AddSliderRow(VoiceRows, voiceRefreshers, "Compressor", 0, 1, true, () => voice.Compress, value => Change(voice with { Compress = value }),
            "Voz mais firme: aproxima falas baixas e altas.");
        AddSliderRow(VoiceRows, voiceRefreshers, "Nivelador", 0, 1, true, () => voice.Leveler, value => Change(voice with { Leveler = value }),
            "Mantém o mesmo volume quando você se afasta ou se aproxima.");
        AddSliderRow(VoiceRows, voiceRefreshers, "Corpo", -6, 6, false, () => voice.BodyDb, value => Change(voice with { BodyDb = value }),
            "Graves da voz: mais para uma voz encorpada, menos se ela soa abafada.");
        AddSliderRow(VoiceRows, voiceRefreshers, "Presença", -6, 6, false, () => voice.PresenceDb, value => Change(voice with { PresenceDb = value }),
            "Faixa que faz a fala ser entendida (3 kHz).");
        AddSliderRow(VoiceRows, voiceRefreshers, "Brilho", -6, 6, false, () => voice.AirDb, value => Change(voice with { AirDb = value }),
            "Agudos da voz. Sem fio o efeito é pequeno: o microfone só chega até 8 kHz.");
        AddSliderRow(VoiceRows, voiceRefreshers, "Ganho", -12, 12, false, () => voice.TrimDb, value => Change(voice with { TrimDb = value }),
            "Volume final do microfone, depois de todo o processamento.");

        voiceInfoTimer.Tick += (_, _) => ShowVoiceInfo();
        voiceInfoTimer.Start();
        ShowVoice();
        SendVoiceToM274();
    }

    private void ShowVoice()
    {
        isShowingRows = true;
        VoiceEnabledCheck.IsChecked = voice.IsEnabled;
        voiceRefreshers.ForEach(refresh => refresh());
        isShowingRows = false;
        VoiceRows.IsEnabled = voice.IsEnabled;
        VoiceRows.Opacity = voice.IsEnabled ? 1 : 0.45;
        ShowVoiceStyle();
        ShowVoiceInfo();
    }

    /// <summary>Marks the style the current chain matches; none is marked once a slider leaves it.</summary>
    private void ShowVoiceStyle()
    {
        bool wasShowing = isShowingRows;
        isShowingRows = true;
        for (int index = 0; index < VoiceStyles.Length; index++)
        {
            ((System.Windows.Controls.RadioButton)VoiceStylePanel.Children[index]).IsChecked = voice == VoiceStyles[index].Settings;
        }
        isShowingRows = wasShowing;
        VoiceStylePanel.IsEnabled = voice.IsEnabled;
        VoiceStylePanel.Opacity = voice.IsEnabled ? 1 : 0.45;
    }

    private void OnVoiceEnabledClick(object sender, RoutedEventArgs e)
    {
        voice = voice with { IsEnabled = VoiceEnabledCheck.IsChecked == true };
        if (voice == new VoiceSettings(IsEnabled: true))
        {
            voice = VoiceStyles[0].Settings; // switched on with nothing set: start from "Natural" instead of a chain that does nothing
        }
        SendVoiceToM274();
        ShowVoice();
        RefreshSaveState();
    }

    private void SendVoiceToM274()
    {
        try
        {
            // While the boom microphone is out, the learned gain rides on top of the user's chain (or alone, when the chain is off).
            m274.SendVoice(appliedBoomDb <= 0 ? voice
                : voice.IsEnabled ? voice with { TrimDb = voice.TrimDb + appliedBoomDb }
                : new VoiceSettings(IsEnabled: true, IsHighPassOn: false, TrimDb: appliedBoomDb));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MicTestText.Text = $"Não foi possível enviar ao M274: {ex.Message}";
        }
    }

    /// <summary>What the link allows, whether the M274 is on the microphone, and the double-processing warning.</summary>
    private void ShowVoiceInfo()
    {
        int? rate;
        try
        {
            rate = headsetAudio.MicrophoneSampleRate();
        }
        catch (COMException)
        {
            rate = null;
        }
        var notes = new List<string>
        {
            rate switch
            {
                null => "Microfone do fone não encontrado.",
                <= 16000 => "Sem fio, o microfone chega ao PC em 16 kHz (voz até 8 kHz). No cabo USB-C chega em 48 kHz: é o maior ganho de qualidade possível, nenhum ajuste substitui.",
                _ => $"No cabo: {rate / 1000} kHz, a melhor qualidade que este microfone entrega.",
            },
        };
        if (!M274Client.IsMicrophoneInstalled())
        {
            notes.Add("O M274 ainda não está ligado ao microfone: instale ou reinstale em Configurações.");
        }
        if (voice.IsEnabled && voice.Denoise > 0 && NoiseReductionCheck.IsChecked == true)
        {
            notes.Add("A redução de ruído do fone (acima) também está ligada: desligue uma das duas para a voz não ser processada duas vezes.");
        }
        VoiceInfoText.Text = string.Join(" ", notes);
    }

    private async void OnMicTestClick(object sender, RoutedEventArgs e)
    {
        if (isTestingMicrophone)
        {
            return;
        }
        isTestingMicrophone = true;
        MicTestButton.IsEnabled = false;
        MicTestText.Text = "Fale agora: grava 5 s e reproduz em seguida…";
        try
        {
            await headsetAudio.RecordAndPlayAsync(TimeSpan.FromSeconds(5));
            MicTestText.Text = "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException)
        {
            MicTestText.Text = ex.Message;
        }
        finally
        {
            isTestingMicrophone = false;
            MicTestButton.IsEnabled = true;
        }
    }
}
