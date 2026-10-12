using System;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;

namespace SPhoto.Ui;

/// <summary>
/// Small builders for the code-built UI, in the light, late Windows 10
/// look: white surfaces, light grey chrome, thin dividers, borderless
/// labelled commands and the system accent colour. Type uses the system
/// fonts (Segoe UI, Microsoft YaHei UI for Chinese): bold titles with a
/// little tracking, 15 px regular body text with airy line spacing.
/// </summary>
internal static class Kit
{
    internal static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    internal static Color Accent => (Color)Application.Current.Resources["SystemAccentColor"];

    internal static readonly SolidColorBrush White = new(Colors.White);
    internal static readonly SolidColorBrush Chrome = new(Color.FromArgb(255, 0xF2, 0xF2, 0xF2));
    internal static readonly SolidColorBrush Stage = new(Color.FromArgb(255, 0xE9, 0xE9, 0xE9));
    internal static readonly SolidColorBrush Divider = new(Color.FromArgb(255, 0xE1, 0xE1, 0xE1));
    internal static readonly SolidColorBrush Secondary = new(Color.FromArgb(255, 0x60, 0x60, 0x60));
    internal static readonly SolidColorBrush Tertiary = new(Color.FromArgb(255, 0x8A, 0x8A, 0x8A));
    internal static readonly SolidColorBrush Tile = new(Color.FromArgb(255, 0xEE, 0xEE, 0xEE));
    internal static SolidColorBrush AccentBrush => new(Accent);

    /// Windows 10 hover and press shades for borderless buttons.
    internal static readonly SolidColorBrush Hover = new(Color.FromArgb(0x19, 0, 0, 0));
    internal static readonly SolidColorBrush Press = new(Color.FromArgb(0x33, 0, 0, 0));
    internal static readonly SolidColorBrush Clear = new(Colors.Transparent);

