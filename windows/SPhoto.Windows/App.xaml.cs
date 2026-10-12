using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SPhoto.Services;
using SPhoto.Ui;

namespace SPhoto;

public partial class App : Application
{
    private static MainWindow? _window;
    private static RenderWorker? _worker;
    internal static TrayStatus? Tray { get; private set; }

    // Keep static initialization at the first actual App access, after
    // Program has configured the isolated test profile.
    static App() { }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            Crash.Report(e.Exception);
            Exit();
        };
    }

    internal static MainWindow Window => _window ?? throw new InvalidOperationException("no window yet");

    internal static PhotoLibrary Library { get; } = new();

    internal static Prefs Prefs { get; } = Prefs.Load();

    /// Renders edits on a thread of its own; started on first use.
    internal static RenderWorker Worker => _worker ??= new RenderWorker();

    /// The design on screen.
    internal static DesignShell? Design => _window?.Design;

    /// Set by "--ui-test LOG": walk every design instead of waiting for a user.
    internal static string? UiTestLog { get; set; }

    /// Checks the original first window against an existing library.
    internal static string? StartupTestLog { get; set; }

    internal static string? EditReturnTestLog { get; set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Native.sp_param_count() != Edit.ParamCount)
        {
            throw new InvalidOperationException(
                $"sphoto_core.dll has {Native.sp_param_count()} adjustments, the app expects {Edit.ParamCount}");
        }
        // "--stress-test LOG [COUNT]": its own window instead of the app's.
        if (StressTest.LogPath is not null)
        {
            StressTest.Start();
            return;
        }

        // Opened from Explorer ("Open with SPhoto", or SPhoto as the default
        // app): show that photo, not the gallery, in a window shaped for it.
        var photo = UiTestLog is null && StartupTestLog is null && EditReturnTestLog is null ? Program.PhotoArgument(Environment.GetCommandLineArgs().Skip(1)) : null;
        _window = new MainWindow(photo);
        _window.Closed += (_, _) =>
        {
            Tray?.Dispose();
            _worker?.Dispose();
            Library.Dispose();
        };
        _window.SetDesign(Designs.Get(Prefs.Design).Create());
        _window.Activate();
        Tray = new TrayStatus(_window, Library);

        if (EditReturnTestLog is { } returnLog)
        {
            _window.DispatcherQueue.TryEnqueue(async () => Environment.Exit(await EditReturnTest.RunAsync(returnLog)));
            return;
        }
        if (StartupTestLog is { } startupLog)
        {
            _window.DispatcherQueue.TryEnqueue(async () => Environment.Exit(await StartupTest.RunAsync(startupLog)));
            return;
        }
        if (UiTestLog is { } log)
        {
            _window.DispatcherQueue.TryEnqueue(async () =>
            {
                // Application.Exit can end the process before Main returns its code.
                Environment.Exit(await UiTest.RunAsync(log));
            });
            return;
        }
        if (photo is not null) Design?.OpenFile(photo, alone: true);
    }

    /// <summary>
    /// A photo another SPhoto start handed over (Program.Redirected): show it
    /// here and bring the window to the front.
    /// </summary>
    internal static void ShowPhoto(string path)
    {
        if (_window is null || Design is not { } design) return;
        design.OpenFile(path);
        _window.BringToFront();
    }

    /// <summary>
    /// Switches to another design, after offering to save unsaved edits.
    /// False when the user stayed.
    /// </summary>
    internal static async Task<bool> SwitchDesignAsync(string id)
    {
        if (_window is null) return false;
        if (Design is { } current)
        {
            if (current.Id == id) return true;
            if (!await current.CanCloseAsync()) return false;
        }
        var next = Designs.Get(id);
        Prefs.Design = next.Id;
        Prefs.Save();
        _window.SetDesign(next.Create());
        return true;
    }
}
