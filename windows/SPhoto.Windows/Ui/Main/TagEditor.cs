using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SPhoto.Library;
using Windows.System;

namespace SPhoto.Ui.Main;

/// <summary>
/// The tags of one photo as removable chips, and a box to add one (with
/// the library's existing tags suggested). Tags live in SPhoto's index,
/// never in the photo file; each remembers whether a person or SPhoto
/// added it, and SPhoto's own say so.
/// </summary>
internal sealed class TagEditor : StackPanel
{
    private readonly Func<string?> _key;
    private readonly Func<Task> _changed;
    private readonly WrapPanelLike _flow = new();
    private readonly AutoSuggestBox _box = new() { PlaceholderText = "添加标签，按 Enter" };
    private readonly Button _plus;

    internal TagEditor(Func<string?> key, Func<Task> changed)
    {
        _key = key;
        _changed = changed;
        Spacing = 10;
        Children.Add(_flow);
        // The plus works by itself: with nothing typed it lists the
        // library's tags, else it adds what is typed.
        _plus = Kit.IconButton(Kit.G.Add, "添加标签", () => _ = PlusAsync(), 14);
        _plus.VerticalAlignment = VerticalAlignment.Center;
        var row = Kit.Columns(Kit.Star(), Kit.Auto);
        row.ColumnSpacing = 4;
        row.Children.Add(_box);
        row.Children.Add(Kit.At(_plus, column: 1));
        Children.Add(row);
        _box.TextChanged += (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            var text = _box.Text.Trim().TrimStart('#');
            if (text.Length == 0) Suggest(null);
            else Suggest(text);
        };
        // A suggestion picked with the mouse or arrows goes straight on.
        _box.SuggestionChosen += (_, e) => _box.Text = e.SelectedItem as string ?? _box.Text;
        _box.QuerySubmitted += async (_, e) =>
        {
            var text = (e.ChosenSuggestion as string) ?? e.QueryText;
            // The plus with nothing typed lists the library's tags to pick from.
            if (e.ChosenSuggestion is null && text.Trim().Length == 0)
            {
                Suggest("");
                return;
            }
            await AddAsync(text);
        };
        _box.GotFocus += (_, _) => IsTyping = true;
        _box.LostFocus += (_, _) => IsTyping = false;
    }

    private async Task PlusAsync()
    {
        var text = _box.Text.Trim();
        if (text.Length > 0)
        {
            await AddAsync(text);
            return;
        }
        _box.Focus(FocusState.Programmatic);
        Suggest("");
    }

    /// <summary>
    /// Lists the library's tags this photo doesn't have yet: those that
    /// start with the text first, then those that contain it; "" lists
    /// them all (most used first), null closes the list.
    /// </summary>
    private void Suggest(string? text)
    {
        if (text is null)
        {
            _box.ItemsSource = null;
            _box.IsSuggestionListOpen = false;
            return;
        }
        var have = _key() is { } key
            ? App.Library.Index.TagsOf(key).Select(t => t.Name).ToHashSet(StringComparer.CurrentCultureIgnoreCase)
            : new System.Collections.Generic.HashSet<string>();
        var names = App.Library.Index.AllTags().Select(t => t.Name).Where(n => !have.Contains(n));
        var list = text.Length == 0
            ? names.Take(30).ToList()
            : names.Where(n => n.StartsWith(text, StringComparison.CurrentCultureIgnoreCase))
                .Concat(names.Where(n => !n.StartsWith(text, StringComparison.CurrentCultureIgnoreCase)
                                         && n.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
                .Take(8).ToList();
        _box.ItemsSource = list;
        _box.IsSuggestionListOpen = list.Count > 0;
    }

    /// Suggestions for the UI test.
    internal int SuggestForTest(string text)
    {
        Suggest(text);
        return (_box.ItemsSource as System.Collections.Generic.List<string>)?.Count ?? 0;
    }

    /// The box has the keyboard: the viewer's single-key shortcuts wait.
    internal bool IsTyping { get; private set; }

    internal async Task AddAsync(string text)
    {
        var name = text.Trim().TrimStart('#');
        if (name.Length == 0 || _key() is not { } key) return;
        App.Library.Index.AddTag(key, name, TagSource.Manual);
        _box.Text = "";
        Suggest(null);
        Show();
        await _changed();
    }

    private async Task RemoveAsync(string name)
    {
        if (_key() is not { } key) return;
        App.Library.Index.RemoveTag(key, name);
        Show();
        await _changed();
    }

    /// Shows the tags of the photo the key names now.
    internal void Show()
    {
        _flow.Children.Clear();
        var key = _key();
        _box.IsEnabled = _plus.IsEnabled = key is not null;
        if (key is null) return;
        foreach (var tag in App.Library.Index.TagsOf(key))
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(Kit.Text(tag.Name, 13));
            if (tag.Source == TagSource.Auto)
            {
                var auto = Kit.Text("自动", 11, secondary: true);
                auto.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(auto);
            }
            var remove = Kit.IconButton(Kit.G.Close, "去掉这个标签", () => _ = RemoveAsync(tag.Name), 10);
            remove.Padding = new Thickness(4);
            row.Children.Add(remove);
            _flow.Children.Add(new Border
            {
                Child = row,
                Background = Kit.White,
                BorderBrush = Kit.Divider,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(10, 2, 2, 2),
                Margin = new Thickness(0, 0, 6, 6),
            });
        }
    }

    internal int Count => _flow.Children.Count;
}

/// <summary>Lays children out left to right, wrapping at the edge.</summary>
internal sealed class WrapPanelLike : Panel
{
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        double x = 0, y = 0, row = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Windows.Foundation.Size(available.Width, double.PositiveInfinity));
            var d = child.DesiredSize;
            if (x > 0 && x + d.Width > available.Width)
            {
                y += row;
                x = row = 0;
            }
            x += d.Width;
            row = Math.Max(row, d.Height);
            width = Math.Max(width, x);
        }
        return new Windows.Foundation.Size(double.IsInfinity(available.Width) ? width : available.Width, y + row);
    }

    protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size final)
    {
        double x = 0, y = 0, row = 0;
        foreach (var child in Children)
        {
            var d = child.DesiredSize;
            if (x > 0 && x + d.Width > final.Width)
            {
                y += row;
                x = row = 0;
            }
            child.Arrange(new Windows.Foundation.Rect(x, y, d.Width, d.Height));
            x += d.Width;
            row = Math.Max(row, d.Height);
        }
        return final;
    }
}
