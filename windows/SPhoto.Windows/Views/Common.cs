using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SPhoto.Services;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace SPhoto.Views;

/// <summary>What the viewer opens: a list of photos and the one to show.</summary>
internal sealed record ViewerArgs(List<PhotoEntry> Photos, int Index);

/// <summary>What the editor opens.</summary>
internal sealed record EditorArgs(string Path);

/// <summary>A photo in the gallery grid, with its thumbnail loaded while
/// it is on screen.</summary>
public sealed class PhotoItem : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private CancellationTokenSource? _loading;
    private Action? _loaded;
    private int _version;

    internal PhotoItem(PhotoEntry entry) => Entry = entry;

    internal PhotoEntry Entry { get; }

    public string Name => Entry.Name;

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Loads the thumbnail; loaded runs once it is set (at once when it
    /// was shown recently). Every view shares the same thumbnail.
    /// Repeated requests share the work and replace the completion callback
    /// with the current tile's callback. Call on the UI thread; it never blocks it.
    /// </summary>
    internal async void Load(Action? loaded = null)
    {
        if (_thumbnail is not null)
        {
            loaded?.Invoke();
            return;
        }
        if (loaded is not null) _loaded = loaded;
        if (_loading is not null) return;
        int version = ++_version;
        if (Thumbnails.Recent(Entry.Path) is { } recent)
        {
            var notify = _loaded;
            _loaded = null;
            Thumbnail = recent;
            if (version == _version) notify?.Invoke();
            return;
        }
        var cancel = _loading = new CancellationTokenSource();
        double asked = Services.Perf.Now;
        try
        {
            var source = await Thumbnails.LoadAsync(Entry.Path, cancel.Token);
            // Scrolled away meanwhile: not ours to show. A newer request's
            // callback and cancellation source must remain untouched.
            if (version != _version || !ReferenceEquals(_loading, cancel))
            {
                Services.Perf.Line("tile", $"dropped after {Services.Perf.Now - asked:F0} ms (recycled)  {Entry.Name}");
                return;
            }
            if (Services.Perf.Now - asked > 300) Services.Perf.Line("tile", $"shown after {Services.Perf.Now - asked:F0} ms");
            var notify = _loaded;
            _loaded = null;
            _loading = null;
            Thumbnail = source;
            // PropertyChanged or the callback may unload and load again.
            if (version == _version) notify?.Invoke();
        }
        catch (Exception)
        {
            // Cancellation or failure releases only this request's state.
            if (version == _version && ReferenceEquals(_loading, cancel))
            {
                _loading = null;
                _loaded = null;
            }
        }
        finally
        {
            cancel.Dispose();
        }
    }

    /// Releases this tile's image and wait; shared generation can finish.
    internal void Unload()
    {
        _version++;
        var cancel = _loading;
        _loading = null;
        _loaded = null;
        cancel?.Cancel();
        Thumbnail = null;
    }
}

/// <summary>The photos of one month.</summary>
public sealed class PhotoGroup : ObservableCollection<PhotoItem>
{
    internal PhotoGroup(string title, IEnumerable<PhotoItem> items) : base(items) => Title = title;

    public string Title { get; }

    public string CountText => $"{Count} 张";
}

/// <summary>
/// Thumbnails as XAML image sources. The JPEG comes from ThumbnailLoader
/// (disk cache, else a direct decode of the original); the most recent images
/// are kept so scrolling back shows
/// them at once. Everything else is left for the garbage collector.
/// </summary>
internal static class Thumbnails
{
    // 128 square 384 px images need at most 72 MiB of decoded pixels.
    private const int RecentCount = 128;

    private static readonly object RecentLock = new();
    private static readonly Dictionary<string, LinkedListNode<(string Key, ImageSource Source)>> RecentIndex = new();
    private static readonly LinkedList<(string Key, ImageSource Source)> RecentOrder = new();

    private static int _generation;
    private static string KeyOf(string path) => path.ToLowerInvariant();

    /// A thumbnail shown recently, or null; never touches the disk.
    internal static ImageSource? Recent(string path)
    {
        lock (RecentLock)
        {
            if (!RecentIndex.TryGetValue(KeyOf(path), out var node)) return null;
            RecentOrder.Remove(node);
            RecentOrder.AddFirst(node);
            return node.Value.Source;
        }
    }

