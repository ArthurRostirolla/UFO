using System.IO;
using Microsoft.Win32;

namespace Ufo;

/// <summary>
/// Starts UFO with Windows through the per-user Run key. On by default; turning it off leaves a
/// marker in %AppData%\UFO so later launches respect the choice instead of re-registering.
/// </summary>
public static class WindowsStartup
{
    public const string LaunchArgument = "--startup";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "UFO";

    private static readonly string OptOutMarker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UFO", "startup-off");

    public static bool IsWanted => !File.Exists(OptOutMarker);

    /// <summary>True when Windows launched this process at sign-in.</summary>
    public static bool IsStartupLaunch => Environment.GetCommandLineArgs().Contains(LaunchArgument);

    /// <summary>Keeps the Run entry pointing at the executable that is running now (it may have moved).</summary>
    public static void Sync()
    {
        // Development builds (bin\Debug, bin\Release) must not take over the entry of the installed copy.
        bool isDevelopmentBuild = Environment.ProcessPath?.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) == true;
        if (IsWanted && !isDevelopmentBuild)
        {
            Register();
        }
    }

    public static void SetWanted(bool isWanted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(OptOutMarker) ?? ".");
        if (isWanted)
        {
            File.Delete(OptOutMarker);
            Register();
        }
        else
        {
            File.WriteAllText(OptOutMarker, "");
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    private static void Register()
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return;
        }
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        key.SetValue(ValueName, $"\"{executable}\" {LaunchArgument}");
    }
}
