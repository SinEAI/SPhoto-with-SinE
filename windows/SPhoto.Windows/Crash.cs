using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SPhoto;

/// <summary>
/// Reports a fatal error. A WinUI app that throws during startup otherwise
/// exits without any sign, so write a log and show a plain Win32 message box,
/// which works even when XAML itself failed to load.
/// </summary>
internal static class Crash
{
    internal static string LogPath =>
        Path.Combine(Services.PhotoLibrary.DataDir, "crash.log");

    /// Writes a non-fatal error to the log without telling the user.
    internal static void Log(object? error)
    {
        var text = $"{DateTime.Now:O}\r\n{error}\r\n\r\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, text);
        }
        catch
        {
            // Nothing more can be done.
        }
    }

    internal static void Report(object? error)
    {
        Log(error);
        // CI launches the app unattended and reads the log instead.
        if (Environment.GetEnvironmentVariable("SPHOTO_NO_CRASH_DIALOG") is null)
        {
            MessageBoxW(IntPtr.Zero, $"SPhoto 启动失败，详情已写入：\n{LogPath}\n\n{error}",
                        "SPhoto", 0x10 /* MB_ICONERROR */);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
