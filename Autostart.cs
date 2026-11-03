using System;
using System.IO;
using Microsoft.Win32;

namespace G5Control;

/// <summary>
/// Starts the app at logon through a shortcut in the user's Startup folder. A Run registry entry was skipped by
/// Explorer at logon even with an "enabled" StartupApproved record, while Startup folder shortcuts start reliably.
/// </summary>
static class Autostart
{
    const string Name = "G5Control";
    const string LinkName = Name + ".lnk";
    // Same flags Task Manager toggles under "Startup apps"; an odd first byte means disabled.
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    static readonly byte[] ApprovedEnabled = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    // Version 1.1.x used a Run registry entry.
    const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string LegacyApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    static readonly string ShortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), LinkName);

    public static bool IsEnabled
    {
        get
        {
            if (!File.Exists(ShortcutPath))
            {
                return false;
            }
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(LinkName) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
        }
    }

    public static void Enable()
    {
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        var link = shell.CreateShortcut(ShortcutPath);
        link.TargetPath = Environment.ProcessPath;
        link.Arguments = "--tray";
        link.WorkingDirectory = AppContext.BaseDirectory;
        link.Save();

        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        approved.SetValue(LinkName, ApprovedEnabled, RegistryValueKind.Binary);
        RemoveLegacy();
    }

    public static void Disable()
    {
        File.Delete(ShortcutPath);
        using var approved = Registry.CurrentUser.CreateSubKey(ApprovedKey);
        approved.DeleteValue(LinkName, throwOnMissingValue: false);
        RemoveLegacy();
    }

    /// <summary>Moves an autostart set up by version 1.1.x over to the Startup folder.</summary>
    public static void MigrateLegacy()
    {
        using var run = Registry.CurrentUser.OpenSubKey(LegacyRunKey);
        if (run?.GetValue(Name) != null)
        {
            Enable();
        }
    }

    static void RemoveLegacy()
    {
        using var run = Registry.CurrentUser.CreateSubKey(LegacyRunKey);
        run.DeleteValue(Name, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.CreateSubKey(LegacyApprovedKey);
        approved.DeleteValue(Name, throwOnMissingValue: false);
    }
}
