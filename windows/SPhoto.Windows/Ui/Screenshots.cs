using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace SPhoto.Ui;

/// <summary>
/// Pictures of the window for the UI test, so a change to the layout can
/// be looked at from CI. XAML draws the tree into a bitmap
/// (RenderTargetBitmap), which is saved as PNG next to the test log.
/// </summary>
internal static class Screenshots
{
    internal static async Task SaveAsync(UiTestLog log, UIElement? root, string name)
    {
        if (root is null || log.FilePath is not { } logPath) return;
        try
        {
            // Let pending layout and fades settle first.
            await Task.Delay(400);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root);
            var pixels = (await bitmap.GetPixelsAsync()).ToArray();
            var dir = Path.Combine(Path.GetDirectoryName(logPath)!, "ui-shots");
            Directory.CreateDirectory(dir);
            using var stream = new FileStream(Path.Combine(dir, name + ".png"), FileMode.Create, FileAccess.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
            var dpi = root.XamlRoot?.RasterizationScale * 96 ?? 96;
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                                 (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, dpi, dpi, pixels);
            await encoder.FlushAsync();
            log.Note($"screenshot {name}.png ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
            LogLayout(log, root, name);
        }
        catch (Exception e)
        {
            log.Note($"screenshot {name} failed: {e.Message}");
        }
    }

    /// <summary>
    /// The window-relative box of each visible text, picture and button,
    /// in reading order, at most 80 of them.
    /// </summary>
    private static void LogLayout(UiTestLog log, UIElement root, string name)
    {
        var size = root is FrameworkElement r ? new Windows.Foundation.Size(r.ActualWidth, r.ActualHeight) : default;
        var boxes = new List<(double X, double Y, double W, double H, string What)>();
        void Walk(DependencyObject node)
        {
            if (node is UIElement { Visibility: Visibility.Collapsed } or UIElement { Opacity: 0 }) return;
            if (node is FrameworkElement e && e.ActualWidth > 0 && e.ActualHeight > 0 &&
                (e is TextBlock || e is Image || e is ButtonBase || e is Microsoft.UI.Xaml.Shapes.Path))
            {
                var at = e.TransformToVisual(root).TransformPoint(default);
                if (at.X < size.Width && at.Y < size.Height && at.X + e.ActualWidth > 0 && at.Y + e.ActualHeight > 0)
                {
                    string what = e switch
                    {
                        TextBlock t => $"text '{Short(t.Text)}' {t.FontSize:0}",
                        ButtonBase b => $"button '{Short(b.Content as string ?? AutomationName(b))}'",
                        Image => "image",
                        _ => "shape",
                    };
                    boxes.Add((at.X, at.Y, e.ActualWidth, e.ActualHeight, what));
                }
            }
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(node, i));
        }
        Walk(root);
        boxes.Sort((a, b) => Math.Abs(a.Y - b.Y) > 2 ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        foreach (var b in boxes.GetRange(0, Math.Min(80, boxes.Count)))
            log.Note($"layout {name}: x {b.X:0.#} y {b.Y:0.#} w {b.W:0.#} h {b.H:0.#} {b.What}");
    }

    private static string Short(string? s) => s is null ? "" : s.Length > 24 ? s[..24] + "…" : s.Replace('\n', ' ');

    private static string AutomationName(UIElement e) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(e) is { Length: > 0 } n ? n :
        ToolTipService.GetToolTip(e) as string ?? "";
}
