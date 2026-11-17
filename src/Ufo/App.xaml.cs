using System.Windows;

namespace Ufo;

public partial class App : Application
{
    private Mutex? singleInstance;

    /// <summary>One UFO at a time: two would fight over the global hotkeys and record the screen twice.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        singleInstance = new Mutex(initiallyOwned: true, "UFO.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            // ponytail: the second launch just leaves; bringing the first window forward needs a message to it.
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
