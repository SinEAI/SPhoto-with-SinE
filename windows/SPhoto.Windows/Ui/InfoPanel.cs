using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SPhoto.Services;
using SPhoto.Views;

namespace SPhoto.Ui;

/// <summary>The facts about one photo: when, how big, which camera, where.</summary>
internal sealed class InfoPanel : StackPanel
{
    private readonly TextBlock _taken = Kit.Text(""), _name = Kit.Text(""), _size = Kit.Text("", 13, secondary: true);
    private readonly TextBlock _camera = Kit.Text("");
    private readonly StackPanel _cameraRow;
    private readonly HyperlinkButton _folder = new() { Padding = new Thickness(0) };
    private readonly TextBlock _folderText = Kit.Text("");
    private PhotoEntry? _entry;

    internal InfoPanel()
    {
        Spacing = 16;
        _taken.IsTextSelectionEnabled = true;
        _name.IsTextSelectionEnabled = true;
        _camera.IsTextSelectionEnabled = true;
        _folder.Content = _folderText;
        ToolTipService.SetToolTip(_folder, "在资源管理器中显示");
        _folder.Click += (_, _) =>
        {
            if (_entry is not null) Shell.Reveal(_entry.Path);
        };
        Children.Add(Field("拍摄时间", _taken));
        Children.Add(Field("文件", _name, _size));
        _cameraRow = Field("相机", _camera);
        Children.Add(_cameraRow);
        Children.Add(Field("位置", _folder));
    }

    private static StackPanel Field(string label, params UIElement[] values)
    {
        var p = new StackPanel { Spacing = 2 };
        p.Children.Add(Kit.Text(label, 12, secondary: true));
        foreach (var v in values) p.Children.Add(v);
        return p;
    }

    internal PhotoEntry? Entry => _entry;

    internal async void Show(PhotoEntry? entry)
    {
        _entry = entry;
        if (entry is null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }
        Visibility = Visibility.Visible;
        _name.Text = entry.Name;
        _folderText.Text = entry.Folder;
        _taken.Text = entry.Taken.ToString("yyyy年M月d日 HH:mm");
        _size.Text = Shell.FormatBytes(entry.Bytes);
        _cameraRow.Visibility = Visibility.Collapsed;
        try
        {
            var details = await ImageIO.DetailsAsync(entry.Path);
            if (_entry != entry) return;
            if (details.Taken is { } taken) _taken.Text = taken.ToString("yyyy年M月d日 HH:mm");
            double mp = details.Width * (double)details.Height / 1e6;
            _size.Text = $"{details.Width} × {details.Height} · {mp:F1} 百万像素 · {Shell.FormatBytes(details.Bytes)}";
            if (details.Camera is { } camera)
            {
                _camera.Text = camera;
                _cameraRow.Visibility = Visibility.Visible;
            }
        }
        catch (Exception)
        {
            // The basic facts stay.
        }
    }
}
