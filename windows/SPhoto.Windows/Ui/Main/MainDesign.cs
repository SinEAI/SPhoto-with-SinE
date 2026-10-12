using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SPhoto.Editing;
using SPhoto.Library;
using SPhoto.Services;
using SPhoto.Views;
using Windows.System;

namespace SPhoto.Ui.Main;

/// <summary>
/// SPhoto's one design. A quiet sidebar of words, no icons: 所有照片, 标签
/// and the user's own pages (each a set of tags shown together), then the
/// archived pages; at the bottom, 新建页面 and the folders (folded, opening
/// upwards, needed now and then) above 设置. The sidebar folds away with the
/// button at its top. 所有照片, tags and pages know photos by their content
/// (identical copies are one photo, similar shots stack, edits fold under
/// their original); a folder shows files exactly as they are on disk. Any
/// photo opens in the same viewer beside the sidebar, where information and
/// editing both come in from the right.
/// </summary>
internal sealed class MainDesign : DesignShell
{
    private enum Page { All, Tags, Tag, UserPage, Folder }

    private readonly NavigationView _nav = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsSettingsVisible = false,
        IsPaneToggleButtonVisible = false,
        OpenPaneLength = 240,
    };
    private readonly NavigationViewItem _allItem, _tagsItem, _archivedItem, _newPageItem, _foldersItem, _settingsItem;
    /// The folders open upwards, above 文件夹, so the bottom of the sidebar
    /// stays put; NavigationView's own groups only open downwards, and a
    /// group among its footer items stuck and pushed the items above it.
    private readonly List<NavigationViewItem> _folderItems = new();
    private readonly FontIcon _foldersChevron = Kit.Icon(Kit.G.ChevronUp, 10);
    private bool _foldersOpen;
    /// Folds the sidebar; it sits at the sidebar's top, or over the
    /// content's top left corner while the sidebar is away.
    private readonly Button _paneButton = Kit.IconButton(Kit.G.Menu, "收起侧栏", () => { });
    private readonly Grid _paneButtonHost = new() { Height = 44, Padding = new Thickness(4, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    /// The sidebar's top row, beside the fold button; the items follow at once.
    private readonly Grid _paneHeader = new() { Height = 44 };
    /// No title bar: the sidebar and the content run to the window's top,
    /// and an invisible strip there drags the window; its buttons sit over
    /// the content's top right corner. Nothing clickable goes under it.
    private const double TitleBarHeight = 32;
    private readonly Border _titleBar = new()
    {
        Height = TitleBarHeight,
        // Left of it, the fold button keeps the window's top left corner.
        Margin = new Thickness(48, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Top,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };
    private readonly Grid _header, _pageArea;
    /// The sidebar was folded for a photo opened from Explorer; it comes
    /// back with the library.
    private bool _paneFoldedForPhoto;
    private readonly HyperlinkButton _back = new() { Padding = new Thickness(0), Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
    private readonly TextBlock _title = Kit.Title("所有照片", 28), _summary = Kit.Text("", 13, secondary: true);
    private readonly ProgressRing _busy = new() { Width = 12, Height = 12, IsActive = false, Visibility = Visibility.Collapsed };
    private readonly StackPanel _modes = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly ToggleButton _byTag, _allOfPage, _select;
    private readonly Button _more, _addPhotos;
    private readonly MenuFlyout _moreMenu = new() { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
    private readonly PhotoGrid _grid = new(176, 176, openOnClick: true);
    private readonly ScrollViewer _cardsScroll = new() { Padding = new Thickness(Inset - 4, 0, Inset, 32) };
    private readonly VariableSizedWrapGrid _cards = new() { Orientation = Orientation.Horizontal, ItemWidth = 232, ItemHeight = 228 };
    private readonly ContentPresenter _empty = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _selectCount = Kit.Text("", 14, secondary: true);
    private readonly Button _addTag, _stack, _untag, _batchFilter, _print, _batchDelete, _pickDone;
    private readonly StackPanel _selectActions, _pickActions;
    /// Picking photos from 所有照片 to add to this tag; null when not picking.
    private string? _pickFor;
    private long? _pickFrom;
    private readonly MainViewer _viewer;
    private readonly MainEditor _editor;

    /// Left edge of every page's content: titles, tiles and cards line up on it.
    private const double Inset = 32;

    private Page _page = Page.All;
    private long _pageId;
    private string _tag = "", _folder = "";
    /// The page a tag was opened from, for the way back.
    private long? _tagFrom;
    /// Pages showing all their photos rather than one card per tag.
    private readonly HashSet<long> _pagesShowingAll = new();
    private List<PhotoEntry> _entries = new();
    private LibraryModel? _model;
    private CancellationTokenSource? _scan;
    private bool _buildingNav;

    internal MainDesign()
    {
        // ----- Sidebar: words only, the way Windows 10 Settings lists its pages.
        _allItem = NavItem("所有照片", "all");
        _tagsItem = NavItem("标签", "tags");
        _archivedItem = NavItem("已归档", "archived");
        _archivedItem.SelectsOnInvoked = false;
        _archivedItem.Foreground = Kit.Secondary;
        // At the bottom, quiet: a new page, the folders (folded), settings.
        _newPageItem = NavItem("新建页面", "new-page");
        _newPageItem.SelectsOnInvoked = false;
        _newPageItem.Foreground = Kit.Secondary;
        ToolTipService.SetToolTip(_newPageItem, "把几个标签放进一个页面，一起浏览");
        _foldersItem = NavItem("文件夹", "folders");
        _foldersItem.SelectsOnInvoked = false;
        _foldersItem.Foreground = Kit.Secondary;
        var foldersLabel = Kit.Columns(Kit.Star(), Kit.Auto);
        foldersLabel.Children.Add(new TextBlock { Text = "文件夹" });
        _foldersChevron.VerticalAlignment = VerticalAlignment.Center;
        _foldersChevron.Margin = new Thickness(0, 0, 8, 0);
        foldersLabel.Children.Add(Kit.At(_foldersChevron, column: 1));
        _foldersItem.Content = foldersLabel;
        _foldersItem.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _settingsItem = NavItem("设置", "settings");
        _settingsItem.SelectsOnInvoked = false;
        _settingsItem.Foreground = Kit.Secondary;
        _nav.FooterMenuItems.Add(_newPageItem);
        _nav.FooterMenuItems.Add(_foldersItem);
        _nav.FooterMenuItems.Add(_settingsItem);

        // The fold button: one place for it, whether the sidebar is in or out.
        _paneButton.Click += (_, _) => SetPaneShown(!_nav.IsPaneVisible);
        _paneButtonHost.Children.Add(_paneButton);
        _nav.PaneHeader = _paneHeader;
        _nav.SelectionChanged += OnNavSelectionChanged;
        _nav.ItemInvoked += OnNavItemInvoked;
        // 标签 and 已归档 open with the same motion as 文件夹.
        _nav.Expanding += (_, e) =>
        {
            if (e.ExpandingItemContainer is not NavigationViewItem group) return;
            var rows = group.MenuItems.OfType<UIElement>().ToList();
            foreach (var r in rows) r.Opacity = 0;
            DispatcherQueue.TryEnqueue(() => Motion.Reveal(rows, -8));
        };

        // ----- Page: header, then the grid or cards.
        var page = _pageArea = Kit.Rows(Kit.Auto, Kit.Star());
        var header = _header = Kit.Columns(Kit.Star(), Kit.Auto);
        header.Padding = new Thickness(Inset, TitleBarHeight + 16, 24, 16);
        var titles = new StackPanel();
        _back.Click += (_, _) => { if (_tagFrom is { } from) Go("page:" + from); };
        titles.Children.Add(_back);
        _title.TextTrimming = TextTrimming.CharacterEllipsis;
        _title.TextWrapping = TextWrapping.NoWrap;
        titles.Children.Add(_title);
        // The summary starts under the title's first letter; the busy ring follows it.
        var summaryRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(1, 4, 0, 0) };
        summaryRow.Children.Add(_summary);
        _busy.VerticalAlignment = VerticalAlignment.Center;
        summaryRow.Children.Add(_busy);
        titles.Children.Add(summaryRow);
        header.Children.Add(titles);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        _byTag = Pill("按标签");
        _allOfPage = Pill("全部照片");
        _byTag.Click += (_, _) => SetPageShowsAll(false);
        _allOfPage.Click += (_, _) => SetPageShowsAll(true);
        _modes.Children.Add(_byTag);
        _modes.Children.Add(_allOfPage);
        actions.Children.Add(_modes);
        // Inside a tag: pick photos from the whole library to give it.
        _addPhotos = Kit.Flat(Kit.TextButton("添加照片", StartPicking));
        ToolTipService.SetToolTip(_addPhotos, "从所有照片里选几张，加上这个标签");
        actions.Children.Add(_addPhotos);
        _select = Kit.Flat(new ToggleButton { Content = "选择", Padding = new Thickness(12, 6, 12, 7) });
        ToolTipService.SetToolTip(_select, "选择多张照片");
        _select.Click += (_, _) => SetSelecting(_select.IsChecked == true);
        actions.Children.Add(_select);
        _more = Kit.IconButton(Kit.G.More, "更多", () => { });
        _more.Flyout = _moreMenu;
        _moreMenu.Opening += (_, _) => FillMoreMenu();
        actions.Children.Add(_more);
        // Selecting: the same row turns into what applies to the selection.
        _selectCount.VerticalAlignment = VerticalAlignment.Center;
        _selectCount.Margin = new Thickness(0, 0, 8, 0);
        _selectCount.Visibility = Visibility.Collapsed;
        actions.Children.Add(_selectCount);
        var refresh = new KeyboardAccelerator { Key = VirtualKey.F5 };
        refresh.Invoked += async (_, e) =>
        {
            e.Handled = true;
            await RefreshAsync();
        };
        KeyboardAccelerators.Add(refresh);
        header.Children.Add(Kit.At(actions, column: 1));
        page.Children.Add(header);

        // GridView items carry a 4 px margin; the padding makes up the rest of the inset.
        // Tiles keep a 4px gap on their right and bottom only, so the first
        // column lines up with the title at Inset.
        _grid.View.Padding = new Thickness(Inset, 0, Inset - 4, 24);
        _grid.Invoked += p => OpenViewer(p);
        _grid.View.SelectionChanged += (_, _) => UpdateSelection();
        page.Children.Add(Kit.At(_grid.View, row: 1));
        _cardsScroll.Content = _cards;
        _cardsScroll.Visibility = Visibility.Collapsed;
        page.Children.Add(Kit.At(_cardsScroll, row: 1));
        page.Children.Add(Kit.At(_empty, row: 1));

        // Selection: words, no icons; what applies to this page only.
        var selectActions = _selectActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
        _addTag = Kit.Flat(Kit.TextButton("添加标签", () => { }));
        var tagMenu = new MenuFlyout();
        tagMenu.Opening += (_, _) => FillTagMenu(tagMenu, SelectedKeys);
        _addTag.Flyout = tagMenu;
        _stack = Kit.Flat(Kit.TextButton("叠放", () => StackSelection()));
        ToolTipService.SetToolTip(_stack, "把选中的照片叠成一组，只露出一张封面");
        _untag = Kit.Flat(Kit.TextButton("移除标签", RemoveTagFromSelection));
        ToolTipService.SetToolTip(_untag, "只是去掉这个标签，照片本身不受影响");
        _batchFilter = Kit.Flat(Kit.TextButton("套用滤镜", () => { }));
        var filterMenu = new MenuFlyout();
        foreach (var f in Filter.All.Where(f => f.Id.Length > 0))
        {
            var item = new MenuFlyoutItem { Text = f.Title };
            var id = f.Id;
            item.Click += async (_, _) => await ApplyFilterToSelectionAsync(id);
            filterMenu.Items.Add(item);
        }
        _batchFilter.Flyout = filterMenu;
        ToolTipService.SetToolTip(_batchFilter, "给选中的照片各存一个加了滤镜的版本");
        _print = Kit.Flat(Kit.TextButton("打印", PrintSelection));
        ToolTipService.SetToolTip(_print, "每张照片打印一页（Ctrl+P）");
        _batchDelete = Kit.Flat(Kit.TextButton("删除", async () => await DeleteSelectionAsync()));
        foreach (var b in new[] { _addTag, _stack, _untag, _batchFilter, _print, _batchDelete }) selectActions.Children.Add(b);
        selectActions.Children.Add(Kit.TextButton("完成", () => SetSelecting(false)));
        actions.Children.Add(selectActions);
        // While picking for a tag the row only adds or cancels.
        _pickActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Visibility = Visibility.Collapsed };
        _pickActions.Children.Add(Kit.TextButton("取消", CancelPicking));
        _pickDone = Kit.TextButton("添加", async () => await FinishPickingAsync(), accent: true);
        _pickActions.Children.Add(_pickDone);
        actions.Children.Add(_pickActions);
        // ----- Viewer over the page, beside the sidebar; the editor lives inside it.
        _editor = new MainEditor(this) { Visibility = Visibility.Collapsed };
        _viewer = new MainViewer(this, _editor) { Visibility = Visibility.Collapsed };
        var content = new Grid();
        content.Children.Add(page);
        content.Children.Add(_viewer);
        _nav.Content = content;
        ClearForMaterial();
        // The viewer's bar stays under the window's buttons.
        _viewer.Margin = new Thickness(0, TitleBarHeight, 0, 0);
        var body = new Grid();
        body.Children.Add(_nav);
        body.Children.Add(_paneButtonHost);
        body.Children.Add(_titleBar);
        SetBody(body);

        var esc = new KeyboardAccelerator { Key = VirtualKey.Escape };
        esc.Invoked += (_, e) =>
        {
            if (_viewer.Visibility == Visibility.Visible || SettingsOpen) return;
            // One level back: out of picking or selecting, then from a tag to the page it was opened from.
            if (_pickFor is not null)
            {
                e.Handled = true;
                CancelPicking();
            }
            else if (_grid.Selecting)
            {
                e.Handled = true;
                SetSelecting(false);
            }
            else if (_tagFrom is { } from)
            {
                e.Handled = true;
                Go("page:" + from);
            }
        };
        KeyboardAccelerators.Add(esc);
        var print = new KeyboardAccelerator { Key = VirtualKey.P, Modifiers = VirtualKeyModifiers.Control };
        print.Invoked += (_, e) =>
        {
            if (_viewer.Visibility == Visibility.Visible || SettingsOpen || !_grid.Selecting || _grid.Selected.Count == 0) return;
            e.Handled = true;
            PrintSelection();
        };
        KeyboardAccelerators.Add(print);

        App.Library.Changed += OnFilesChanged;
        App.Library.CatalogChanged += OnCatalogChanged;
        Unloaded += (_, _) =>
        {
            _scan?.Cancel();
            App.Library.Changed -= OnFilesChanged;
            App.Library.CatalogChanged -= OnCatalogChanged;
        };
        BuildNav();
        _nav.SelectedItem = _allItem;
        _ = RefreshAsync();
    }

    private static NavigationViewItem NavItem(string text, string tag) => new() { Content = text, Tag = tag };

    internal override UIElement? TitleBar => _titleBar;

    /// <summary>
    /// The sidebar is left clear so the window's frosted material shows
    /// there; the content is white. NavigationView takes these brushes
    /// from its resources when its template loads, so they are set before that.
    /// </summary>
    private void ClearForMaterial()
    {
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Brush pane = clear;
        Brush content = Kit.White;
        _nav.Resources["NavigationViewDefaultPaneBackground"] = pane;
        _nav.Resources["NavigationViewExpandedPaneBackground"] = pane;
        _nav.Resources["NavigationViewContentBackground"] = content;
        _nav.Resources["NavigationViewContentGridBorderBrush"] = clear;
        // No edge or rounded corner between sidebar and content: they meet
        // straight, up to the window's top.
        _nav.Resources["NavigationViewContentGridBorderThickness"] = new Thickness(0);
        _nav.Resources["NavigationViewContentGridCornerRadius"] = new CornerRadius(0);
        _nav.Resources["NavigationViewMinimalContentGridBorderThickness"] = new Thickness(0);
        _nav.Resources["NavigationViewMinimalContentGridCornerRadius"] = new CornerRadius(0);
        _paneHeader.Background = pane;
        _pageArea.Background = content;
    }

    internal bool PaneShown => _nav.IsPaneVisible;

    /// <summary>
    /// Shows or folds the sidebar. Folded, the content takes the whole
    /// window and leaves room at its top left for the button to bring the
    /// sidebar back; a photo on screen glides to its new size.
    /// </summary>
    internal void SetPaneShown(bool shown)
    {
        _paneFoldedForPhoto = false;
        if (_nav.IsPaneVisible == shown) return;
        if (_viewer.Visibility == Visibility.Visible) _viewer.Photo.GlideOnNextResize();
        _nav.IsPaneVisible = shown;
        ToolTipService.SetToolTip(_paneButton, shown ? "收起侧栏" : "展开侧栏");
        _header.Padding = new Thickness(Inset, TitleBarHeight + 16, 24, 16);
        _viewer.LeadingSpace = shown ? 0 : 40;
    }

    /// One half of a two-way switch, as the viewer's version pills.
    private static ToggleButton Pill(string text) =>
        Kit.Flat(new ToggleButton { Content = text, Padding = new Thickness(12, 5, 12, 6), CornerRadius = new CornerRadius(14) });

    internal override string Id => "main";

    internal LibraryModel? Model => _model;
    internal bool EditorOpen => _editor.Visibility == Visibility.Visible;
    internal MainViewer Viewer => _viewer;
    internal MainEditor Editor => _editor;
    internal PhotoGrid Grid => _grid;

    internal override string? ShownPhoto =>
        EditorOpen ? _editor.Session?.Path :
        _viewer.Visibility == Visibility.Visible ? _viewer.Photo.Current?.Path : null;

    internal override void OpenFile(string path, bool alone = false)
    {
        // A window made for one photo starts with the sidebar folded.
        if (alone && PaneShown)
        {
            SetPaneShown(false);
            _paneFoldedForPhoto = true;
        }
        _viewer.OpenFile(path);
    }

    internal override Task<bool> CanCloseAsync() => _editor.CanCloseAsync();

    protected override void OnSettingsClosed()
    {
        BuildNav();
        _ = RefreshAsync();
    }

    // ----- Library -----

    private void OnFilesChanged() => DispatcherQueue.TryEnqueue(() => _ = RefreshAsync());

    private void OnCatalogChanged() => DispatcherQueue.TryEnqueue(() => _ = ReloadModelAsync());

    internal async Task RefreshAsync()
    {
        _scan?.Cancel();
        var scan = _scan = new CancellationTokenSource();
        SetBusy(true);
        if (_entries.Count == 0) _summary.Text = "正在查找照片…";
        try
        {
            double t0 = Perf.Now;
            if (_model is null)
            {
                // Show the last indexed library before touching source folders.
                _entries = await App.Library.CachedPhotosAsync();
                if (scan.IsCancellationRequested) return;
                if (_entries.Count > 0) await ReloadModelAsync();
            }
            var photos = await App.Library.ScanAsync(scan.Token);
            if (scan.IsCancellationRequested) return;
            Perf.Line("library", $"scan {Perf.Now - t0:F0} ms, {photos.Count} files");
            _entries = photos;
            await ReloadModelAsync();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_scan == scan) SetBusy(false);
            UpdateHeader();
        }
    }

    private void SetBusy(bool on)
    {
        _busy.IsActive = on;
        _busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// Reads the index again (fingerprints, stacks, tags and pages changed).
    internal async Task ReloadModelAsync()
    {
        try
        {
            double t0 = Perf.Now;
            _model = await LibraryModel.LoadAsync(App.Library);
            Perf.Line("library", $"model reload {Perf.Now - t0:F0} ms");
        }
        catch (Exception e)
        {
            Crash.Log(e);
            return;
        }
        BuildNav();
        ShowPage(force: false);
        _viewer.ModelChanged();
    }

    // ----- Sidebar -----

    private string? _navSignature;

    private void BuildNav()
    {
        // While the library is indexed the model reloads every second or
        // so; the sidebar is rebuilt only when what it lists changed.
        var signature = string.Join("\u0001",
            (_model?.Tags ?? Array.Empty<LibraryModel.TagInfo>()).Take(30).Select(t => t.Name)
                .Concat((_model?.Pages ?? Array.Empty<PageInfo>()).Select(p => $"{p.Id}:{p.Name}:{p.Archived}"))
                .Concat(App.Library.Folders));
        if (signature == _navSignature) return;
        _navSignature = signature;
        _buildingNav = true;
        var selectedTag = (_nav.SelectedItem as NavigationViewItem)?.Tag as string;
        _nav.MenuItems.Clear();
        _nav.MenuItems.Add(_allItem);
        _tagsItem.MenuItems.Clear();
        foreach (var t in (_model?.Tags ?? Array.Empty<LibraryModel.TagInfo>()).Take(30))
        {
            _tagsItem.MenuItems.Add(NavItem(t.Name, "tag:" + t.Name));
        }
        _nav.MenuItems.Add(_tagsItem);
        var pages = _model?.Pages ?? Array.Empty<PageInfo>();
        foreach (var p in pages.Where(p => !p.Archived)) _nav.MenuItems.Add(NavItem(p.Name, "page:" + p.Id));
        _archivedItem.MenuItems.Clear();
        foreach (var p in pages.Where(p => p.Archived)) _archivedItem.MenuItems.Add(NavItem(p.Name, "page:" + p.Id));
        if (_archivedItem.MenuItems.Count > 0) _nav.MenuItems.Add(_archivedItem);

        foreach (var old in _folderItems) _nav.FooterMenuItems.Remove(old);
        _folderItems.Clear();
        foreach (var folder in App.Library.Folders)
        {
            var item = NavItem(LibraryActions.FolderName(folder), "folder:" + folder);
            ToolTipService.SetToolTip(item, folder);
            item.Margin = new Thickness(16, 0, 0, 0);
            _folderItems.Add(item);
        }
        var add = NavItem("添加文件夹…", "add-folder");
        add.SelectsOnInvoked = false;
        add.Foreground = Kit.Secondary;
        add.Margin = new Thickness(16, 0, 0, 0);
        _folderItems.Add(add);
        if (_foldersOpen) ShowFolders(true);
        // Keep the selection on the same page when it still exists.
        _nav.SelectedItem = Find(selectedTag) ?? _allItem;
        _buildingNav = false;
    }

    /// Opens or closes the folders above 文件夹.
    private void ShowFolders(bool open)
    {
        _foldersOpen = open;
        // Moving items must not read as the user picking another page.
        bool building = _buildingNav;
        _buildingNav = true;
        var selected = _nav.SelectedItem;
        foreach (var item in _folderItems) _nav.FooterMenuItems.Remove(item);
        if (open)
        {
            int at = _nav.FooterMenuItems.IndexOf(_foldersItem);
            for (int i = 0; i < _folderItems.Count; i++) _nav.FooterMenuItems.Insert(at + i, _folderItems[i]);
            // They open upwards, so the nearest comes first, from below.
            if (!building)
            {
                var rows = _folderItems.AsEnumerable().Reverse().Cast<UIElement>().ToList();
                foreach (var r in rows) r.Opacity = 0;
                DispatcherQueue.TryEnqueue(() => Motion.Reveal(rows, 8));
            }
        }
        if (selected is NavigationViewItem item0 && (!_folderItems.Contains(item0) || open)) _nav.SelectedItem = item0;
        _buildingNav = building;
        _foldersChevron.Glyph = open ? Kit.G.ChevronDown : Kit.G.ChevronUp;
    }

    internal bool FoldersOpen => _foldersOpen;
    internal void ToggleFoldersForTest() => ShowFolders(!_foldersOpen);
    /// Where the top item, 文件夹 and 设置 sit in the window (for the UI test).
    internal (double All, double Folders, double Settings) SidebarRowsForTest() =>
        (Top(_allItem), Top(_foldersItem), Top(_settingsItem));
    private static double Top(UIElement e) => e.TransformToVisual(null).TransformPoint(default).Y;
    internal double FirstFolderTopForTest => _folderItems.Count > 0 ? Top(_folderItems[0]) : double.NaN;

    private NavigationViewItem? Find(string? tag)
    {
        if (tag is null) return null;
        foreach (var o in _nav.MenuItems.Concat(_nav.FooterMenuItems))
        {
            if (o is not NavigationViewItem item) continue;
            if ((string?)item.Tag == tag) return item;
            foreach (var c in item.MenuItems)
            {
                if (c is NavigationViewItem child && (string?)child.Tag == tag) return child;
            }
        }
        return null;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_buildingNav) return;
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "all";
        Go(tag);
    }

    // Groups (标签, 已归档) open and close by themselves: NavigationView
    // toggles a group when it is clicked, and toggling it here as well
    // undid that, so a click seemed to do nothing.
    private async void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args) =>
        await SidebarInvokedAsync((args.InvokedItemContainer as NavigationViewItem)?.Tag as string);

    private async Task SidebarInvokedAsync(string? tag)
    {
        // The sidebar wins over a photo on screen: going to a page closes
        // the viewer, even when that page is the one already lit.
        if (_viewer.Visibility == Visibility.Visible && tag is not (null or "settings" or "new-page" or "folders" or "archived" or "add-folder"))
        {
            if (!await _editor.LeaveAsync()) return;
            _viewer.Close();
        }
        switch (tag)
        {
            case "settings":
                ShowSettings();
                break;
            case "folders":
                ShowFolders(!_foldersOpen);
                break;
            case "add-folder":
                await AddFolderAsync();
                break;
            case "new-page":
                await NewPageAsync();
                break;
        }
    }

    /// Shows a page by its sidebar tag ("all", "tags", "tag:旅行", "page:3", "folder:C:\…").
    /// from: the page a tag counts as opened from (for its way back).
    internal void Go(string tag, long? from = null)
    {
        // Going anywhere ends picking without returning to the tag.
        _pickFor = null;
        SetSelecting(false);
        from ??= _page == Page.UserPage && tag.StartsWith("tag:", StringComparison.Ordinal) ? _pageId : null;
        _tagFrom = null;
        if (tag == "tags") _page = Page.Tags;
        else if (tag.StartsWith("tag:", StringComparison.Ordinal))
        {
            _page = Page.Tag;
            _tag = tag[4..];
            _tagFrom = from;
        }
        else if (tag.StartsWith("page:", StringComparison.Ordinal) && long.TryParse(tag[5..], out var id))
        {
            _page = Page.UserPage;
            _pageId = id;
        }
        else if (tag.StartsWith("folder:", StringComparison.Ordinal))
        {
            _page = Page.Folder;
            _folder = tag[7..];
        }
        else _page = Page.All;
        // A tag opened from a page keeps the page lit in the sidebar.
        var item = Find(_tagFrom is { } p ? "page:" + p : tag);
        if (!ReferenceEquals(_nav.SelectedItem, item))
        {
            _buildingNav = true;
            _nav.SelectedItem = item;
            _buildingNav = false;
        }
        ShowPage(force: true);
    }

    private async Task AddFolderAsync()
    {
        if (await LibraryActions.AddFolderAsync(XamlRoot) is not { } folder) return;
        BuildNav();
        Go("folder:" + folder);
        await RefreshAsync();
    }

    private async Task RemoveFolderAsync()
    {
        if (!await Dialogs.ConfirmAsync(XamlRoot, $"不再显示“{LibraryActions.FolderName(_folder)}”？",
                "SPhoto 不再显示这个文件夹里的照片。文件本身不会被删除，之后可以再添加回来。", "移除")) return;
        App.Library.RemoveFolder(_folder);
        BuildNav();
        Go("all");
        await RefreshAsync();
    }

    // ----- Pages -----

    private bool InLibrary => _page is not Page.Folder;

    private PageInfo? CurrentUserPage => _page == Page.UserPage ? _model?.PageOf(_pageId) : null;

    /// A user page showing one card per tag (the default) rather than all its photos.
    private bool ShowsTagCards => _page == Page.Tags || (_page == Page.UserPage && !_pagesShowingAll.Contains(_pageId));

    private Scope CurrentScope => _page switch
    {
        Page.UserPage => Scope.OfPage(_pageId),
        Page.Tag => Scope.OfTag(_tag),
        _ => Scope.All,
    };

    /// The photos of the page as the grid and viewer list them.
    internal List<PhotoEntry> PagePhotos()
    {
        if (_page == Page.Folder) return FolderPhotos(_folder);
        if (_model is null) return _page == Page.All ? _entries : new List<PhotoEntry>();
        return _model.Shots(CurrentScope).Select(s => s.Cover).ToList();
    }

    /// A folder as Explorer shows it: by subfolder, then by name.
    private List<PhotoEntry> FolderPhotos(string folder) =>
        _entries.Where(p => PhotoLibrary.IsInside(p.Path, folder))
            .OrderBy(p => p.Folder, Comparer<string>.Create(PhotoLibrary.NaturalCompare))
            .ThenBy(p => p.Name, Comparer<string>.Create(PhotoLibrary.NaturalCompare))
            .ToList();

    private string SubfolderOf(PhotoEntry p)
    {
        var relative = Path.GetRelativePath(_folder, p.Folder);
        return relative == "." ? LibraryActions.FolderName(_folder) : relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private void SetPageShowsAll(bool all)
    {
        if (all) _pagesShowingAll.Add(_pageId);
        else _pagesShowingAll.Remove(_pageId);
        ShowPage(force: true);
    }

    private void ShowPage(bool force)
    {
        if (_page == Page.UserPage && CurrentUserPage is null && _model is not null)
        {
            // The page went away (deleted elsewhere).
            _page = Page.All;
        }
        bool cards = ShowsTagCards;
        _grid.View.Visibility = cards ? Visibility.Collapsed : Visibility.Visible;
        _cardsScroll.Visibility = cards ? Visibility.Visible : Visibility.Collapsed;
        if (cards) BuildTagCards(_page == Page.Tags ? null : CurrentUserPage?.Tags);
        else
        {
            var photos = PagePhotos();
            if (_page == Page.Folder) _grid.SetPhotos(photos, SubfolderOf, force);
            else _grid.SetPhotos(photos, LibraryActions.Month, force, _model is { } m ? m.Badge : null);
        }
        UpdateHeader();
        UpdateCommands();
    }

    private void UpdateHeader()
    {
        var userPage = CurrentUserPage;
        _title.Text = _pickFor is { } picking ? $"选择要加到“{picking}”的照片" : _page switch
        {
            Page.Tags => "标签",
            Page.Tag => _tag,
            Page.UserPage => userPage?.Name ?? "",
            Page.Folder => LibraryActions.FolderName(_folder),
            _ => "所有照片",
        };
        var from = _tagFrom is { } id ? _model?.PageOf(id) : null;
        _back.Content = from is null ? "" : "‹ " + from.Name;
        _back.Visibility = from is null ? Visibility.Collapsed : Visibility.Visible;
        int count = _grid.Entries.Count;
        _summary.Text = _page switch
        {
            Page.Tags => $"{_model?.Tags.Count ?? 0} 个标签",
            Page.UserPage when ShowsTagCards => $"{userPage?.Tags.Count ?? 0} 个标签",
            Page.Folder => $"{count:N0} 个文件 · {_folder}",
            _ when _busy.IsActive && count == 0 => "正在查找照片…",
            _ => count == 0 ? "" : $"{count:N0} 张照片",
        };
        ShowEmpty();
    }

    private void ShowEmpty()
    {
        UIElement? message = null;
        var userPage = CurrentUserPage;
        if (App.Library.Folders.Count == 0)
        {
            message = Kit.Message(Kit.G.Pictures, "SPhoto 里还没有照片",
                "添加一个存放照片的文件夹，SPhoto 会把里面的照片按拍摄时间整理好。原始文件不会被移动或修改。",
                Kit.TextButton("添加文件夹", async () => await AddFolderAsync(), accent: true));
        }
        else if (_page == Page.Tags && _model?.Tags.Count == 0)
        {
            message = Kit.Message(Kit.G.Tag, "还没有标签",
                "打开一张照片，在“信息”里给它加上标签；也可以选择几张照片，一起加标签。标签只存在 SPhoto 里，不会写进照片文件。");
        }
        else if (userPage is { Tags.Count: 0 })
        {
            message = Kit.Message(Kit.G.Tag, "这个页面还没有标签",
                "页面把几个标签放在一起浏览，比如“人物”里放每个人的名字。",
                Kit.TextButton("选择标签", async () => await EditPageAsync(), accent: true));
        }
        else if (!ShowsTagCards && _grid.Entries.Count == 0 && !_busy.IsActive)
        {
            message = _page is Page.Tag or Page.UserPage
                ? Kit.Message(Kit.G.Tag, "这里还没有照片", "给照片加上这个页面里的标签，就会出现在这里。")
                : Kit.Message(Kit.G.Pictures, "这里还没有照片", "支持 JPEG、PNG、HEIC、WebP、TIFF 等格式。放进文件夹的新照片会自动出现。");
        }
        _empty.Content = message;
        _empty.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateCommands()
    {
        bool grid = !ShowsTagCards;
        bool picking = _pickFor is not null;
        bool selecting = _grid.Selecting;
        _modes.Visibility = _page == Page.UserPage && !selecting ? Visibility.Visible : Visibility.Collapsed;
        _byTag.IsChecked = ShowsTagCards;
        _allOfPage.IsChecked = !ShowsTagCards;
        _stack.Visibility = InLibrary ? Visibility.Visible : Visibility.Collapsed;
        _untag.Visibility = _page == Page.Tag ? Visibility.Visible : Visibility.Collapsed;
        _addPhotos.Visibility = _page == Page.Tag && !selecting ? Visibility.Visible : Visibility.Collapsed;
        _select.Visibility = grid && !selecting ? Visibility.Visible : Visibility.Collapsed;
        _more.Visibility = selecting ? Visibility.Collapsed : Visibility.Visible;
        _selectCount.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed;
        _selectActions.Visibility = selecting && !picking ? Visibility.Visible : Visibility.Collapsed;
        _pickActions.Visibility = picking ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Adding photos to a tag -----

    /// From inside a tag: 所有照片 in selecting mode, its bar adding to the tag.
    private void StartPicking()
    {
        if (_page != Page.Tag || _model is null) return;
        var tag = _tag;
        var from = _tagFrom;
        Go("all");
        _pickFor = tag;
        _pickFrom = from;
        SetSelecting(true);
        UpdateHeader();
        UpdateCommands();
    }

    /// Back to the tag, adding nothing.
    private void CancelPicking()
    {
        if (_pickFor is not { } tag) return;
        Go("tag:" + tag, _pickFrom);
    }

    private async Task FinishPickingAsync()
    {
        if (_pickFor is not { } tag || _model is null) return;
        var keys = SelectedKeys();
        var from = _pickFrom;
        foreach (var key in keys) _model.Index.AddTag(key, tag, TagSource.Manual);
        Go("tag:" + tag, from);
        await ReloadModelAsync();
    }

    internal void StartPickingForTest() => StartPicking();
    internal Task FinishPickingForTest() => FinishPickingAsync();
    internal bool Picking => _pickFor is not null;

    /// The "⋯" menu: what this page can do besides selecting. Words only.
    private void FillMoreMenu()
    {
        _moreMenu.Items.Clear();
        void Add(string text, Action click)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) => click();
            _moreMenu.Items.Add(item);
        }
        if (!ShowsTagCards && _grid.Entries.Count > 0) Add("幻灯片放映", () => OpenViewer(_grid.Entries[0], slideshow: true));
        switch (_page)
        {
            case Page.UserPage when CurrentUserPage is { } page:
                Add("编辑页面…", async () => await EditPageAsync());
                Add(page.Archived ? "移回侧栏" : "归档", async () => await ArchivePageAsync(!page.Archived));
                Add("删除页面…", async () => await DeletePageAsync());
                break;
            case Page.Tag:
                Add("重命名标签…", async () => await RenameTagAsync(_tag));
                Add("删除标签…", async () => await DeleteTagAsync(_tag));
                break;
            case Page.Tags:
                Add("新建页面…", async () => await NewPageAsync());
                break;
            case Page.Folder:
                Add("在资源管理器中打开", () => Shell.OpenFolder(_folder));
                Add("从 SPhoto 中移除…", async () => await RemoveFolderAsync());
                break;
        }
        if (_moreMenu.Items.Count > 0) _moreMenu.Items.Add(new MenuFlyoutSeparator());
        var refresh = new MenuFlyoutItem { Text = "刷新" };
        refresh.KeyboardAcceleratorTextOverride = "F5";
        refresh.Click += async (_, _) => await RefreshAsync();
        _moreMenu.Items.Add(refresh);
    }

    /// One card per tag, its cover a collage of its newest photos. On a
    /// page each card is small, medium (two cells wide) or large (two by two).
    private void BuildTagCards(IReadOnlyList<string>? only)
    {
        _cards.Children.Clear();
        if (_model is null) return;
        var page = CurrentUserPage;
        var tags = only ?? _model.Tags.Select(t => t.Name).ToList();
        foreach (var name in tags)
        {
            var count = _model.CountOf(name);
            var tag = name;
            var size = page?.SizeOf(tag) ?? CardSize.Small;
            int columns = size == CardSize.Small ? 1 : 2, rows = size == CardSize.Large ? 2 : 1;
            // A cell's cover is 24 narrower and 72 shorter than the cell (padding, margin, name and count).
            double width = columns * _cards.ItemWidth - 24, height = rows * _cards.ItemHeight - 72;
            var card = Card(name, count == 0 ? "还没有照片" : $"{count} 张", new TagCollage(name, _model.CoverPhotos(name, 4), width, height),
                            () => Go("tag:" + tag));
            VariableSizedWrapGrid.SetColumnSpan(card, columns);
            VariableSizedWrapGrid.SetRowSpan(card, rows);
            card.ContextFlyout = TagMenu(tag, page, size);
            _cards.Children.Add(card);
        }
    }

    private MenuFlyout TagMenu(string tag, PageInfo? page = null, CardSize size = CardSize.Small)
    {
        var menu = new MenuFlyout();
        if (page is not null)
        {
            // Sizes right in the menu, no submenu.
            foreach (var (choice, text) in new[] { (CardSize.Small, "小卡片"), (CardSize.Medium, "中卡片"), (CardSize.Large, "大卡片") })
            {
                var item = new RadioMenuFlyoutItem { Text = text, GroupName = "size", IsChecked = choice == size };
                var id = page.Id;
                item.Click += async (_, _) => await SetCardSizeAsync(id, tag, choice);
                menu.Items.Add(item);
            }
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        var rename = new MenuFlyoutItem { Text = "重命名…" };
        rename.Click += async (_, _) => await RenameTagAsync(tag);
        var delete = new MenuFlyoutItem { Text = "删除标签…" };
        delete.Click += async (_, _) => await DeleteTagAsync(tag);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        return menu;
    }

    private static Button Card(string title, string count, UIElement cover, Action click)
    {
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(cover);
        var name = Kit.Strong(title, 15);
        name.Margin = new Thickness(0, 10, 0, 0);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.TextWrapping = TextWrapping.NoWrap;
        stack.Children.Add(name);
        stack.Children.Add(Kit.Text(count, 13, secondary: true));
        var card = Kit.Flat(new Button
        {
            Content = stack,
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 12, 12),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        });
        card.Click += (_, _) => click();
        return card;
    }

    private async Task SetCardSizeAsync(long page, string tag, CardSize size)
    {
        if (_model is null) return;
        _model.Index.SetPageTagSize(page, tag, size);
        await ReloadModelAsync();
    }

    // ----- Pages and tags: names and choices -----

    internal async Task NewPageAsync()
    {
        var answer = await AskPageAsync("新建页面", "", Array.Empty<string>(), "创建");
        if (answer is not { } a || _model is null) return;
        long id = _model.Index.CreatePage(a.Name, a.Tags);
        await ReloadModelAsync();
        Go("page:" + id);
    }

    private async Task EditPageAsync()
    {
        if (CurrentUserPage is not { } page || _model is null) return;
        var answer = await AskPageAsync("编辑页面", page.Name, page.Tags, "完成");
        if (answer is not { } a) return;
        _model.Index.RenamePage(page.Id, a.Name);
        _model.Index.SetPageTags(page.Id, a.Tags);
        await ReloadModelAsync();
    }

    private async Task ArchivePageAsync(bool archived)
    {
        if (CurrentUserPage is not { } page || _model is null) return;
        _model.Index.ArchivePage(page.Id, archived);
        await ReloadModelAsync();
        if (archived) _archivedItem.IsExpanded = true;
        Go("page:" + page.Id);
    }

    private async Task DeletePageAsync()
    {
        if (CurrentUserPage is not { } page || _model is null) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, $"删除页面“{page.Name}”？",
                "只删除这个页面，标签和照片都还在。", "删除页面")) return;
        _model.Index.DeletePage(page.Id);
        await ReloadModelAsync();
        Go("all");
    }

    private async Task RenameTagAsync(string tag)
    {
        var name = await AskNameAsync("重命名标签", "标签名称", tag, "重命名");
        if (name is null || _model is null || name == tag) return;
        _model.Index.RenameTag(tag, name);
        await ReloadModelAsync();
        if (_page == Page.Tag && _tag == tag) Go("tag:" + LibraryIndex.CleanTag(name));
    }

    private async Task DeleteTagAsync(string tag)
    {
        if (_model is null) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, $"删除标签“{tag}”？",
                $"{_model.CountOf(tag)} 张照片会去掉这个标签，页面里也不再有它。照片本身不受影响。", "删除标签")) return;
        _model.Index.DeleteTag(tag);
        await ReloadModelAsync();
        if (_page == Page.Tag && _tag == tag) Go(_tagFrom is { } from ? "page:" + from : "tags");
    }

    /// A page's name and tags: a name box over a list of the library's tags to tick.
    private async Task<(string Name, IReadOnlyList<string> Tags)?> AskPageAsync(string title, string name, IReadOnlyList<string> chosen, string primary)
    {
        var box = new TextBox { PlaceholderText = "页面名称，比如“人物”", Text = name, MaxLength = 40 };
        var list = new StackPanel { Spacing = 2 };
        // Tags already on the page first, in their order, then the rest by use.
        var all = chosen.Concat((_model?.Tags ?? Array.Empty<LibraryModel.TagInfo>()).Select(t => t.Name).Where(t => !chosen.Contains(t))).ToList();
        var boxes = new List<CheckBox>();
        foreach (var tag in all)
        {
            var check = new CheckBox { Content = tag, IsChecked = chosen.Contains(tag), MinWidth = 0 };
            boxes.Add(check);
            list.Children.Add(check);
        }
        var content = new StackPanel { Spacing = 14, MinWidth = 360 };
        content.Children.Add(box);
        content.Children.Add(Kit.Text("显示这些标签的照片", 13, secondary: true));
        if (all.Count == 0)
        {
            content.Children.Add(Kit.Paragraph("还没有标签。先在照片的“信息”里加上标签，再回来选择。", 13));
        }
        else
        {
            content.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280 });
        }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        void Validate() => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        box.TextChanged += (_, _) => Validate();
        Validate();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return (box.Text.Trim(), boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content).ToList());
    }

    private async Task<string?> AskNameAsync(string title, string placeholder, string text, string primary)
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = text, MaxLength = 60 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().TrimStart('#').Trim().Length > 0;
        dialog.IsPrimaryButtonEnabled = text.Trim().Length > 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return box.Text.Trim();
    }

    /// Fills an "add tag" menu for the photos keys() returns: the most used
    /// tags, then a new one.
    internal void FillTagMenu(MenuFlyout menu, Func<IReadOnlyList<string>> keys)
    {
        menu.Items.Clear();
        foreach (var t in (_model?.Tags ?? Array.Empty<LibraryModel.TagInfo>()).Take(12))
        {
            var item = new MenuFlyoutItem { Text = t.Name };
            var name = t.Name;
            item.Click += async (_, _) => await AddTagAsync(name, keys());
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        var create = new MenuFlyoutItem { Text = "新标签…" };
        create.Click += async (_, _) =>
        {
            var chosen = keys();
            var name = await AskNameAsync("新标签", "标签名称", "", "添加");
            if (name is not null) await AddTagAsync(name, chosen);
        };
        menu.Items.Add(create);
    }

    internal async Task AddTagAsync(string tag, IReadOnlyList<string> keys)
    {
        if (_model is null || keys.Count == 0) return;
        foreach (var key in keys) _model.Index.AddTag(key, tag, TagSource.Manual);
        SetSelecting(false);
        await ReloadModelAsync();
    }

    // ----- Selection -----

    private void SetSelecting(bool on)
    {
        _select.IsChecked = on;
        _grid.Selecting = on;
        UpdateCommands();
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        int n = _grid.View.SelectedItems.Count;
        _selectCount.Text = n == 0 ? "点按照片来选择" : $"已选择 {n} 张";
        bool keys = SelectedKeys().Count > 0;
        _batchDelete.IsEnabled = n > 0;
        _batchFilter.IsEnabled = n > 0;
        _print.IsEnabled = n > 0;
        _addTag.IsEnabled = keys;
        _untag.IsEnabled = keys;
        _stack.IsEnabled = n > 1 && keys;
        _pickDone.IsEnabled = keys;
        if (_pickFor is { } tag) _pickDone.Content = n == 0 ? $"添加到“{tag}”" : $"把 {n} 张添加到“{tag}”";
    }

    /// Fingerprints of the selected photos, every shot of a stack tile included.
    private IReadOnlyList<string> SelectedKeys()
    {
        if (_model is null) return Array.Empty<string>();
        var keys = new List<string>();
        foreach (var p in _grid.Selected)
        {
            var members = InLibrary && _model.ShotOf(p.Path) is { } shot ? shot.Stack : new[] { p };
            foreach (var m in members)
            {
                if (_model.KeyOf(m.Path) is { } key) keys.Add(key);
            }
        }
        return keys.Distinct().ToList();
    }

    /// The photos on screen, one page each (a stack prints its cover).
    private void PrintSelection() => _ = PhotoPrinter.PrintAsync(XamlRoot, _grid.Selected);

    private async void StackSelection()
    {
        if (_model is null) return;
        var keys = SelectedKeys();
        if (keys.Count < 2) return;
        _model.Index.MergeIntoStack(keys);
        SetSelecting(false);
        await ReloadModelAsync();
    }

    private async void RemoveTagFromSelection()
    {
        if (_model is null || _page != Page.Tag) return;
        foreach (var key in SelectedKeys()) _model.Index.RemoveTag(key, _tag);
        SetSelecting(false);
        await ReloadModelAsync();
    }

    /// The files a delete of these tiles means: in 图库, every copy and every
    /// stacked shot (asked first); in 文件夹, just the files.
    private async Task DeleteSelectionAsync()
    {
        var removed = await DeleteAsync(_grid.Selected, InLibrary);
        if (removed.Count == 0) return;
        _entries = _entries.Where(p => !removed.Contains(p.Path)).ToList();
        SetSelecting(false);
        await RefreshAsync();
    }

    /// wholeStacks: a tile stands for its whole stack (the grid), not just
    /// the shot on screen (the viewer).
    internal async Task<HashSet<string>> DeleteAsync(IReadOnlyList<PhotoEntry> tiles, bool library, bool wholeStacks = true)
    {
        var files = new List<PhotoEntry>();
        int copies = 0, stacked = 0;
        foreach (var t in tiles)
        {
            if (!library || _model?.ShotOf(t.Path) is not { } shot)
            {
                files.Add(t);
                continue;
            }
            foreach (var member in wholeStacks ? shot.Stack : new[] { t })
            {
                if (!member.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase)) stacked++;
                foreach (var path in _model.CopiesOf(member.Path))
                {
                    if (!path.Equals(member.Path, StringComparison.OrdinalIgnoreCase)) copies++;
                    files.Add(path.Equals(member.Path, StringComparison.OrdinalIgnoreCase) ? member : EditActions.EntryFor(path, member.Taken));
                }
            }
        }
        if (copies + stacked > 0)
        {
            var parts = new List<string>();
            if (stacked > 0) parts.Add($"{stacked} 张叠放在一起的相似照片");
            if (copies > 0) parts.Add($"{copies} 个在别处的相同副本");
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"删除 {files.Count} 个文件？",
                Content = Kit.Paragraph($"除了看到的照片，还包括{string.Join("和", parts)}。它们都会移到回收站，需要时可以还原。", 14, secondary: false),
                PrimaryButtonText = "全部删除",
                SecondaryButtonText = "只删看到的",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            var answer = await dialog.ShowAsync();
            if (answer == ContentDialogResult.None) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (answer == ContentDialogResult.Secondary) files = tiles.ToList();
            var failed = await Shell.RecycleAsync(files.Select(f => f.Path));
            return files.Select(f => f.Path).Except(failed, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return await LibraryActions.DeleteAsync(XamlRoot, files);
    }

    /// Saves a filtered version of each selected photo; originals stay.
    internal async Task<int> ApplyFilterToSelectionAsync(string filter, bool report = true)
    {
        var photos = _grid.Selected;
        if (photos.Count == 0) return 0;
        int done = 0;
        var failed = new List<string>();
        _selectCount.Text = $"正在处理 0 / {photos.Count}…";
        foreach (var p in photos)
        {
            try
            {
                var session = new EditSession(p.Path);
                await session.LoadAsync();
                session.Apply(session.State.WithFilter(filter, 1f));
                var saved = await session.SaveCopyAsync();
                await Task.Run(() => LibraryModel.RecordVersion(App.Library.Index, p.Path, saved));
                done++;
            }
            catch (Exception)
            {
                failed.Add(p.Name);
            }
            _selectCount.Text = $"正在处理 {done + failed.Count} / {photos.Count}…";
        }
        SetSelecting(false);
        await RefreshAsync();
        if (!report) return done;
        var title = Filter.All.First(f => f.Id == filter).Title;
        await Dialogs.ShowAsync(XamlRoot, $"已存好 {done} 个“{title}”版本",
            failed.Count == 0 ? "新版本和原图在同一个文件夹里，文件名带“_已编辑”，在图库里叠在原图下面。原图没有改动。"
                              : "这些照片没能处理：" + string.Join("、", failed.Take(5)));
        return done;
    }

    // ----- Viewer and editor -----

    /// Opens the viewer on a photo of the page, growing it from its thumbnail.
    internal void OpenViewer(PhotoEntry entry, bool slideshow = false)
    {
        _viewer.Open(PagePhotos(), entry.Path, library: InLibrary, slideshow: slideshow, from: _grid.ImageOf(entry.Path));
    }

    internal void Edit(PhotoEntry entry) => _viewer.BeginEdit(entry);

    /// The viewer closed on a photo: keep it in view in the grid.
    internal void ViewerClosed(string? path)
    {
        if (_paneFoldedForPhoto) SetPaneShown(true);
        if (path is not null)
        {
            var cover = _model?.ShotOf(path)?.Cover.Path ?? path;
            _grid.ScrollTo(cover);
        }
        _grid.View.Focus(FocusState.Programmatic);
    }

    /// The editor closed; saved is the version it wrote, if any.
    internal async Task EditorClosedAsync(string original, string? saved)
    {
        await _viewer.EndEditAsync(saved);
        if (saved is null) return;
        await Task.Run(() => LibraryModel.RecordVersion(App.Library.Index, original, saved));
        await RefreshAsync();
    }

    internal void Removed(IEnumerable<string> paths)
    {
        var gone = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _entries = _entries.Where(p => !gone.Contains(p.Path)).ToList();
        _ = RefreshAsync();
    }

    // ----- UI test -----

    internal override async Task SelfTestAsync(UiTestLog log, string photo) =>
        await MainSelfTest.RunAsync(this, log, photo);

    internal bool IsBusy => _busy.IsActive;
    internal string PageTitle => _title.Text;
    internal int CardCount => _cards.Children.Count;
    internal bool CardsShown => _cardsScroll.Visibility == Visibility.Visible;
    internal Task SetCardSizeForTest(long page, string tag, CardSize size) => SetCardSizeAsync(page, tag, size);
    internal int FirstCardColumnsForTest => _cards.Children.Count > 0 ? VariableSizedWrapGrid.GetColumnSpan((FrameworkElement)_cards.Children[0]) : 0;
    /// Selecting shows its actions in the header and hides 选择 there.
    internal bool SelectionActionsInHeader => _selectCount.Visibility == Visibility.Visible && _select.Visibility == Visibility.Collapsed;
    internal void SelectForTest(bool on) => SetSelecting(on);
    internal Task<int> FilterSelectionForTest(string filter) => ApplyFilterToSelectionAsync(filter, report: false);
    internal void StackSelectionForTest() => StackSelection();
    internal void ShowPageAllForTest(bool all) => SetPageShowsAll(all);

    /// A click on a sidebar entry, as NavigationView reports it, then the selection.
    internal async Task ClickSidebarForTest(string tag)
    {
        await SidebarInvokedAsync(tag);
        Go(tag);
    }
}
