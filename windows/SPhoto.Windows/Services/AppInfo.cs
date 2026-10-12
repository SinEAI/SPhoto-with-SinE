using System.Reflection;

namespace SPhoto.Services;

/// <summary>The app's own version, as the build stamped it.</summary>
internal static class AppInfo
{
    /// <summary>
    /// "0.2.0" locally, "0.2.0.&lt;run&gt;" from CI (SPhoto.Windows.csproj,
    /// ci.yml). The SDK may append "+commit" to the informational version;
    /// that part is dropped.
    /// </summary>
    internal static string Version { get; } = Read();

    private static string Read()
    {
        var assembly = typeof(AppInfo).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version
            ?? "";
        int plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
