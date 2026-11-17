using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

// ufo-enginectl install | remove
//
// Installs or removes the M274 for the AW Pro headset outputs. Runs elevated (UAC), started by
// the app when the user clicks the button. Everything it changes is recorded first in
// C:\ProgramData\UFO\engine-backup.json, and "remove" puts each value back exactly as it was.
//
// What "install" changes:
//   1. copies UfoApo.dll to C:\Program Files\UFO\Engine and registers it (regsvr32);
//   2. on each AWPRO output and microphone, points the mode-effect slot (MFX) at the engine;
//   3. sets DisableProtectedAudioDG=1 so Windows loads an unsigned effect (same switch Equalizer APO uses);
//   4. lets the audio service read/write C:\ProgramData\UFO;
//   5. restarts the Windows audio service (sound drops for a second).

const string RenderClsid = "{7C1F2D3A-5B6E-4A91-9C0D-55464F454E01}";
const string CaptureClsid = "{7C1F2D3A-5B6E-4A91-9C0D-55464F454E02}";
const string ModeEffectValue = "{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},6";
const string ModeListValue = "{d3993a3f-99c2-4402-b5ec-a92a0367664b},6";
const string DefaultMode = "{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}";
const string FriendlyNameValue = "{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
const string RenderDevicesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
const string CaptureDevicesKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
const string AudioKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Audio";
// The first build wrote the flag here by mistake (Windows ignores it); cleaned up on install and remove.
const string MistakenAudioKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Audio";
const string ProtectedFlag = "DisableProtectedAudioDG";
const string DataDirectory = @"C:\ProgramData\UFO";
string installDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UFO", "Engine");
string installedDll = Path.Combine(installDirectory, "UfoApo.dll");
string backupPath = Path.Combine(DataDirectory, "engine-backup.json");
string logPath = Path.Combine(DataDirectory, "enginectl.log");

Directory.CreateDirectory(DataDirectory);
try
{
    switch (args.FirstOrDefault())
    {
        case "install": Install(); break;
        case "remove": Remove(); break;
        case "start-audio": break; // only the "finally" below: turns the audio service back on
        default:
            Log("uso: ufo-enginectl install | remove | start-audio");
            return 2;
    }
    return 0;
}
catch (Exception ex)
{
    Log($"ERRO: {ex.Message}");
    return 1;
}
finally
{
    // Never leave the PC without sound: a failure half-way must not skip this
    // (starting a service that is already running is a harmless no-op).
    Run("net", "start audiosrv");
}

void Install()
{
    string sourceDll = Path.Combine(AppContext.BaseDirectory, "UfoApo.dll");
    if (!File.Exists(sourceDll))
    {
        throw new FileNotFoundException("UfoApo.dll não está ao lado do instalador.", sourceDll);
    }

    // Record the original values once; a second install must not overwrite them with ours.
    Backup backup = LoadBackup() ?? new Backup(null, [], ReadProtectedFlag());
    DeleteMistakenFlag();
    Dictionary<string, EndpointBackup> microphones = backup.Microphones ?? [];
    backup = backup with { Microphones = microphones };
    foreach ((string devicesKey, Dictionary<string, EndpointBackup> originals) in new[] { (RenderDevicesKey, backup.Endpoints), (CaptureDevicesKey, microphones) })
    {
        using RegistryKey devices = OpenDevices(devicesKey);
        foreach (string id in HeadsetEndpoints(devices))
        {
            if (!originals.ContainsKey(id))
            {
                using RegistryKey? effects = devices.OpenSubKey($@"{id}\FxProperties");
                originals[id] = new EndpointBackup(effects?.GetValue(ModeEffectValue) as string, effects?.GetValue(ModeListValue) as string[]);
            }
        }
    }
    if (backup.Endpoints.Count == 0)
    {
        throw new InvalidOperationException("Nenhuma saída AWPRO encontrada. Conecte o dongle ou o cabo e tente de novo.");
    }
    File.WriteAllText(backupPath, JsonSerializer.Serialize(backup));

    Directory.CreateDirectory(installDirectory);
    Run("net", "stop audiosrv /y");                       // releases the DLL if a previous version is loaded
    File.Copy(sourceDll, installedDll, overwrite: true);
    Run("regsvr32", $"/s \"{installedDll}\"");
    // Users (the app), LOCAL SERVICE and write-restricted services (audiodg) may modify the data folder.
    Run("icacls", $"\"{DataDirectory}\" /grant *S-1-5-32-545:(OI)(CI)M *S-1-5-19:(OI)(CI)M *S-1-5-33:(OI)(CI)M /T /C /Q");

    foreach ((string devicesKey, Dictionary<string, EndpointBackup> originals, string clsid) in
        new[] { (RenderDevicesKey, backup.Endpoints, RenderClsid), (CaptureDevicesKey, microphones, CaptureClsid) })
    {
        using RegistryKey devices = OpenDevices(devicesKey);
        foreach (string id in originals.Keys)
        {
            using RegistryKey? effects = OpenEffects(devices, id);
            if (effects is null)
            {
                continue;
            }
            effects.SetValue(ModeEffectValue, clsid, RegistryValueKind.String);
            if (effects.GetValue(ModeListValue) is not string[] modes || !modes.Contains(DefaultMode, StringComparer.OrdinalIgnoreCase))
            {
                effects.SetValue(ModeListValue, new[] { DefaultMode }, RegistryValueKind.MultiString);
            }
            Log($"M274 ligado em {id}");
        }
    }
    using (RegistryKey audio = Registry.LocalMachine.CreateSubKey(AudioKey))
    {
        audio.SetValue(ProtectedFlag, 1, RegistryValueKind.DWord);
    }
    Run("net", "start audiosrv");
    Log("instalado");
}

