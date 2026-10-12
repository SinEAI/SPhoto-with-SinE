using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SPhoto.Services;

namespace SPhoto.Ui;

/// <summary>
/// One complete front end. Each design arranges the gallery, viewer and
/// editor its own way; they share only the services and building blocks
/// underneath (library, decoding, render worker, edit session, panels).
/// </summary>
public abstract class DesignShell : Grid
{
    private readonly Grid _overlay = new() { Visibility = Visibility.Collapsed };

    protected DesignShell()
    {
        // Clear: the window's material, or white, shows through where the
        // design leaves its parts clear.
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        // Shortcuts (Esc, F5) stay silent: by default XAML shows them as a
        // tooltip over the element that owns them, here the whole page.
        KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        Children.Add(_overlay);
    }

    /// The design's own screens, under the settings overlay.
    protected void SetBody(UIElement body)
    {
        Children.Clear();
        Children.Add(body);
        // Settings start under the window's drag strip and buttons.
        _overlay.Padding = new Thickness(0, TitleBar is FrameworkElement bar ? bar.Height : 0, 0, 0);
        _overlay.Background = Kit.White;
        Children.Add(_overlay);
    }

    internal abstract string Id { get; }

    /// The strip the design draws in place of the window's title bar, if any.
    internal virtual UIElement? TitleBar => null;

    /// Opens one photo file (launched from Explorer); alone when the window
    /// was made for it.
    internal abstract void OpenFile(string path, bool alone = false);

    /// The photo a viewer is showing, if any (for the UI test).
    internal abstract string? ShownPhoto { get; }

    /// May the design go away (window closing, design switch)? Offers to
    /// save unsaved edits.
    internal virtual Task<bool> CanCloseAsync() => Task.FromResult(true);

    /// Walks every screen of the design for the CI UI test.
    internal abstract Task SelfTestAsync(UiTestLog log, string photo);

    internal bool SettingsOpen => _overlay.Visibility == Visibility.Visible;

    internal void ShowSettings()
    {
        _overlay.Children.Clear();
        _overlay.Children.Add(new SettingsView(CloseSettings));
        _overlay.Visibility = Visibility.Visible;
    }

    internal void CloseSettings()
    {
        _overlay.Visibility = Visibility.Collapsed;
        _overlay.Children.Clear();
        OnSettingsClosed();
    }

    /// Folders may have changed in settings.
    protected virtual void OnSettingsClosed()
    {
    }
}

internal sealed record DesignInfo(string Id, string Title, string Summary, Func<DesignShell> Create);

internal static class Designs
{
    /// One design now: a sidebar of words with tags and pages, and a viewer
    /// with information and editing on the right. The shell stays so a design can be swapped whole.
    internal static readonly IReadOnlyList<DesignInfo> All = new DesignInfo[]
    {
        new("main", "SPhoto", "左侧标签与页面，沉浸看图，信息和修图从右侧滑入。", () => new Main.MainDesign()),
    };

    internal static DesignInfo Get(string id) => All.FirstOrDefault(d => d.Id == id) ?? All[0];
}

/// <summary>Collects what the UI test saw.</summary>
public sealed class UiTestLog
{
    private readonly StringBuilder _text = new();
    private readonly object _lock = new();

    /// Each line is also appended here at once, so a crash still leaves
    /// a record of how far the test got.
    internal string? FilePath { get; set; }

    internal int Failures { get; private set; }

    internal void Pass(string what) => Write($"ok    {what}");

    internal void Note(string what) => Write($"note  {what}");

    internal void Fail(string what)
    {
        Failures++;
        Write($"FAIL  {what}");
    }

    internal void Check(bool ok, string what)
    {
        if (ok) Pass(what);
        else Fail(what);
    }

    internal void Write(string line)
    {
        lock (_lock)
        {
            _text.AppendLine(line);
            if (FilePath is null) return;
            try
            {
                System.IO.File.AppendAllText(FilePath, line + "\n");
            }
            catch (Exception)
            {
            }
        }
    }

    /// Waits on the UI thread (yielding to it) until done() or the timeout.
    internal static async Task<bool> WaitAsync(Func<bool> done, int timeoutMs = 15000)
    {
        var start = Environment.TickCount64;
        while (!done())
        {
            if (Environment.TickCount64 - start > timeoutMs) return false;
            await Task.Delay(50);
        }
        return true;
    }

    public override string ToString()
    {
        lock (_lock) return _text.ToString();
    }
}
