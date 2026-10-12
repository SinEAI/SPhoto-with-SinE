using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SPhoto.Services;

namespace SPhoto;

/// <summary>
/// Replaces the XAML-generated Main (DISABLE_XAML_GENERATED_MAIN) so that an
/// exception while XAML starts up is reported instead of ending the process
/// silently. It also hands a photo opened from Explorer to the SPhoto that is
/// already running, unless the user wants a new window for it.
/// </summary>
public static class Program
{
    /// What Main returns once the app exits (the UI test sets it).
    internal static int ExitCode { get; set; }

    /// The key the first normally started SPhoto registers; later photo
    /// starts hand their photo to it.
    private const string MainInstanceKey = "sphoto-main";

    private static DispatcherQueue? _ui;

    [STAThread]
    private static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash.Report(e.ExceptionObject);
        try
        {
            if (args.Length > 0 && args[0] == "--runtime-test")
            {
                return RuntimeTest.Run(args.Length > 1 ? args[1] : "runtime-results.json");
            }
            if (args.Length > 1 && args[0] == "--runtime-probe")
            {
                return RuntimeTest.Probe(args[1]);
            }
            if (args.Length > 0 && args[0] == "--self-test")
            {
                return SelfTest.Run(args.Length > 1 ? args[1] : "self-test.log");
            }
            // The installer registers SPhoto for "Open with" and Default
            // apps after copying it, and removes that again on uninstall.
            if (args.Length > 0 && args[0] is "--register" or "--unregister")
            {
                try
                {
                    if (args[0] == "--register") DefaultApp.Register();
                    else DefaultApp.Unregister();
                    return 0;
                }
                catch (Exception e)
                {
                    Crash.Log(e);
                    return 1;
                }
            }
            // App's static fields create the library and read preferences.
            // Prepare the test profile before touching any App property.
            if (args.Length > 0 && args[0] is "--ui-test" or "--edit-return-test" or "--startup-test")
            {
                if (!PrepareUiTestData(args)) return 2;
            }
            bool test = false;
            if (args.Length > 0 && args[0] == "--edit-return-test")
            {
                App.EditReturnTestLog = Path.GetFullPath(args.Length > 1 ? args[1] : "edit-return-test.log");
                test = true;
            }
            if (args.Length > 0 && args[0] == "--startup-test")
            {
                App.StartupTestLog = Path.GetFullPath(args.Length > 1 ? args[1] : "startup-test.log");
                test = true;
            }
            if (args.Length > 0 && args[0] == "--ui-test")
            {
                App.UiTestLog = Path.GetFullPath(args.Length > 1 ? args[1] : "ui-test.log");
                test = true;
            }
            if (args.Length > 0 && args[0] == "--stress-test")
            {
                StressTest.Configure(args.Length > 1 ? args[1] : "stress-test.log", args.Length > 2 ? args[2] : null);
                test = true;
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            if (!test)
            {
                if (Redirected(PhotoArgument(args))) return 0;
                // The last build started owns "Open with"; off the UI thread.
                Task.Run(DefaultApp.UpdateIfMoved);
            }
            Application.Start(p =>
            {
                _ui = DispatcherQueue.GetForCurrentThread();
                var context = new DispatcherQueueSynchronizationContext(_ui);
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
            return ExitCode;
        }
        catch (Exception e)
        {
            Crash.Report(e);
            return 1;
        }
    }

    private static bool PrepareUiTestData(string[] args)
    {
        bool startup = args[0] == "--startup-test";
        var data = Environment.GetEnvironmentVariable("SPHOTO_DATA_DIR");
        string? error = null;
        if (string.IsNullOrWhiteSpace(data))
        {
            if (startup) error = "startup test requires SPHOTO_DATA_DIR containing an indexed fixture library";
            else data = Path.Combine(Path.GetTempPath(), "SPhotoTest-" + Guid.NewGuid().ToString("N"));
        }
        if (error is null)
        {
            data = Path.GetFullPath(data!);
            var normal = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SPhoto"));
            if (string.Equals(Path.TrimEndingDirectorySeparator(data), Path.TrimEndingDirectorySeparator(normal), StringComparison.OrdinalIgnoreCase))
                error = "UI tests require a separate fixture profile, not the normal app data directory";
            else if (startup && (!File.Exists(Path.Combine(data, "settings.json")) || !File.Exists(Path.Combine(data, "library.db"))))
                error = "startup test requires settings.json and library.db from an indexed fixture library";
        }
        if (error is not null)
        {
            var log = Path.GetFullPath(args.Length > 1 ? args[1] : args[0][2..] + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, "FAILED: " + error + Environment.NewLine);
            return false;
        }
        Environment.SetEnvironmentVariable("SPHOTO_DATA_DIR", data);
        Directory.CreateDirectory(data!);
        var settings = Path.Combine(data!, "settings.json");
        if (!File.Exists(settings)) File.WriteAllText(settings, "{\"Folders\":[]}");
        return true;
    }

    /// <summary>
    /// Claims the main-instance key, or, when another SPhoto holds it and a
    /// photo should open there, sends this activation to it. True when this
    /// process is done; false (open here) also when the hand-over fails.
    /// Follows "Create a single-instanced WinUI app with
    /// C#": https://learn.microsoft.com/windows/apps/windows-app-sdk/applifecycle/applifecycle-single-instance
    /// </summary>
    private static bool Redirected(string? photo)
    {
        AppInstance key;
        try
        {
            key = AppInstance.FindOrRegisterForKey(MainInstanceKey);
        }
        catch (Exception)
        {
            return false;
        }
        if (key.IsCurrent)
        {
            key.Activated += OnActivated;
            return false;
        }
        if (photo is null || Prefs.Load().OpenInNewWindow) return false;

        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        // Redirect on another thread and wait without blocking COM: an STA
        // Main must keep pumping while the other process takes the call.
        var done = CreateEvent(IntPtr.Zero, true, false, null);
        bool sent = false;
        Task.Run(() =>
        {
            try
            {
                key.RedirectActivationToAsync(args).AsTask().Wait();
                sent = true;
            }
            catch (Exception)
            {
                // The other SPhoto is closing or hung: open the photo here.
            }
            finally
            {
                SetEvent(done);
            }
        });
        _ = CoWaitForMultipleObjects(0 /* CWMO_DEFAULT */, 0xFFFFFFFF /* INFINITE */, 1, new[] { done }, out _);
        CloseHandle(done);
        if (!sent) return false;

        // This process was started by Explorer, so it may give the
        // foreground away; the other window cannot take it by itself.
        try
        {
            var process = Process.GetProcessById((int)key.ProcessId);
            AllowSetForegroundWindow(process.Id);
            SetForegroundWindow(process.MainWindowHandle);
        }
        catch (Exception)
        {
            // The window still opens the photo, just maybe behind.
        }
        return true;
    }

    /// <summary>
    /// Another SPhoto handed over its start (on a thread pool thread): open
    /// its photo here.
    /// </summary>
    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        if (args.Kind != ExtendedActivationKind.Launch ||
            args.Data is not Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch ||
            PhotoArgument(SplitCommandLine(launch.Arguments)) is not { } photo)
        {
            return;
        }
        _ui?.TryEnqueue(() => App.ShowPhoto(photo));
    }

    /// The first argument that is a photo on disk, as a full path.
    internal static string? PhotoArgument(IEnumerable<string> args) =>
        args.Select(a => a.Trim('"'))
            .FirstOrDefault(a => PhotoLibrary.IsPhoto(a) && File.Exists(a)) is { } path
            ? Path.GetFullPath(path)
            : null;

    /// Splits a command line the way Windows does for Main's args.
    private static string[] SplitCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return Array.Empty<string>();
        var argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero) return Array.Empty<string>();
        try
        {
            var args = new string[count];
            for (int i = 0; i < count; i++)
            {
                args[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            }
            return args;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles, IntPtr[] pHandles, out uint dwIndex);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
