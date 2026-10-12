using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using SPhoto.Services;
using SPhoto.Ui;

namespace SPhoto;

/// <summary>
/// The one window. It holds whichever design the user picked in settings;
/// the design arranges the gallery, viewer and editor inside it.
/// </summary>
public sealed partial class MainWindow : Window
{
    private bool _closing;

    /// Started with a photo from Explorer: the window is shaped for that
    /// photo and its size is not remembered for the library.
    private readonly bool _viewer;

    public MainWindow(string? photo)
    {
        // Read the photo's header while XAML loads.
        var photoSize = photo is null ? null : Task.Run(() => ImageIO.SizeAsync(photo));
        try
        {
            InitializeComponent();
        }
        catch (XamlParseException e)
        {
            // WinUI reports only "XAML parsing failed"; XamlReader names the
            // failing element and line.
            throw new InvalidOperationException($"XAML diagnosis: {DiagnoseXaml()}", e);
        }
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "SPhoto.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        ApplyMaterial();
        _viewer = photoSize is not null && PlaceForPhoto(photoSize);
        if (!_viewer) Place();
        AppWindow.Closing += OnClosing;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// The standard window is 55% of the screen's work area wide and 78%
    /// high, centred. The first start of a day opens at that size; later
    /// starts the same day come back where the user left the window.
    /// </summary>
    private void Place()
    {
        double scale = Math.Max(1, GetDpiForWindow(Hwnd) / 96.0);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            // Below this the sidebar and a useful grid no longer fit.
            presenter.PreferredMinimumWidth = (int)(800 * scale);
            presenter.PreferredMinimumHeight = (int)(560 * scale);
        }
        var prefs = App.Prefs;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        if (prefs.WindowDay == today && prefs.WindowWidth > 0 && prefs.WindowHeight > 0)
        {
            var saved = new RectInt32(prefs.WindowX, prefs.WindowY, prefs.WindowWidth, prefs.WindowHeight);
            // Only where some screen still shows it (a monitor may be gone).
            var there = DisplayArea.GetFromRect(saved, DisplayAreaFallback.None);
            if (there is not null)
            {
                AppWindow.MoveAndResize(saved);
                if (prefs.WindowMaximized && AppWindow.Presenter is OverlappedPresenter p) p.Maximize();
                return;
            }
        }
        int w = (int)(area.Width * 0.55), h = (int)(area.Height * 0.78);
        w = Math.Max(w, (int)(800 * scale));
        h = Math.Max(h, (int)(560 * scale));
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    /// <summary>
    /// Opened with a photo: a window with the photo's shape, 78% of the work
    /// area high (less when that would be wider than 85% of it), at least
    /// 480 x 360 and at most 90% of the work area wide, centred. False when
    /// the photo's size could not be read in time.
    /// </summary>
    private bool PlaceForPhoto(Task<(int Width, int Height)> photoSize)
    {
        int pw, ph;
        try
        {
            // Only the header is read; a slow disk falls back to Place.
            if (!photoSize.Wait(TimeSpan.FromSeconds(2))) return false;
            (pw, ph) = photoSize.Result;
        }
        catch (Exception)
        {
            return false;
        }
        if (pw <= 0 || ph <= 0) return false;

        double scale = Math.Max(1, GetDpiForWindow(Hwnd) / 96.0);
        int minW = (int)(480 * scale), minH = (int)(360 * scale);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = minW;
            presenter.PreferredMinimumHeight = minH;
        }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        double aspect = (double)pw / ph;
        double h = area.Height * 0.78, w = h * aspect;
        if (w > area.Width * 0.85)
        {
            w = area.Width * 0.85;
            h = w / aspect;
        }
        int width = Math.Min(Math.Max((int)w, minW), (int)(area.Width * 0.9));
        int height = Math.Min(Math.Max((int)h, minH), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        return true;
    }

    /// Remembers the window for later starts today.
    private void RememberPlace()
    {
        var prefs = App.Prefs;
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        bool minimized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        prefs.WindowDay = DateTime.Now.ToString("yyyy-MM-dd");
        prefs.WindowMaximized = maximized;
        if (!maximized && !minimized)
        {
            prefs.WindowX = AppWindow.Position.X;
            prefs.WindowY = AppWindow.Position.Y;
            prefs.WindowWidth = AppWindow.Size.Width;
            prefs.WindowHeight = AppWindow.Size.Height;
        }
        prefs.Save();
    }

    /// <summary>
    /// Restores and activates the window. A process can only take the
    /// foreground when allowed; the SPhoto that handed over the photo allows
    /// it (Program.Redirected), as in "Create a single-instanced WinUI app
    /// with C#" on Microsoft Learn.
    /// </summary>
    internal void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }
        Activate();
        SetForegroundWindow(Hwnd);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    internal IntPtr Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    internal DesignShell? Design => Host.Children.Count > 0 ? Host.Children[0] as DesignShell : null;

    /// <summary>
    /// Windows' frosted material behind the window, which the design lets
    /// show through its sidebar: a light, see-through Acrylic. Where
    /// Windows has no Acrylic for the window, a plain light grey instead.
    /// </summary>
    private void ApplyMaterial()
    {
        if (DesktopAcrylicController.IsSupported())
        {
            SystemBackdrop = new SidebarBackdrop();
            Host.Background = null;
        }
        else
        {
            Host.Background = Kit.Chrome;
        }
    }

    internal void SetDesign(DesignShell design)
    {
        Host.Children.Clear();
        Host.Children.Add(design);
        // No separate title bar: the design draws the top strip, and the
        // window's buttons sit over its right end.
        ExtendsContentIntoTitleBar = design.TitleBar is not null;
        if (design.TitleBar is { } bar)
        {
            SetTitleBar(bar);
            AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
    }

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing || Design is not { } design) return;
        args.Cancel = true;
        if (await design.CanCloseAsync())
        {
            _closing = true;
            if (App.UiTestLog is null && !_viewer) RememberPlace();
            Close();
        }
    }

    private static string DiagnoseXaml()
    {
        try
        {
            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("MainWindow.xaml");
            if (stream is null) return "MainWindow.xaml is not embedded";
            var xaml = new StreamReader(stream).ReadToEnd();
            var match = Regex.Match(xaml, @"<Grid[\s\S]*?/>");
            if (!match.Success) return "no Grid in MainWindow.xaml";
            var grid = match.Value.Replace("<Grid",
                "<Grid xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"");
            try { XamlReader.Load(grid); }
            catch (Exception e) { return $"window content: {e.Message}"; }
            return "the window content loads on its own";
        }
        catch (Exception e)
        {
            return $"diagnosis failed: {e.Message}";
        }
    }
}
