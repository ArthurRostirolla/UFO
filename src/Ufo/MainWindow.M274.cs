using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Ufo.Audio;

namespace Ufo;

/// <summary>
/// M274 controls: install/remove (through the elevated m274ctl, so every system change
/// goes through a UAC prompt the user approves) and a live readout proving the M274 is processing.
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer m274Timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer spectrumTimer = new() { Interval = TimeSpan.FromMilliseconds(66) };
    private uint lastM274Blocks, lastMicrophoneBlocks;
    private bool isM274Running;
    private bool isM274Busy;
    private string? m274Failure;
    private string busyLabel = "Instalando…";

    private static string M274CtlPath => Path.Combine(AppContext.BaseDirectory, "m274", "m274ctl.exe");

    private void InitializeM274()
    {
        m274Timer.Tick += (_, _) => ShowM274State();
        m274Timer.Start();
        // The spectrum needs a faster beat than the state readout, and only while it is on screen.
        spectrumTimer.Tick += (_, _) => DrawSpectrum(isM274Running && EqPanel.IsVisible ? m274.ReadMeters()?.Spectrum : null);
        spectrumTimer.Start();
        ShowM274State();
    }

    private void ShowM274State()
    {
        bool isInstalled = M274Client.IsInstalled();
        M274Meters? meters = isInstalled ? m274.ReadMeters() : null;
        bool isRunning = meters is not null && meters.Blocks != lastM274Blocks;
        lastM274Blocks = meters?.Blocks ?? 0;
        isM274Running = isRunning;

        // Live microphone level while its screen is open and nothing else is using the line.
        if (MicPanel.IsVisible && !isTestingMicrophone && m274.ReadMicrophoneMeters() is { } microphone)
        {
            MicTestText.Text = microphone.Blocks != lastMicrophoneBlocks
                ? $"nível {20 * Math.Log10(Math.Max(Math.Max(microphone.PeakLeft, microphone.PeakRight), 1e-4)):0} dB"
                : "";
            lastMicrophoneBlocks = microphone.Blocks;
        }

        // A Windows or driver update can detach the M274: say so and offer the one-click repair.
        bool isDetached = !isInstalled && M274Client.WasInstalled();
        EqGateTitle.Text = isDetached ? "O Windows desligou o M274" : "O equalizador precisa do M274";
        if (m274Failure is null)
        {
            EqGateText.Text = isDetached
                ? "Uma atualização do Windows ou do driver tirou o M274 do fone. Reparar o religa em segundos; seus ajustes continuam salvos."
                : "É ele que aplica a curva ao som do fone. A instalação pede permissão de administrador e reinicia o áudio do Windows por um instante.";
        }

        // Equalizer screen: locked and blurred behind the install prompt until the M274 is there.
        EqInstallGate.Visibility = isInstalled ? Visibility.Collapsed : Visibility.Visible;
        EqContent.IsEnabled = isInstalled;
        EqContent.Opacity = isInstalled ? 1 : 0.45;
        EqBlur.Radius = isInstalled ? 0 : 7;
        if (m274Failure is not null)
        {
            EqGateText.Text = m274Failure;
        }

        EqM274Text.Text = isRunning && meters is not null
            ? $"M274 ativo · {meters.SampleRate / 1000:0.#} kHz · reserva {meters.HeadroomDb:0.0} dB · limitador {20 * Math.Log10(Math.Max(meters.LimiterGain, 1e-4)):0.0} dB · {meters.LatencyFrames * 1000.0 / Math.Max(meters.SampleRate, 1):0.0} ms"
            : isInstalled ? "M274 instalado, aguardando áudio no fone." : "";
        if (!isM274Busy)
        {
            M274StateText.Text = m274Failure ?? (!isInstalled ? "Não instalado" : isRunning ? "Ativo" : "Instalado, sem áudio tocando");
        }
        // Loading state from the click until the helper exits (UAC prompt included).
        string idleLabel = isInstalled ? "Reinstalar" : isDetached ? "Reparar M274" : "Instalar M274";
        InstallM274Button.Content = isM274Busy ? busyLabel : idleLabel;
        EqGateInstallButton.Content = isM274Busy ? busyLabel : idleLabel;
        RemoveM274Button.IsEnabled = isInstalled && !isM274Busy;
        InstallM274Button.IsEnabled = EqGateInstallButton.IsEnabled = !isM274Busy;
        M274Progress.Visibility = EqGateProgress.Visibility = isM274Busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnInstallM274Click(object sender, RoutedEventArgs e) => RunM274Ctl("install");

    private void OnRemoveM274Click(object sender, RoutedEventArgs e) => RunM274Ctl("remove");

    /// <summary>Starts the elevated helper; Windows shows the UAC prompt and the user decides.</summary>
    private async void RunM274Ctl(string verb)
    {
        if (isM274Busy)
        {
            return;
        }
        if (!File.Exists(M274CtlPath))
        {
            m274Failure = "Instalador do M274 não encontrado nesta cópia do app.";
            ShowM274State();
            return;
        }
        isM274Busy = true;
        m274Failure = null;
        busyLabel = verb == "remove" ? "Removendo…" : "Instalando…";
        M274StateText.Text = "Aguardando a permissão do Windows…";
        ShowM274State();
        try
        {
            // Off the UI thread: Process.Start blocks while the UAC prompt is open.
            using Process? process = await Task.Run(() =>
                Process.Start(new ProcessStartInfo(M274CtlPath, verb) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }));
            if (process is null)
            {
                return;
            }
            M274StateText.Text = "Aplicando… o som do PC para por um instante.";
            await process.WaitForExitAsync();
            m274Failure = process.ExitCode == 0 ? null : $"A instalação falhou. Detalhes em {M274Client.DataDirectory}\\m274ctl.log";
            if (process.ExitCode == 0)
            {
                SendToM274();
                SendVoiceToM274();
            }
        }
        catch (Win32Exception)
        {
            // The user declined the UAC prompt: nothing changed.
        }
        finally
        {
            isM274Busy = false;
            ShowM274State();
        }
    }
}