void Remove()
{
    Backup? backup = LoadBackup();
    Run("net", "stop audiosrv /y");
    if (backup is not null)
    {
        foreach ((string devicesKey, Dictionary<string, EndpointBackup>? originals) in new[] { (RenderDevicesKey, backup.Endpoints), (CaptureDevicesKey, backup.Microphones) })
        {
            using RegistryKey devices = OpenDevices(devicesKey);
            foreach ((string id, EndpointBackup original) in originals ?? [])
            {
                using RegistryKey? effects = OpenEffects(devices, id);
                if (effects is null)
                {
                    continue;
                }
                Restore(effects, ModeEffectValue, original.ModeEffect, RegistryValueKind.String);
                Restore(effects, ModeListValue, original.ModeList, RegistryValueKind.MultiString);
                Log($"original restaurado em {id}");
            }
        }
        using RegistryKey audio = Registry.LocalMachine.CreateSubKey(AudioKey);
        Restore(audio, ProtectedFlag, backup.OriginalProtectedFlag, RegistryValueKind.DWord);
        DeleteMistakenFlag();
    }
    if (File.Exists(installedDll))
    {
        Run("regsvr32", $"/u /s \"{installedDll}\"");
        File.Delete(installedDll);
    }
    Run("net", "start audiosrv");
    File.Delete(backupPath);
    Log("removido");
}

static void Restore(RegistryKey key, string name, object? original, RegistryValueKind kind)
{
    if (original is null)
    {
        key.DeleteValue(name, throwOnMissingValue: false);
    }
    else
    {
        key.SetValue(name, original, kind);
    }
}

// Administrators hold SetValue and read on FxProperties, not full control: asking for more
// ("writable: true" wants create-subkey too) is refused with "access is not allowed".
static RegistryKey? OpenEffects(RegistryKey devices, string id) =>
    devices.OpenSubKey($@"{id}\FxProperties", RegistryKeyPermissionCheck.ReadWriteSubTree,
        System.Security.AccessControl.RegistryRights.SetValue | System.Security.AccessControl.RegistryRights.QueryValues);

static RegistryKey OpenDevices(string devicesKey) =>
    Registry.LocalMachine.OpenSubKey(devicesKey) ?? throw new InvalidOperationException("Chave de dispositivos de áudio não encontrada.");

static IEnumerable<string> HeadsetEndpoints(RegistryKey devices)
{
    foreach (string id in devices.GetSubKeyNames())
    {
        using RegistryKey? properties = devices.OpenSubKey($@"{id}\Properties");
        if (properties?.GetValue(FriendlyNameValue) is string name && name.Contains("AWPRO", StringComparison.OrdinalIgnoreCase))
        {
            yield return id;
        }
    }
}

static void DeleteMistakenFlag()
{
    using RegistryKey? mistaken = Registry.LocalMachine.OpenSubKey(MistakenAudioKey, writable: true);
    mistaken?.DeleteValue(ProtectedFlag, throwOnMissingValue: false);
}

static int? ReadProtectedFlag()
{
    using RegistryKey? audio = Registry.LocalMachine.OpenSubKey(AudioKey);
    return audio?.GetValue(ProtectedFlag) as int?;
}

Backup? LoadBackup() => File.Exists(backupPath) ? JsonSerializer.Deserialize<Backup>(File.ReadAllText(backupPath)) : null;

void Run(string file, string arguments)
{
    using Process process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false, CreateNoWindow = true })
        ?? throw new InvalidOperationException($"Não foi possível iniciar {file}.");
    process.WaitForExit();
    Log($"{file} {arguments} -> {process.ExitCode}");
}

void Log(string message)
{
    Console.WriteLine(message);
    File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
}

internal sealed record EndpointBackup(string? ModeEffect, string[]? ModeList);

/// <summary>ProtectedFlag is unused (kept so older backup files still load); OriginalProtectedFlag is the real key.</summary>
internal sealed record Backup(int? ProtectedFlag, Dictionary<string, EndpointBackup> Endpoints, int? OriginalProtectedFlag = null, Dictionary<string, EndpointBackup>? Microphones = null);
