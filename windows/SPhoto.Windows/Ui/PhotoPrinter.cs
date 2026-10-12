using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using SPhoto.Services;
using SPhoto.Views;
using Windows.Graphics.Printing;

namespace SPhoto.Ui;

/// <summary>
/// Prints photos through Windows' own print dialog (printer, paper, copies
/// and preview are all there), one photo per page, fitted upright inside
/// the printable area. The way Microsoft's "Print from your app" describes
/// it for WinUI 3: PrintManagerInterop for the window, a PrintDocument
/// whose pages are XAML elements. The photos are decoded before the dialog
/// opens, so no page is ever printed before its picture has arrived.
/// </summary>
internal static class PhotoPrinter
{
    /// Big enough for a full page at photo-printer resolution, small enough
    /// that a dozen photos do not exhaust memory.
    private const int PrintSide = 3600;

    private static PrintManager? _manager;
    private static PrintDocument? _document;
    private static IPrintDocumentSource? _source;
    private static List<WriteableBitmap> _bitmaps = new();
    private static readonly List<UIElement> Pages = new();
    private static string _title = "";
    private static bool _busy;

    /// Opens the print dialog for these photos, or says why it cannot.
    internal static async Task PrintAsync(XamlRoot root, IReadOnlyList<PhotoEntry> photos)
    {
        if (photos.Count == 0 || _busy) return;
        if (!PrintManager.IsSupported())
        {
            await Dialogs.ShowAsync(root, "无法打印", "这台电脑不支持从应用打印。");
            return;
        }
        _busy = true;
        try
        {
            var bitmaps = new List<WriteableBitmap>();
            var failed = new List<string>();
            foreach (var p in photos)
            {
                try
                {
                    var pixels = await Task.Run(() => ImageIO.DecodeAsync(p.Path, PrintSide, bgra: true));
                    bitmaps.Add(ToBitmap(pixels));
                }
                catch (Exception)
                {
                    failed.Add(p.Name);
                }
            }
            if (bitmaps.Count == 0)
            {
                await Dialogs.ShowAsync(root, "无法打印", "这些照片没能打开：" + string.Join("、", failed.Take(5)));
                return;
            }
            _bitmaps = bitmaps;
            _title = photos.Count == 1 ? photos[0].Name : $"{bitmaps.Count} 张照片";
            Register();
            await PrintManagerInterop.ShowPrintUIForWindowAsync(App.Window.Hwnd);
        }
        catch (Exception e)
        {
            Crash.Log(e);
            await Dialogs.ShowAsync(root, "无法打印", "现在没法打开打印对话框，请稍后再试。");
        }
        finally
        {
            _busy = false;
        }
    }

    private static WriteableBitmap ToBitmap(Pixels bgra)
    {
        var bitmap = new WriteableBitmap(bgra.Width, bgra.Height);
        using (var stream = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bitmap.PixelBuffer))
        {
            stream.Write(bgra.Data, 0, bgra.Data.Length);
        }
        bitmap.Invalidate();
        return bitmap;
    }

    /// Once per window: the print manager asks for a task when the dialog opens.
    private static void Register()
    {
        if (_manager is not null) return;
        _document = new PrintDocument();
        _source = _document.DocumentSource;
        _document.Paginate += OnPaginate;
        _document.GetPreviewPage += (_, e) => _document.SetPreviewPage(e.PageNumber, Pages[e.PageNumber - 1]);
        _document.AddPages += (_, _) =>
        {
            foreach (var page in Pages) _document.AddPage(page);
            _document.AddPagesComplete();
        };
        _manager = PrintManagerInterop.GetForWindow(App.Window.Hwnd);
        _manager.PrintTaskRequested += (_, args) =>
        {
            var task = args.Request.CreatePrintTask(_title, a => a.SetSource(_source));
            // Photos print best as photos: the dialog starts on photo quality.
            task.Options.PrintQuality = PrintQuality.Photographic;
        };
    }

    /// One page per photo, the photo centred and fitted in the printable area.
    private static void OnPaginate(object sender, PaginateEventArgs e)
    {
        Pages.Clear();
        var page = ((PrintTaskOptions)e.PrintTaskOptions).GetPageDescription(0);
        var area = page.ImageableRect;
        foreach (var bitmap in _bitmaps)
        {
            var sheet = new Grid
            {
                Width = page.PageSize.Width,
                Height = page.PageSize.Height,
                Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            };
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.Uniform,
                Width = Math.Max(1, area.Width),
                Height = Math.Max(1, area.Height),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(area.X, area.Y, 0, 0),
            };
            sheet.Children.Add(image);
            Pages.Add(sheet);
        }
        _document!.SetPreviewPageCount(Pages.Count, PreviewPageCountType.Final);
    }
}
