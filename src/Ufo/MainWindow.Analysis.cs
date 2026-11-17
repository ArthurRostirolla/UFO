using System.Windows;
using System.Windows.Threading;
using Ufo.Audio;

namespace Ufo;

/// <summary>
/// "Analisar o que está tocando": listens to a playlist through the engine's meters and applies a
/// balancing proposal to the active profile. Started and finished by the user; "Desfazer" puts the
/// previous setup back.
/// </summary>
public partial class MainWindow
{
    private const double AnalysisFrameSeconds = 0.1;

    private readonly DispatcherTimer analysisTimer = new() { Interval = TimeSpan.FromSeconds(AnalysisFrameSeconds) };
    private PlaylistAnalysis? analysis;
    private (EqualizerSettings Equalizer, SoundSettings Sound, int Profile)? beforeAnalysis;
    private uint analysisBlocks;
    private bool isAnalyzing;

    private void InitializeAnalysis()
    {
        analysisTimer.Tick += (_, _) => ListenForAnalysis();
        ShowAnalysis("Deixe uma playlist tocando e o UFO propõe o ajuste que a equilibra neste perfil.");
    }

    private void OnAnalyzeClick(object sender, RoutedEventArgs e)
    {
        if (isCalibrating || isVerifying)
        {
            return;
        }
        if (!isAnalyzing)
        {
            analysis = new PlaylistAnalysis();
            beforeAnalysis = null;
            isAnalyzing = true;
            SendToEngine(); // flat while listening, so what is measured is the music itself
            analysisTimer.Start();
            ShowAnalysis("Ouvindo… dê play na playlist.");
            return;
        }
        if (analysis is null || analysis.SecondsHeard(AnalysisFrameSeconds) < PlaylistAnalysis.MinimumSeconds)
        {
            return;
        }
        PlaylistProposal proposal = analysis.Propose();
        StopAnalysis();
        beforeAnalysis = (equalizer, sound, activeProfile);
        equalizer = equalizer with { IsEnabled = true, PresetName = Equalizer.CustomPresetName, Bands = proposal.Bands };
        sound = sound with { TrimDb = proposal.TrimDb, Night = proposal.Night };
        selectedBand = -1;
        ShowEqualizer();
        ShowSound();
        ScheduleEngineSend();
        ShowAnalysis($"Aplicado em {profiles[activeProfile].Name}: {proposal.Summary}. Use \"Segure para comparar\" para ouvir a diferença e Salvar para manter.");
    }

    /// <summary>While listening this cancels; after a proposal it undoes it.</summary>
    private void OnAnalysisCancelClick(object sender, RoutedEventArgs e)
    {
        if (isAnalyzing)
        {
            StopAnalysis();
            SendToEngine();
            ShowAnalysis("Análise cancelada.");
            return;
        }
        if (beforeAnalysis is { } before && before.Profile == activeProfile)
        {
            equalizer = before.Equalizer;
            sound = before.Sound;
            selectedBand = -1;
            ShowEqualizer();
            ShowSound();
            ScheduleEngineSend();
        }
        beforeAnalysis = null;
        ShowAnalysis("Ajuste anterior restaurado.");
    }

    private void StopAnalysis()
    {
        analysisTimer.Stop();
        isAnalyzing = false;
    }

    private void ListenForAnalysis()
    {
        if (analysis is null || engine.ReadMeters() is not { } meters)
        {
            return;
        }
        if (meters.Blocks != analysisBlocks) // the engine only counts blocks while audio flows
        {
            analysisBlocks = meters.Blocks;
            analysis.Add(meters.Spectrum, Math.Max(meters.PeakLeft, meters.PeakRight));
        }
        var heard = TimeSpan.FromSeconds(analysis.SecondsHeard(AnalysisFrameSeconds));
        ShowAnalysis(heard.TotalSeconds < 1
            ? "Ouvindo… dê play na playlist."
            : $"Ouvindo… {heard:m\\:ss}. Pule faixas à vontade: quanto mais músicas, melhor. O som fica sem ajustes enquanto isso.");
    }

    private void ShowAnalysis(string message)
    {
        bool canFinish = analysis is not null && analysis.SecondsHeard(AnalysisFrameSeconds) >= PlaylistAnalysis.MinimumSeconds;
        AnalysisText.Text = message;
        AnalyzeButton.Content = isAnalyzing ? canFinish ? "Concluir e aplicar" : "Ouvindo…" : "Analisar o que está tocando";
        AnalyzeButton.IsEnabled = !isAnalyzing || canFinish;
        AnalysisCancelButton.Content = isAnalyzing ? "Cancelar" : "Desfazer";
        AnalysisCancelButton.Visibility = isAnalyzing || beforeAnalysis is not null ? Visibility.Visible : Visibility.Collapsed;
    }
}
