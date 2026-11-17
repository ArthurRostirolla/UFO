using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Ufo.Audio;

namespace Ufo;

/// <summary>
/// UFO Engine controls: install/remove (through the elevated ufo-enginectl, so every system change
/// goes through a UAC prompt the user approves) and a live readout proving the engine is processing.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer engineTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer spectrumTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private uint lastEngineBlocks, lastMicrophoneBlocks;
    private bool isEngineRunning;
    private bool isEngineBusy;
    private string? engineFailure;
    private string busyLabel = "Instalando…";

    private static string EngineCtlPath => Path.Combine(AppContext.BaseDirectory, "engine", "ufo-enginectl.exe");

    private void InitializeEngine()
    {
        engineTimer.Tick += (_, _) => ShowEngineState();
        engineTimer.Start();
        // The spectrum needs a faster beat than the state readout, and only while it is on screen.
        spectrumTimer.Tick += (_, _) => DrawSpectrum(isEngineRunning && EqPanel.IsVisible ? engine.ReadMeters()?.Spectrum : null);
        spectrumTimer.Start();
        ShowEngineState();
    }

    private void ShowEngineState()
    {
        bool isInstalled = EngineClient.IsInstalled();
        EngineMeters? meters = isInstalled ? engine.ReadMeters() : null;
        bool isRunning = meters is not null && meters.Blocks != lastEngineBlocks;
        lastEngineBlocks = meters?.Blocks ?? 0;
        isEngineRunning = isRunning;

        // Live microphone level while its screen is open and nothing else is using the line.
        if (MicPanel.IsVisible && !isTestingMicrophone && engine.ReadMicrophoneMeters() is { } microphone)
        {
            MicTestText.Text = microphone.Blocks != lastMicrophoneBlocks
                ? $"nível {20 * Math.Log10(Math.Max(Math.Max(microphone.PeakLeft, microphone.PeakRight), 1e-4)):0} dB"
                : "";
            lastMicrophoneBlocks = microphone.Blocks;
        }

        // A Windows or driver update can detach the engine: say so and offer the one-click repair.
        bool isDetached = !isInstalled && EngineClient.WasInstalled();
        EqGateTitle.Text = isDetached ? "O Windows desligou o motor de áudio" : "O equalizador precisa do motor de áudio";
        if (engineFailure is null)
        {
            EqGateText.Text = isDetached
                ? "Uma atualização do Windows ou do driver tirou o motor do fone. Reparar o religa em segundos; seus ajustes continuam salvos."
                : "É ele que aplica a curva ao som do fone. A instalação pede permissão de administrador e reinicia o áudio do Windows por um instante.";
        }

        // Equalizer screen: locked and blurred behind the install prompt until the engine is there.
        EqInstallGate.Visibility = isInstalled ? Visibility.Collapsed : Visibility.Visible;
        EqContent.IsEnabled = isInstalled;
        EqContent.Opacity = isInstalled ? 1 : 0.45;
        EqBlur.Radius = isInstalled ? 0 : 7;
        if (engineFailure is not null)
        {
            EqGateText.Text = engineFailure;
        }

        EqEngineText.Text = isRunning && meters is not null
            ? $"Motor ativo · {meters.SampleRate / 1000:0.#} kHz · reserva {meters.HeadroomDb:0.0} dB · limitador {20 * Math.Log10(Math.Max(meters.LimiterGain, 1e-4)):0.0} dB · {meters.LatencyFrames * 1000.0 / Math.Max(meters.SampleRate, 1):0.0} ms"
            : isInstalled ? "Motor instalado, aguardando áudio no fone." : "";
        if (!isEngineBusy)
        {
            EngineStateText.Text = engineFailure ?? (!isInstalled ? "Não instalado" : isRunning ? "Ativo" : "Instalado, sem áudio tocando");
        }
        // Loading state from the click until the helper exits (UAC prompt included).
        string idleLabel = isInstalled ? "Reinstalar" : isDetached ? "Reparar motor" : "Instalar motor";
        InstallEngineButton.Content = isEngineBusy ? busyLabel : idleLabel;
        EqGateInstallButton.Content = isEngineBusy ? busyLabel : idleLabel;
        RemoveEngineButton.IsEnabled = isInstalled && !isEngineBusy;
        InstallEngineButton.IsEnabled = EqGateInstallButton.IsEnabled = !isEngineBusy;
        EngineProgress.Visibility = EqGateProgress.Visibility = isEngineBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnInstallEngineClick(object sender, RoutedEventArgs e) => RunEngineCtl("install");

    private void OnRemoveEngineClick(object sender, RoutedEventArgs e) => RunEngineCtl("remove");

    /// <summary>Starts the elevated helper; Windows shows the UAC prompt and the user decides.</summary>
    private async void RunEngineCtl(string verb)
    {
        if (isEngineBusy)
        {
            return;
        }
        if (!File.Exists(EngineCtlPath))
        {
            engineFailure = "Instalador do motor não encontrado nesta cópia do app.";
            ShowEngineState();
            return;
        }
        isEngineBusy = true;
        engineFailure = null;
        busyLabel = verb == "remove" ? "Removendo…" : "Instalando…";
        EngineStateText.Text = "Aguardando a permissão do Windows…";
        ShowEngineState();
        try
        {
            // Off the UI thread: Process.Start blocks while the UAC prompt is open.
            using Process? process = await Task.Run(() =>
                Process.Start(new ProcessStartInfo(EngineCtlPath, verb) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }));
            if (process is null)
            {
                return;
            }
            EngineStateText.Text = "Aplicando… o som do PC para por um instante.";
            await process.WaitForExitAsync();
            engineFailure = process.ExitCode == 0 ? null : $"A instalação falhou. Detalhes em {EngineClient.DataDirectory}\\enginectl.log";
            if (process.ExitCode == 0)
            {
                SendToEngine();
                SendVoiceToEngine();
            }
        }
        catch (Win32Exception)
        {
            // The user declined the UAC prompt: nothing changed.
        }
        finally
        {
            isEngineBusy = false;
            ShowEngineState();
        }
    }
}