    internal static TextBlock Text(string text, double size = 15, bool secondary = false, FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Foreground = secondary ? Secondary : new SolidColorBrush(Colors.Black),
        };
        if (weight is { } w) t.FontWeight = w;
        return t;
    }

    /// A paragraph: wrapped body text with room between the lines.
    internal static TextBlock Paragraph(string text, double size = 14, bool secondary = true)
    {
        var t = Text(text, size, secondary);
        t.LineHeight = Math.Round(size * 1.65);
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        return t;
    }

    /// Titles: bold, slightly tracked so Chinese headings breathe.
    internal static TextBlock Title(string text, double size = 28)
    {
        var t = Text(text, size, weight: FontWeights.Bold);
        t.CharacterSpacing = 30;
        return t;
    }

    /// Borderless with the Windows 10 hover and press shades.
    internal static T Flat<T>(T control) where T : Control
    {
        foreach (var key in new[] { "ButtonBackground", "ToggleButtonBackground" }) control.Resources[key] = Clear;
        foreach (var key in new[] { "ButtonBackgroundPointerOver", "ToggleButtonBackgroundPointerOver" }) control.Resources[key] = Hover;
        foreach (var key in new[] { "ButtonBackgroundPressed", "ToggleButtonBackgroundPressed" }) control.Resources[key] = Press;
        foreach (var key in new[] { "ButtonBorderBrush", "ButtonBorderBrushPointerOver", "ButtonBorderBrushPressed",
                                    "ToggleButtonBorderBrush", "ToggleButtonBorderBrushPointerOver", "ToggleButtonBorderBrushPressed" })
        {
            control.Resources[key] = Clear;
        }
        control.Background = Clear;
        control.BorderThickness = new Thickness(0);
        return control;
    }

    /// A command bar in the Windows 10 Photos manner: borderless commands
    /// with 20 px icons and labels beside them.
    internal static CommandBar Bar()
    {
        var bar = new CommandBar
        {
            // Measuring for overflow in an auto-sized column can loop the layout.
            IsDynamicOverflowEnabled = false,
            DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
            Background = Clear,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bar.Resources["AppBarButtonContentHeight"] = 20.0;
        bar.Resources["AppBarThemeCompactHeight"] = 48.0;
        return bar;
    }

    internal static TextBlock Strong(string text, double size = 14) => Text(text, size, weight: FontWeights.SemiBold);

    internal static FontIcon Icon(string glyph, double size = 16) => new() { Glyph = glyph, FontSize = size };

    internal static AppBarButton Command(string label, string glyph, Action click, string? tip = null)
    {
        var b = new AppBarButton { Label = label, Icon = Icon(glyph) };
        ToolTipService.SetToolTip(b, tip ?? label);
        b.Click += (_, _) => click();
        return b;
    }

    internal static AppBarToggleButton Toggle(string label, string glyph, Action<bool> changed, string? tip = null)
    {
        var b = new AppBarToggleButton { Label = label, Icon = Icon(glyph) };
        ToolTipService.SetToolTip(b, tip ?? label);
        b.Click += (_, _) => changed(b.IsChecked == true);
        return b;
    }

    internal static Button IconButton(string glyph, string tip, Action click, double size = 16)
    {
        var b = Flat(new Button
        {
            Content = Icon(glyph, size),
            Padding = new Thickness(10, 8, 10, 8),
        });
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    internal static Button TextButton(string text, Action click, bool accent = false, string? glyph = null)
    {
        object content = text;
        if (glyph is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(Icon(glyph, 14));
            row.Children.Add(new TextBlock { Text = text });
            content = row;
        }
        var b = new Button { Content = content };
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        b.Click += (_, _) => click();
        return b;
    }

    internal static Border Line(bool vertical = false) => new()
    {
        Background = Divider,
        Width = vertical ? 1 : double.NaN,
        Height = vertical ? double.NaN : 1,
    };

    internal static KeyboardAccelerator Key(VirtualKey key, VirtualKeyModifiers modifiers, Action invoked)
    {
        var k = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        k.Invoked += (_, e) =>
        {
            e.Handled = true;
            invoked();
        };
        return k;
    }

    internal static UIElement At(UIElement e, int row = 0, int column = 0, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow((FrameworkElement)e, row);
        Grid.SetColumn((FrameworkElement)e, column);
        if (rowSpan > 1) Grid.SetRowSpan((FrameworkElement)e, rowSpan);
        if (columnSpan > 1) Grid.SetColumnSpan((FrameworkElement)e, columnSpan);
        return e;
    }

    internal static Grid Rows(params GridLength[] heights)
    {
        var g = new Grid();
        foreach (var h in heights) g.RowDefinitions.Add(new RowDefinition { Height = h });
        return g;
    }

    internal static Grid Columns(params GridLength[] widths)
    {
        var g = new Grid();
        foreach (var w in widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return g;
    }

    internal static GridLength Auto => GridLength.Auto;
    internal static GridLength Star(double v = 1) => new(v, GridUnitType.Star);
    internal static GridLength Px(double v) => new(v);

    /// A centred message with an icon, for empty and error states.
    internal static StackPanel Message(string glyph, string title, string text, Button? action = null)
    {
        var p = new StackPanel
        {
            Spacing = 10,
            MaxWidth = 440,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        p.Children.Add(new FontIcon { Glyph = glyph, FontSize = 44, Foreground = Tertiary });
        var t = Title(title, 22);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.TextAlignment = TextAlignment.Center;
        p.Children.Add(t);
        var body = Paragraph(text);
        body.HorizontalAlignment = HorizontalAlignment.Center;
        body.TextAlignment = TextAlignment.Center;
        p.Children.Add(body);
        if (action is not null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Center;
            p.Children.Add(action);
        }
        return p;
    }

    /// Glyphs from Segoe MDL2 Assets, the Windows 10 icon font.
    internal static class G
    {
        internal const string Back = "", Forward = "", Left = "", Right = "";
        internal const string Edit = "", Delete = "", Info = "", More = "";
        internal const string Save = "", SaveAs = "", Undo = "", Redo = "";
        internal const string Compare = "", Rotate = "", Flip = "", Crop = "";
        internal const string Filter = "", Adjust = "", Folder = "", FolderAdd = "";
        internal const string Photo = "", Pictures = "", Refresh = "", Settings = "";
        internal const string Explorer = "", Copy = "", Select = "", Fit = "";
        internal const string Close = "", Check = "", Slideshow = "", Share = "";
        internal const string Calendar = "", Album = "", Star = "", Sort = "";
        internal const string FullScreen = "", Tag = "", Sun = "", Color = "";
        internal const string Effects = "", Print = "", Search = "", Add = "";
        internal const string Menu = "\uE700", ChevronUp = "\uE70E", ChevronDown = "\uE70D";
    }
}
