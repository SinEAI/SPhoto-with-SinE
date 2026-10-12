using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SPhoto.Services;

/// <summary>
/// Registers SPhoto with Windows as a photo viewer, for the current user
/// only (HKCU; never HKLM), the way an unpackaged app does it:
/// https://learn.microsoft.com/windows/win32/shell/default-programs
/// https://learn.microsoft.com/windows/win32/shell/fa-progids
/// https://learn.microsoft.com/windows/win32/shell/app-registration
/// </summary>
/// <remarks>
/// Windows keys "Open with" entries it makes itself by file name alone
/// (Classes\Applications\SPhoto.exe), so with several builds in different
/// folders it can start a stale one. Every registration here names the exe
/// that is running, and a normal start moves an existing registration to it.
/// </remarks>
internal static class DefaultApp
{
    private const string ProgId = "SPhoto.Photo";

    /// The value name under RegisteredApplications; ms-settings takes it as
    /// registeredAppUser.
    private const string RegisteredName = "SPhoto";
    private const string CapabilitiesPath = @"Software\SPhoto\Capabilities";

    private static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("no process path");

    private static string Command => $"\"{Exe}\" \"%1\"";

    private static string ApplicationsKey => $@"Software\Classes\Applications\{Path.GetFileName(Exe)}";

    /// <summary>
    /// Writes the ProgID, the "Open with" entries for every photo type, the
    /// Default Programs capabilities and the Applications entry, all
    /// pointing at this exe, then tells the shell.
    /// </summary>
    internal static void Register()
    {
        var extensions = PhotoLibrary.Extensions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToArray();
        var icon = $"\"{Exe}\",0";

        using (var progId = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            progId.SetValue("", "SPhoto 照片");
            progId.SetValue("FriendlyTypeName", "SPhoto 照片");
            using (var defaultIcon = progId.CreateSubKey("DefaultIcon")) defaultIcon.SetValue("", icon);
            using (var command = progId.CreateSubKey(@"shell\open\command")) command.SetValue("", Command);
        }
        foreach (var ext in extensions)
        {
            // OpenWithProgids takes an empty REG_NONE value named after the ProgID.
            using var openWith = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            openWith.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        using (var capabilities = Registry.CurrentUser.CreateSubKey(CapabilitiesPath))
        {
            capabilities.SetValue("ApplicationName", "SPhoto");
            capabilities.SetValue("ApplicationDescription", "看照片、管理图库、修图。");
            capabilities.SetValue("ApplicationIcon", icon);
            using var associations = capabilities.CreateSubKey("FileAssociations");
            foreach (var ext in extensions) associations.SetValue(ext, ProgId);
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            registered.SetValue(RegisteredName, CapabilitiesPath);
        }

        WriteApplicationsEntry(extensions);
        NotifyShell();
    }

    /// <summary>
    /// Removes everything Register wrote (the uninstaller runs this), so no
    /// "Open with" entry is left pointing at a deleted exe.
    /// </summary>
    internal static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        foreach (var ext in PhotoLibrary.Extensions)
        {
            using var openWith = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            openWith?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\SPhoto\Capabilities", throwOnMissingSubKey: false);
        using (var registered = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
        {
            registered?.DeleteValue(RegisteredName, throwOnMissingValue: false);
        }
        Registry.CurrentUser.DeleteSubKeyTree(ApplicationsKey, throwOnMissingSubKey: false);
        NotifyShell();
    }

    /// <summary>
    /// When SPhoto was registered from another folder (another build), points
    /// the registration at this exe instead, so the last build started wins.
    /// Does nothing when SPhoto was never registered. Errors are ignored: a
    /// locked-down registry must not stop the app.
    /// </summary>
    internal static void UpdateIfMoved()
    {
        try
        {
            if (IsStale($@"Software\Classes\{ProgId}\shell\open\command"))
            {
                Register();
            }
            else if (IsStale($@"{ApplicationsKey}\shell\open\command"))
            {
                WriteApplicationsEntry(PhotoLibrary.Extensions.ToArray());
                NotifyShell();
            }
        }
        catch (Exception)
        {
            // Not worth an error; the settings button registers again.
        }
    }

    /// <summary>
    /// Opens Windows' default apps settings, at SPhoto's page where Windows
    /// supports it (Windows 11 with the 2023-04 update or later; earlier
    /// versions ignore the parameter and show the list):
    /// https://learn.microsoft.com/windows/apps/develop/launch/launch-default-apps-settings
    /// </summary>
    internal static Uri SettingsUri =>
        new("ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(RegisteredName));

    /// True when the command at key exists and starts another exe.
    private static bool IsStale(string key)
    {
        using var command = Registry.CurrentUser.OpenSubKey(key);
        return command?.GetValue("") is string value &&
               !string.Equals(value.Trim(), Command, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Classes\Applications\SPhoto.exe is the entry "Open with" uses when
    /// the user picked the exe by hand; Windows writes it with whichever
    /// SPhoto.exe that was. Rewritten here to this exe.
    /// </summary>
    private static void WriteApplicationsEntry(string[] extensions)
    {
        using var app = Registry.CurrentUser.CreateSubKey(ApplicationsKey);
        app.SetValue("FriendlyAppName", "SPhoto");
        using (var defaultIcon = app.CreateSubKey("DefaultIcon")) defaultIcon.SetValue("", $"\"{Exe}\",0");
        using (var command = app.CreateSubKey(@"shell\open\command")) command.SetValue("", Command);
        using var supported = app.CreateSubKey("SupportedTypes");
        foreach (var ext in extensions) supported.SetValue(ext, "");
    }

    /// Tells Explorer the associations changed, as Default Programs asks.
    private static void NotifyShell() =>
        SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_DWORD | SHCNF_FLUSH, IntPtr.Zero, IntPtr.Zero);

    private const int SHCNE_ASSOCCHANGED = 0x08000000;
    private const uint SHCNF_DWORD = 0x0003;
    private const uint SHCNF_FLUSH = 0x1000;

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
