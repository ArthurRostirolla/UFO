using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Ufo.Audio;

namespace Ufo;

/// <summary>
/// "Verificar" in settings: an on-demand check of headset, output, engine and microphone, one line
/// per finding with what to do about it. Nothing here runs by itself: it plays a short tone and
/// records the microphone, so it only starts when the user asks.
/// </summary>
public partial class MainWindow
{
    private enum CheckLevel { Ok, Info, Warning, Failure }

    private bool isVerifying;

    private async void OnVerifyClick(object sender, RoutedEventArgs e)
    {
        if (isVerifying || isCalibrating || isAnalyzing || isTestingMicrophone)
        {
            return;
        }
        isVerifying = true;
        VerifyButton.IsEnabled = false;
        CheckResults.Children.Clear();
        try
        {
            AddCheck(isHeadsetLinked ? CheckLevel.Ok : CheckLevel.Failure, "Fone",
                isHeadsetLinked ? "Conectado e respondendo." : "Não responde. Ligue o fone e confira o dongle ou o cabo USB-C.");
            if (CheckAudioOption.IsChecked == true)
            {
                await VerifyOutputAsync();
            }
            if (CheckMicrophoneOption.IsChecked == true)
            {
                await VerifyMicrophoneAsync();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or IOException)
        {
            AddCheck(CheckLevel.Failure, "Verificação interrompida", ex.Message);
        }
        finally
        {
            headsetAudio.StopTone();
            VerifyStepText.Text = "";
            VerifyButton.IsEnabled = true;
            isVerifying = false;
        }
    }

    private async Task VerifyOutputAsync()
    {
        if (headsetAudio.OutputInfo() is not { } output)
        {
            AddCheck(CheckLevel.Failure, "Saída de som", "O Windows não vê a saída do fone. Reconecte o dongle ou o cabo.");
            return;
        }
        AddCheck(output.IsDefault ? CheckLevel.Ok : CheckLevel.Warning, "Saída de som", output.IsDefault
            ? $"{output.Name} é a saída padrão do Windows."
            : $"{output.Name} existe, mas o Windows está tocando em outro dispositivo. Escolha o fone no ícone de som da barra de tarefas.");
        AddCheck(output.SampleRate >= 48000 && output.Bits >= 24 ? CheckLevel.Ok : CheckLevel.Warning, "Formato",
            $"{output.SampleRate / 1000.0:0.#} kHz, {output.Bits} bits" + (output.SampleRate >= 48000 && output.Bits >= 24
                ? ": o máximo que o fone aceita."
                : ". O fone aceita 48 kHz e 24 bits: ajuste em Som > Propriedades do dispositivo > Formato."));
        if (output.IsMuted || output.VolumeDb < -40)
        {
            AddCheck(CheckLevel.Warning, "Volume", output.IsMuted ? "O fone está mudo no Windows." : "O volume do Windows está quase no zero.");
        }

        if (!M274Client.IsInstalled())
        {
            AddCheck(CheckLevel.Failure, "M274", M274Client.WasInstalled()
                ? "O Windows desligou o M274 do fone. Use Reparar, logo acima."
                : "Não instalado: equalizador e ajustes de Som não têm efeito. Use Instalar M274, logo acima.");
            return;
        }
        // A quiet tone through the normal Windows path: if the engine's block counter moves, it is in the chain.
        VerifyStepText.Text = "Tocando um tom curto no fone…";
        uint blocksBefore = engine.ReadMeters()?.Blocks ?? 0;
        headsetAudio.PlayTone(1000, -30);
        await Task.Delay(1200);
        M274Meters? meters = engine.ReadMeters();
        headsetAudio.StopTone();
        if (meters is null || meters.Blocks == blocksBefore)
        {
            AddCheck(CheckLevel.Failure, "M274", "Instalado, mas o som não passou por ele. Use Reinstalar, logo acima; se continuar, reinicie o PC.");
            return;
        }
        double limiterDb = 20 * Math.Log10(Math.Max(meters.LimiterGain, 1e-4));
        AddCheck(CheckLevel.Ok, "M274",
            $"Processando em {meters.SampleRate / 1000:0.#} kHz, atraso de {meters.LatencyFrames * 1000.0 / Math.Max(meters.SampleRate, 1):0.0} ms, perfil {profiles[activeProfile].Name}.");
        if (limiterDb < -6)
        {
            AddCheck(CheckLevel.Warning, "Limitador", $"Está segurando {-limiterDb:0} dB: o som está sendo empurrado demais. Baixe \"Volume do perfil\" ou os ganhos da curva.");
        }
    }

    private async Task VerifyMicrophoneAsync()
    {
        if (headsetAudio.MicrophoneInfo() is not { } microphone)
        {
            AddCheck(CheckLevel.Failure, "Microfone", "O Windows não vê o microfone do fone. Reconecte o dongle ou o cabo.");
            return;
        }
        AddCheck(microphone.IsDefault ? CheckLevel.Ok : CheckLevel.Warning, "Microfone", microphone.IsDefault
            ? $"{microphone.Name} é o microfone padrão do Windows."
            : $"{microphone.Name} existe, mas o padrão do Windows é outro. Escolha o do fone em Configurações > Som > Entrada.");
        AddCheck(CheckLevel.Info, "Qualidade do link", microphone.SampleRate <= 16000
            ? "Sem fio: 16 kHz (voz até 8 kHz). No cabo USB-C sobe para 48 kHz."
            : $"{microphone.SampleRate / 1000.0:0.#} kHz: a melhor qualidade deste microfone.");
        if (isHeadsetLinked && MicEnabledCheck.IsChecked != true)
        {
            AddCheck(CheckLevel.Warning, "Microfone desligado", "Está desligado no fone. Ligue em Microfone > Microfone ativo.");
        }
        if (microphone.IsMuted)
        {
            AddCheck(CheckLevel.Warning, "Microfone mudo", "Está mudo no Windows.");
        }
        if (!M274Client.IsMicrophoneInstalled())
        {
            AddCheck(CheckLevel.Warning, "M274 no microfone", "Não está ligado: o processamento de voz não tem efeito. Use Instalar ou Reinstalar, logo acima.");
        }
        if (voice.IsEnabled && voice.Denoise > 0 && NoiseReductionCheck.IsChecked == true)
        {
            AddCheck(CheckLevel.Warning, "Redução de ruído em dobro", "Está ligada no fone e no app. Desligue uma das duas, ou a voz sai processada duas vezes.");
        }

        VerifyStepText.Text = "Fique em silêncio por 2 segundos…";
        MicrophoneLevel silence = await headsetAudio.MeasureMicrophoneAsync(TimeSpan.FromSeconds(2));
        VerifyStepText.Text = "Agora fale normalmente por 3 segundos…";
        MicrophoneLevel speech = await headsetAudio.MeasureMicrophoneAsync(TimeSpan.FromSeconds(3));

        if (speech.PeakDb < -80)
        {
            AddCheck(CheckLevel.Failure, "Captação", "Nenhum som chegou do microfone. Confira se ele está ligado e encaixado.");
            return;
        }
        AddCheck(silence.RmsDb > -45 ? CheckLevel.Warning : CheckLevel.Ok, "Ruído de fundo", silence.RmsDb > -45
            ? $"Alto ({silence.RmsDb:0} dB). Aumente \"Redução de ruído\" e \"Silenciar pausas\" em Microfone."
            : $"Baixo ({silence.RmsDb:0} dB).");
        (CheckLevel level, string detail) = speech.PeakDb switch
        {
            >= -0.5 => (CheckLevel.Warning, $"Estourando (pico {speech.PeakDb:0.0} dB). Baixe o \"Ganho\" em Microfone ou afaste o microfone."),
            < -30 => (CheckLevel.Warning, $"Voz muito baixa (pico {speech.PeakDb:0} dB). Aproxime o microfone ou aumente \"Ganho\" e \"Nivelador\"."),
            _ => speech.RmsDb - silence.RmsDb < 12
                ? (CheckLevel.Warning, "A voz mal se destaca do ruído. Se você falou, aproxime o microfone e ligue a redução de ruído.")
                : (CheckLevel.Ok, $"Voz clara: pico {speech.PeakDb:0} dB, {speech.RmsDb - silence.RmsDb:0} dB acima do ruído."),
        };
        AddCheck(level, "Voz", detail);
    }

    private void AddCheck(CheckLevel level, string title, string detail)
    {
        (string mark, string brush) = level switch
        {
            CheckLevel.Ok => ("✓", "InkBrush"),
            CheckLevel.Info => ("i", "MutedBrush"),
            CheckLevel.Warning => ("!", "LowBrush"),
            _ => ("✕", "ErrorBrush"),
        };
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var markText = new TextBlock { Text = mark, FontSize = 15, FontWeight = FontWeights.Bold };
        markText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        text.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("Hint"), FontSize = 13, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(text, 1);
        row.Children.Add(markText);
        row.Children.Add(text);
        // One name for screen readers: the verdict, then the finding.
        string verdict = level switch { CheckLevel.Ok => "certo", CheckLevel.Info => "informação", CheckLevel.Warning => "atenção", _ => "problema" };
        System.Windows.Automation.AutomationProperties.SetName(row, $"{title}: {verdict}. {detail}");
        CheckResults.Children.Add(row);
    }
}
