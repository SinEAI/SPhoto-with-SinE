using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SPhoto.Services;
using SPhoto.Views;
using Windows.System;

namespace SPhoto.Ui;

/// <summary>What a tile shows over its photo: a stack of similar shots
/// (offset cards behind and a count) or edited versions (a corner pill).
/// The two look different on purpose: a stack is several photos, versions
/// are one photo edited.</summary>
internal readonly record struct TileBadge(int Stack, int Versions);

/// <summary>
/// A virtualised grid of photo thumbnails, optionally grouped, built in
/// code: thumbnails load while their tiles are on screen and are dropped
/// when scrolled away. Double click or Enter opens a photo.
/// </summary>
internal sealed class PhotoGrid
{
    private readonly CollectionViewSource _source = new();
    private readonly Dictionary<string, PhotoItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private List<PhotoEntry> _entries = new();

    /// <param name="strip">One scrolling row (a filmstrip) instead of a grid.</param>
    /// <param name="openOnClick">A single click opens, as in the Windows 10
    /// Photos app; otherwise a click selects and a double click opens.</param>
    internal PhotoGrid(double tileWidth, double tileHeight, bool rounded = false,
                       ListViewSelectionMode selection = ListViewSelectionMode.Extended,
                       bool strip = false, bool openOnClick = false)
    {
        _openOnClick = openOnClick;
        View = strip ? new ListView() : new GridView();
        View.SelectionMode = openOnClick ? ListViewSelectionMode.None : selection;
        View.IsItemClickEnabled = openOnClick;
        // Children: two cards behind (stacks), the photo, the stack count,
        // the versions pill. Built once; containers are recycled.
        string corner = rounded ? "6" : "0";
        View.ItemTemplate = (DataTemplate)XamlReader.Load(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
            $"<Grid Width=\"{tileWidth}\" Height=\"{tileHeight}\">" +
            $"<Border Background=\"#FFE4E4E4\" Margin=\"10,10,0,0\" CornerRadius=\"{corner}\" Visibility=\"Collapsed\" />" +
            $"<Border Background=\"#FFCFCFCF\" BorderBrush=\"#FFFFFFFF\" BorderThickness=\"1\" Margin=\"5,5,5,5\" CornerRadius=\"{corner}\" Visibility=\"Collapsed\" />" +
            $"<Grid Background=\"#FFEEEEEE\" CornerRadius=\"{corner}\"><Image Stretch=\"UniformToFill\" /></Grid>" +
            "<Border HorizontalAlignment=\"Right\" VerticalAlignment=\"Bottom\" Margin=\"0,0,16,16\" Padding=\"8,2,8,3\" " +
            "CornerRadius=\"11\" Background=\"#B3000000\" Visibility=\"Collapsed\">" +
            "<TextBlock Foreground=\"White\" FontSize=\"12\" FontWeight=\"SemiBold\" /></Border>" +
            "<Border HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\" Margin=\"6\" Padding=\"7,2,8,3\" " +
            "CornerRadius=\"11\" Background=\"#EBFFFFFF\" Visibility=\"Collapsed\">" +
            "<StackPanel Orientation=\"Horizontal\" Spacing=\"4\">" +
            "<FontIcon Glyph=\"&#xE70F;\" FontSize=\"11\" VerticalAlignment=\"Center\" />" +
            "<TextBlock FontSize=\"12\" /></StackPanel></Border>" +
            "</Grid></DataTemplate>");
        if (strip)
        {
            View.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load(
                "<ItemsPanelTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
                "<ItemsStackPanel Orientation=\"Horizontal\" /></ItemsPanelTemplate>");
            ScrollViewer.SetHorizontalScrollMode(View, ScrollMode.Enabled);
            ScrollViewer.SetHorizontalScrollBarVisibility(View, ScrollBarVisibility.Auto);
            ScrollViewer.SetVerticalScrollMode(View, ScrollMode.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(View, ScrollBarVisibility.Disabled);
        }
        View.ContainerContentChanging += OnContainerContentChanging;
        View.Loaded += (_, _) =>
        {
            if (FindScroller(View) is { } scroller && !ReferenceEquals(scroller, _scroller))
            {
                if (_scroller is not null) _scroller.ViewChanged -= OnViewChanged;
                _scroller = scroller;
                scroller.ViewChanged += OnViewChanged;
            }
            // SetPhotos can run before XAML has created the first tiles.
            // Every load needs a pass after layout, even with the same scroller.
            HealSoon();
        };
        View.ChoosingGroupHeaderContainer += OnChoosingGroupHeader;
        View.ItemClick += (_, e) =>
        {
            if (e.ClickedItem is PhotoItem item) Invoked?.Invoke(item.Entry);
        };
        View.DoubleTapped += (_, e) =>
        {
            if (!View.IsItemClickEnabled && ItemOf(e.OriginalSource) is { } item) Invoked?.Invoke(item.Entry);
        };
        View.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter && !View.IsItemClickEnabled && View.SelectedItem is PhotoItem item)
            {
                e.Handled = true;
                Invoked?.Invoke(item.Entry);
            }
        };
    }

    private readonly bool _openOnClick;
    private Func<PhotoEntry, TileBadge>? _badge;

    /// Picking several photos (a "Select" command): clicks select instead
    /// of opening.
    internal bool Selecting
    {
        get => View.SelectionMode == ListViewSelectionMode.Multiple && _openOnClick;
        set
        {
            if (!_openOnClick) return;
            View.IsItemClickEnabled = !value;
            View.SelectionMode = value ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;
        }
    }

    /// Adds a photo to the selection.
    internal void SelectPath(string path)
    {
        if (_items.TryGetValue(path, out var item) && !View.SelectedItems.Contains(item)) View.SelectedItems.Add(item);
    }

    /// Scrolls a photo into view without selecting it.
    internal void ScrollTo(string path)
    {
        if (_items.TryGetValue(path, out var item)) View.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
    }

    internal ListViewBase View { get; }

    /// A photo was opened (double click or Enter).
    internal event Action<PhotoEntry>? Invoked;

    internal IReadOnlyList<PhotoEntry> Entries => _entries;

    internal List<PhotoEntry> Selected => View.SelectedItems.OfType<PhotoItem>().Select(i => i.Entry).ToList();

    internal static PhotoItem? ItemOf(object? source)
    {
        for (var e = source as DependencyObject; e is not null; e = VisualTreeHelper.GetParent(e))
        {
            if (e is FrameworkElement { DataContext: PhotoItem item }) return item;
        }
        return null;
    }

    /// The same item for a photo kept in place; a fresh one otherwise, so
    /// an old tile being recycled can never cancel a new tile's request.
    private PhotoItem ItemFor(PhotoEntry p, bool inPlace) =>
        inPlace && _items.TryGetValue(p.Path, out var old) && old.Entry == p ? old : new PhotoItem(p);

    private bool _grouped;
    private ObservableCollection<PhotoItem>? _list;
    private ObservableCollection<PhotoGroup>? _groups;

    /// <summary>
    /// Shows the photos, grouped by groupOf (null: one flat grid).
    /// Unchanged lists are left alone so scrolling and selection survive;
    /// a list that changed a little (the library still being indexed,
    /// stacks forming) is updated in place, so the tiles on screen keep
    /// their thumbnails and their pending loads. badge: what each tile
    /// shows over its photo (stacks, versions).
    /// </summary>
    internal void SetPhotos(List<PhotoEntry> photos, Func<PhotoEntry, string>? groupOf = null, bool force = false,
                            Func<PhotoEntry, TileBadge>? badge = null)
    {
        _badge = badge;
        if (!force && photos.SequenceEqual(_entries))
        {
            RefreshBadges();
            HealSoon();
            return;
        }
        bool grouped = groupOf is not null;
        int kept = photos.Count(p => _items.ContainsKey(p.Path));
        // In place only for a flat list: a grouped view (by month) did not
        // lay out tiles added to its groups afterwards (seen in CI).
        bool inPlace = !force && !grouped && !_grouped && _list is not null
                       && photos.Count > 0 && kept * 2 >= photos.Count;
        _entries = photos;
        var keep = View.SelectedItem as PhotoItem;
        var items = new Dictionary<string, PhotoItem>(StringComparer.OrdinalIgnoreCase);
        PhotoItem Take(PhotoEntry p) => items[p.Path] = ItemFor(p, inPlace);
        if (!grouped)
        {
            var wanted = photos.Select(Take).ToList();
            if (inPlace) Sync(_list!, wanted);
            else
            {
                _list = new ObservableCollection<PhotoItem>(wanted);
                _groups = null;
                _source.IsSourceGrouped = false;
                _source.Source = _list;
            }
        }
        else
        {
            _groups = new ObservableCollection<PhotoGroup>(photos.GroupBy(groupOf!).Select(g => new PhotoGroup(g.Key, g.Select(Take))));
            _list = null;
            _source.IsSourceGrouped = true;
            _source.Source = _groups;
        }
        _grouped = grouped;
        _items.Clear();
        foreach (var pair in items) _items[pair.Key] = pair.Value;
        if (!inPlace) View.ItemsSource = _source.View;
        Perf.Line("grid", $"{(inPlace ? "updated in place" : "list replaced")}: {photos.Count} photos, {kept} kept");
        if (keep is not null && _items.TryGetValue(keep.Entry.Path, out var again) && View.SelectedItem != again) View.SelectedItem = again;
        if (inPlace) RefreshBadges();
        HealSoon();
    }

    /// Makes a list hold the wanted items in order with as few changes as it takes.
    private static void Sync<T>(ObservableCollection<T> list, IReadOnlyList<T> wanted) where T : class
    {
        var set = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!set.Contains(list[i])) list.RemoveAt(i);
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < list.Count && ReferenceEquals(list[i], wanted[i])) continue;
            int at = -1;
            for (int j = i + 1; j < list.Count; j++)
            {
                if (ReferenceEquals(list[j], wanted[i]))
                {
                    at = j;
                    break;
                }
            }
            if (at >= 0) list.Move(at, i);
            else list.Insert(i, wanted[i]);
        }
        while (list.Count > wanted.Count) list.RemoveAt(list.Count - 1);
    }


    /// Selects a photo and scrolls it into view.
    internal void Reveal(string path)
    {
        if (!_items.TryGetValue(path, out var item)) return;
        // SelectedItems.Clear() throws E_UNEXPECTED in Single mode (seen in CI),
        // so only multi-select modes clear first; Single just swaps the selection.
        switch (View.SelectionMode)
        {
            case ListViewSelectionMode.None:
                break;
            case ListViewSelectionMode.Single:
                View.SelectedItem = item;
                break;
            default:
                try { View.SelectedItems.Clear(); }
                catch (System.Runtime.InteropServices.COMException) { }
                View.SelectedItem = item;
                break;
        }
        View.ScrollIntoView(item);
    }

    /// Scrolls to the first photo of a group title (for date jumps).
    internal void ScrollToGroup(string title)
    {
        if (_source.Source is ObservableCollection<PhotoGroup> groups &&
            groups.FirstOrDefault(g => g.Title == title) is { Count: > 0 } group)
        {
            View.ScrollIntoView(group[0], ScrollIntoViewAlignment.Leading);
        }
    }

    /// The image of a tile on screen (for the connected animation), or null.
    internal Image? ImageOf(string path)
    {
        if (!_items.TryGetValue(path, out var item)) return null;
        return View.ContainerFromItem(item) is SelectorItem { ContentTemplateRoot: Grid root } ? PhotoOf(root) : null;
    }

    internal bool IsOnScreen(string path) =>
        _items.TryGetValue(path, out var item)
        && View.ContainerFromItem(item) is SelectorItem { ActualWidth: > 0, ActualHeight: > 0 } container
        && ReferenceEquals(container.Content, item) && InViewport(container);

    private static Image? PhotoOf(Grid root) =>
        root.Children.Count > 2 && root.Children[2] is Grid g && g.Children[0] is Image i ? i : null;

    /// Tile badges after the library learned more (stacks, versions).
    internal void RefreshBadges()
    {
        foreach (var container in RealizedContainers())
        {
            if (container is { Content: PhotoItem item, ContentTemplateRoot: Grid root }) ApplyBadge(root, item.Entry);
        }
    }

    private void ApplyBadge(Grid root, PhotoEntry entry)
    {
        if (root.Children.Count < 5) return;
        var badge = _badge?.Invoke(entry) ?? default;
        bool stack = badge.Stack > 1;
        var shown = stack ? Visibility.Visible : Visibility.Collapsed;
        root.Children[0].Visibility = shown;
        root.Children[1].Visibility = shown;
        ((FrameworkElement)root.Children[2]).Margin = stack ? new Thickness(0, 0, 10, 10) : new Thickness(0);
        var count = (Border)root.Children[3];
        count.Visibility = shown;
        if (stack) ((TextBlock)count.Child).Text = badge.Stack.ToString();
        var versions = (Border)root.Children[4];
        versions.Visibility = badge.Versions > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (badge.Versions > 0) ((TextBlock)((StackPanel)versions.Child).Children[1]).Text = $"{badge.Versions} 个版本";
        if (stack) ToolTipService.SetToolTip(count, $"{badge.Stack} 张相似照片叠在一起");
    }

    /// Phase 0 shows the empty tile at once; the thumbnail is asked for in
    /// phase 1, which XAML skips for tiles that fly past during a fast
    /// scroll (incremental rendering, as in "Optimize ListView and GridView
    /// performance" on Microsoft Learn).
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not PhotoItem item) return;
        if (args.InRecycleQueue)
        {
            if (args.ItemContainer.ContentTemplateRoot is Grid recycled && PhotoOf(recycled) is { } oldImage) oldImage.Source = null;
            // A photo that moved in the list may already have a new tile.
            if (View.ContainerFromItem(item) is not { } other || ReferenceEquals(other, args.ItemContainer)) item.Unload();
            return;
        }
        if (args.ItemContainer.ContentTemplateRoot is not Grid root || PhotoOf(root) is not { } image)
        {
            // A fresh container may reach phase 0 before its template is
            // ready. Still request phase 1 so its first thumbnail is loaded.
            if (args.Phase == 0) args.RegisterUpdateCallback(OnContainerContentChanging);
            HealSoon();
            return;
        }
        if (args.Phase == 0)
        {
            // Recently shown thumbnails come back at once from memory.
            image.Source = item.Thumbnail ?? Thumbnails.Recent(item.Entry.Path);
            ToolTipService.SetToolTip(args.ItemContainer, item.Name);
            ApplyBadge(root, item.Entry);
            if (image.Source is null) args.RegisterUpdateCallback(OnContainerContentChanging);
            // Let XAML set Content to this item. Handled would suppress
            // that assignment, so LoadInto's recycled-container guard
            // would reject the first thumbnail even after it was decoded.
            return;
        }
        if (item.Thumbnail is not null)
        {
            image.Source = item.Thumbnail;
            return;
        }
        LoadInto(args.ItemContainer, item);
    }

    private void LoadInto(SelectorItem container, PhotoItem item)
    {
        item.Load(() =>
        {
            // Still showing this photo? Containers are reused.
            if (container.Content == item && container.ContentTemplateRoot is Grid g && PhotoOf(g) is { } i)
            {
                i.Source = item.Thumbnail;
            }
        });
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _healTimer;

    /// Tiles Heal had to fix (for the UI test).
    internal int Healed { get; private set; }

    /// Looks over the tiles shortly after the list changed or a scroll ended.
    private void HealSoon()
    {
        // One pass after a layout change; loading completes by notification.
        if (_healTimer is null)
        {
            _healTimer = View.DispatcherQueue.CreateTimer();
            _healTimer.Interval = TimeSpan.FromMilliseconds(200);
            _healTimer.IsRepeating = false;
            _healTimer.Tick += (_, _) => Heal();
        }
        if (!_healTimer.IsRunning) _healTimer.Start();
    }

    /// <summary>
    /// Every realised tile without a picture gets one: from its item, from
    /// memory, or a fresh request. XAML's order of recycling and reusing
    /// tiles while a list changes under it (a first run, stacks forming)
    /// could leave a tile whose request was dropped; this puts it right.
    /// </summary>
    private void Heal()
    {
        foreach (var container in RealizedContainers())
        {
            if (container is not { Content: PhotoItem item, ContentTemplateRoot: Grid root } || !IsCurrent(item)) continue;
            if (PhotoOf(root) is not { Source: null } image) continue;
            Healed++;
            if ((item.Thumbnail ?? Thumbnails.Recent(item.Entry.Path)) is { } source) image.Source = source;
            else LoadInto(container, item);
        }
    }

    private IEnumerable<SelectorItem> RealizedContainers() =>
        View.ItemsPanelRoot is Panel panel ? panel.Children.OfType<SelectorItem>() : Enumerable.Empty<SelectorItem>();

    private bool IsCurrent(PhotoItem item) =>
        _items.TryGetValue(item.Entry.Path, out var current) && ReferenceEquals(current, item);

    /// Tiles on screen still without their thumbnail (for the UI test).
    internal int BlankTilesOnScreen() => TilesOnScreen().Blank;

    /// Counts only; startup diagnostics must not refresh or repair the view.
    internal string StartupState()
    {
        int realized = 0, matching = 0, loaded = 0;
        foreach (var container in RealizedContainers())
        {
            realized++;
            if (container.Content is PhotoItem item && _items.TryGetValue(item.Entry.Path, out var current)
                && ReferenceEquals(current, item))
            {
                matching++;
                if (item.Thumbnail is not null) loaded++;
            }
        }
        return $"items {_items.Count}, realized {realized}, content matches {matching}, loaded items {loaded}, screen {TilesOnScreen()}, healed {Healed}";
    }

    /// Counts actual containers, so an unlaid-out grid is not mistaken for
    /// a complete first screen just because it has no blank tiles yet.
    internal (int Tiles, int Blank) TilesOnScreen()
    {
        int tiles = 0, blank = 0;
        foreach (var c in RealizedContainers())
        {
            if (c is not { Content: PhotoItem item, ContentTemplateRoot: Grid root, ActualWidth: > 0, ActualHeight: > 0 }
                || !IsCurrent(item) || !InViewport(c) || PhotoOf(root) is not { } image) continue;
            tiles++;
            if (image.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 }) blank++;
        }
        return (tiles, blank);
    }

    private ScrollViewer? _scroller;

    private void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!args.IsIntermediate) HealSoon();
    }

    /// Is a tile on screen now, rather than in the buffer XAML realises
    /// around it? Unlaid-out containers cannot satisfy a visibility check.
    private bool InViewport(FrameworkElement container)
    {
        if (View.Visibility != Visibility.Visible || container.Visibility != Visibility.Visible) return false;
        var scroller = _scroller ?? FindScroller(View);
        if (scroller is null || container.ActualHeight <= 0 || scroller.ViewportHeight <= 0 || scroller.ViewportWidth <= 0)
            return false;
        try
        {
            var r = container.TransformToVisual(scroller).TransformBounds(
                new Windows.Foundation.Rect(0, 0, container.ActualWidth, container.ActualHeight));
            return r.Bottom > 0 && r.Top < scroller.ViewportHeight && r.Right > 0 && r.Left < scroller.ViewportWidth;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer s) return s;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }

    private void OnChoosingGroupHeader(ListViewBase sender, ChoosingGroupHeaderContainerEventArgs args)
    {
        if (args.Group is not PhotoGroup group) return;
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        header.Children.Add(Kit.Title(group.Title, 18));
        var count = Kit.Text(group.CountText, 12, secondary: true);
        count.VerticalAlignment = VerticalAlignment.Bottom;
        count.Margin = new Thickness(0, 0, 0, 2);
        header.Children.Add(count);
        args.GroupHeaderContainer = new GridViewHeaderItem { Content = header };
    }
}