    private static void Remember(string path, ImageSource source, int generation)
    {
        var key = KeyOf(path);
        lock (RecentLock)
        {
            if (_generation != generation) return;
            if (RecentIndex.TryGetValue(key, out var old)) RecentOrder.Remove(old);
            RecentIndex[key] = RecentOrder.AddFirst((key, source));
            while (RecentOrder.Count > RecentCount && RecentOrder.Last is { } last)
            {
                RecentOrder.RemoveLast();
                RecentIndex.Remove(last.Value.Key);
            }
        }
    }

    /// Forgets a photo's thumbnails (after it changed on disk).
    internal static void Forget(string path)
    {
        lock (RecentLock)
        {
            _generation++;
            if (RecentIndex.Remove(KeyOf(path), out var node)) RecentOrder.Remove(node);
        }
    }

    /// The thumbnail of a photo, or null when none can be made. Call on
    /// the UI thread.
    internal static async Task<ImageSource?> LoadAsync(string path)
    {
        try
        {
            return await LoadAsync(path, CancellationToken.None);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The thumbnail of a photo, or null when none can be made; cancelling
    /// stops this caller's wait without interrupting shared generation. Call
    /// on the UI thread.
    /// </summary>
    internal static async Task<ImageSource?> LoadAsync(string path, CancellationToken cancel)
    {
        int generation = Volatile.Read(ref _generation);
        if (Recent(path) is { } recent) return recent;
        var jpeg = await ThumbnailLoader.FetchAsync(path, cancel);
        if (jpeg is null) return null;
        var source = await ToImageAsync(jpeg);
        if (source is not null) Remember(path, source, generation);
        return source;
    }

    /// A thumbnail JPEG as an image decoded at no more than 384 px, so
    /// thousands of tiles cost thousands of small bitmaps, not photos.
    private static async Task<ImageSource?> ToImageAsync(byte[] jpeg)
    {
        try
        {
            var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Physical };
            if (ThumbnailCache.JpegSize(jpeg) is var (w, h))
            {
                int bucket = (int)ThumbnailCache.Side;
                if (w >= h) bitmap.DecodePixelWidth = Math.Min(w, bucket);
                else bitmap.DecodePixelHeight = Math.Min(h, bucket);
            }
            // Not disposed: it holds no handle, and XAML may read it again
            // to decode once more after dropping the pixels off screen.
            var stream = new MemoryStream(jpeg, writable: false);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            return bitmap;
        }
        catch (Exception e)
        {
            Perf.Line("thumb", $"XAML thumbnail decode failed (0x{e.HResult:X8})");
            return null;
        }
    }

    /// Premultiplied BGRA pixels as something an Image can show.
    internal static async Task<ImageSource> ToSourceAsync(Pixels bgra)
    {
        // Not disposed: XAML copies the bitmap to the screen on a worker
        // thread after SetBitmapAsync returns, and a closed bitmap there
        // ends the app (RO_E_CLOSED in SoftwareBitmap.LockBuffer; seen with
        // a 6000x4000 photo, whose copy takes long enough to lose the race).
        var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            bgra.Data.AsBuffer(), BitmapPixelFormat.Bgra8, bgra.Width, bgra.Height, BitmapAlphaMode.Premultiplied);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        return source;
    }
}

internal static class Dialogs
{
    /// Asks a yes/no question; true when the user picks primary.
    internal static async Task<bool> ConfirmAsync(XamlRoot root, string title, string text, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    internal static async Task ShowAsync(XamlRoot root, string title, string text)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "好",
        };
        await dialog.ShowAsync();
    }
}

internal static class Shell
{
    internal static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Explorer missing is not worth a dialog.
        }
    }

    internal static void OpenFolder(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception)
        {
        }
    }

    /// Moves photos to the Recycle Bin; returns the paths that could not be.
    internal static async Task<List<string>> RecycleAsync(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                await file.DeleteAsync(StorageDeleteOption.Default);
            }
            catch (Exception)
            {
                failed.Add(path);
            }
        }
        return failed;
    }

    internal static string FormatBytes(long bytes) =>
        bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):F1} GB" :
        bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):F1} MB" :
        $"{Math.Max(1, bytes / 1024)} KB";
}
