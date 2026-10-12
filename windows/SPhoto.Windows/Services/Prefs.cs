using System;
using System.IO;
using System.Text.Json;

namespace SPhoto.Services;

/// <summary>App preferences, in prefs.json next to the library settings.</summary>
internal sealed class Prefs
{
    public string Design { get; set; } = "main";

    /// A photo opened from Explorer while SPhoto runs: false shows it in
    /// the running window, true opens another window (Program.Redirected).
    public bool OpenInNewWindow { get; set; }

    /// The window as the user left it, and the day that was: the first
    /// start of a day opens at the standard size again.
    public string? WindowDay { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    private static string FilePath => Path.Combine(PhotoLibrary.DataDir, "prefs.json");

    internal static Prefs Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Prefs>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception)
        {
            // Defaults.
        }
        return new();
    }

    internal void Save()
    {
        try
        {
            Directory.CreateDirectory(PhotoLibrary.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception)
        {
            // Preferences are not worth an error.
        }
    }
}
