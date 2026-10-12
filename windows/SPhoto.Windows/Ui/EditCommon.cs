using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SPhoto.Editing;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace SPhoto.Ui;

/// <summary>A message strip over the photo: saved, failed, no GPU.</summary>
/// <remarks>Holds an InfoBar rather than deriving from it: a subclass of a
/// templated control does not get the control's style.</remarks>
internal sealed class Notice : Grid
{
    private readonly InfoBar _bar = new() { IsOpen = false };

    internal Notice()
    {
        VerticalAlignment = VerticalAlignment.Top;
        HorizontalAlignment = HorizontalAlignment.Center;
        Margin = new Thickness(16);
        MaxWidth = 640;
        Children.Add(_bar);
    }

    internal void Show(InfoBarSeverity severity, string message, string? reveal = null)
    {
        _bar.Severity = severity;
        _bar.Message = message;
        if (reveal is not null)
        {
            var button = new Button { Content = "在资源管理器中显示" };
            button.Click += (_, _) => Views.Shell.Reveal(reveal);
            _bar.ActionButton = button;
        }
        else
        {
            _bar.ActionButton = null;
        }
        _bar.IsOpen = true;
    }
}

/// <summary>A see-through layer with a spinner and a line of text.</summary>
internal sealed class BusyLayer : Grid
{
    private readonly TextBlock _text = Kit.Text("", 14);

    internal BusyLayer()
    {
        Visibility = Visibility.Collapsed;
        Background = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255));
        var stack = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new ProgressRing { IsActive = true, Width = 40, Height = 40 });
        _text.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(_text);
        Children.Add(stack);
    }

    internal void Set(string? text)
    {
        Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        _text.Text = text ?? "";
    }
}

/// <summary>What every design's editor does the same way.</summary>
internal static class EditActions
{
    /// While the button is held, the preview shows the original.
    internal static void HoldToCompare(UIElement button, PhotoPreview preview)
    {
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => preview.Comparing = true), true);
        foreach (var e in new[] { UIElement.PointerReleasedEvent, UIElement.PointerCanceledEvent, UIElement.PointerCaptureLostEvent, UIElement.PointerExitedEvent })
        {
            button.AddHandler(e, new PointerEventHandler((_, _) => preview.Comparing = false), true);
        }
    }

    /// Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z on an element.
    internal static void AddUndoKeys(UIElement element, EditSession session)
    {
        element.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        element.KeyboardAccelerators.Add(Kit.Key(VirtualKey.Z, VirtualKeyModifiers.Control, session.Undo));
        element.KeyboardAccelerators.Add(Kit.Key(VirtualKey.Y, VirtualKeyModifiers.Control, session.Redo));
        element.KeyboardAccelerators.Add(Kit.Key(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, session.Redo));
    }

    /// Saves "name_已编辑" next to the original. Returns the new file, or
    /// null after telling the user why not.
    internal static async Task<string?> SaveCopyAsync(EditSession session, Notice notice, BusyLayer? busy)
    {
        try
        {
            busy?.Set("正在保存…");
            var target = await session.SaveCopyAsync();
            notice.Show(InfoBarSeverity.Success, $"已保存为 {Path.GetFileName(target)}，原图未改动。", target);
            return target;
        }
        catch (Exception e)
        {
            notice.Show(InfoBarSeverity.Error, $"保存失败：{e.Message}");
            return null;
        }
        finally
        {
            busy?.Set(null);
        }
    }

    /// Asks where to save, then saves there.
    internal static async Task<string?> SaveAsAsync(EditSession session, Notice notice, BusyLayer? busy)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(session.Path) + "_已编辑",
        };
        picker.FileTypeChoices.Add("JPEG 图片", new List<string> { ".jpg" });
        picker.FileTypeChoices.Add("PNG 图片", new List<string> { ".png" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window.Hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return null;
        try
        {
            busy?.Set("正在保存…");
            await session.SaveToAsync(file.Path);
            notice.Show(InfoBarSeverity.Success, $"已保存为 {file.Name}，原图未改动。", file.Path);
            return file.Path;
        }
        catch (Exception e)
        {
            notice.Show(InfoBarSeverity.Error, $"保存失败：{e.Message}");
            return null;
        }
        finally
        {
            busy?.Set(null);
        }
    }

    /// Opens a session, reporting failure on the notice. True when loaded.
    internal static async Task<bool> LoadAsync(EditSession session, Notice notice, BusyLayer? busy)
    {
        try
        {
            busy?.Set("正在打开…");
            await session.LoadAsync();
            if (!await App.Worker.IsGpuAsync)
            {
                notice.Show(InfoBarSeverity.Informational, "没有找到可用的显卡加速，预览会慢一些。");
            }
            return true;
        }
        catch (Exception e)
        {
            notice.Show(InfoBarSeverity.Error, $"无法打开这张照片：{e.Message}");
            return false;
        }
        finally
        {
            busy?.Set(null);
        }
    }

    /// A PhotoEntry for a file just saved, dated like its original.
    internal static Services.PhotoEntry EntryFor(string path, DateTime taken)
    {
        var info = new FileInfo(path);
        return new Services.PhotoEntry(path, taken, info.Exists ? info.Length : 0);
    }
}
