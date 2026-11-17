using System.ComponentModel;
using System.Windows;
using Forms = System.Windows.Forms;

namespace Ufo;

/// <summary>
/// Background mode: with "Continuar em segundo plano" on, closing the window only hides it and UFO
/// stays as an icon in the notification area. Double-click the icon to bring it back; right-click,
/// "Fechar" to quit for real.
/// </summary>
public partial class MainWindow
{
    private Forms.NotifyIcon? trayIcon;
    private bool isQuitting;
    private bool hasShownTrayHint;

    /// <summary>The tray icon exists only while background mode is on.</summary>
    private void SyncTrayIcon()
    {
        if (preferences.RunInBackground && trayIcon is null)
        {
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Abrir UFO", null, (_, _) => ShowFromTray());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Fechar", null, (_, _) => QuitFromTray());
            trayIcon = new Forms.NotifyIcon
            {
                Icon = Environment.ProcessPath is { } path ? System.Drawing.Icon.ExtractAssociatedIcon(path) : null,
                Text = "UFO",
                ContextMenuStrip = menu,
                Visible = true,
            };
            trayIcon.DoubleClick += (_, _) => ShowFromTray();
        }
        else if (!preferences.RunInBackground && trayIcon is not null)
        {
            trayIcon.Dispose();
            trayIcon = null;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (preferences.RunInBackground && !isQuitting)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        trayIcon?.Dispose();
        trayIcon = null;
        base.OnClosed(e);
    }

    private void HideToTray()
    {
        SyncTrayIcon();
        Hide();
        if (!hasShownTrayHint && trayIcon is not null)
        {
            // Once per run, say where it went and how to leave.
            hasShownTrayHint = true;
            trayIcon.ShowBalloonTip(4000, "UFO continua aqui", "Clique com o botão direito no ícone e escolha Fechar para sair.", Forms.ToolTipIcon.None);
        }
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
    }

    private void QuitFromTray()
    {
        isQuitting = true;
        Close();
    }
}
