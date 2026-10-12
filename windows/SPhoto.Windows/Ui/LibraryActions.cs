using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using SPhoto.Services;
using SPhoto.Views;
using Windows.Storage.Pickers;

namespace SPhoto.Ui;

/// <summary>Library actions every design offers the same way.</summary>
internal static class LibraryActions
{
    /// Asks for a folder and adds it. Returns it, or null.
    internal static async Task<string?> AddFolderAsync(XamlRoot root)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Window.Hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return null;
        if (!App.Library.AddFolder(folder.Path))
        {
            await Dialogs.ShowAsync(root, "没有添加这个文件夹",
                "它已经在图库中，或者位于图库里某个文件夹之内。\n" + folder.Path);
            return null;
        }
        return Path.GetFullPath(folder.Path).TrimEnd(Path.DirectorySeparatorChar);
    }

    /// Asks, then moves photos to the Recycle Bin. Returns the ones removed.
    internal static async Task<HashSet<string>> DeleteAsync(XamlRoot root, IReadOnlyList<PhotoEntry> photos)
    {
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (photos.Count == 0) return removed;
        var title = photos.Count == 1 ? $"删除“{photos[0].Name}”？" : $"删除这 {photos.Count} 张照片？";
        if (!await Dialogs.ConfirmAsync(root, title, "照片会移到回收站，需要时可以从回收站还原。", "删除")) return removed;
        var paths = photos.Select(p => p.Path).ToList();
        var failed = await Shell.RecycleAsync(paths);
        removed.UnionWith(paths.Except(failed, StringComparer.OrdinalIgnoreCase));
        if (failed.Count > 0)
        {
            await Dialogs.ShowAsync(root, $"有 {failed.Count} 张照片没能删除",
                "它们可能正被其他程序使用，或者没有删除权限。\n" + string.Join("\n", failed.Take(5)));
        }
        return removed;
    }

    /// "2024年5月" style month title.
    internal static string Month(PhotoEntry p) => $"{p.Taken.Year}年{p.Taken.Month}月";

    /// "2024年5月6日 星期一" style day title.
    internal static string Day(PhotoEntry p) =>
        $"{p.Taken.Year}年{p.Taken.Month}月{p.Taken.Day}日 星期{"日一二三四五六"[(int)p.Taken.DayOfWeek]}";

    internal static bool IsEditedCopy(PhotoEntry p) =>
        Path.GetFileNameWithoutExtension(p.Path).Contains("_已编辑", StringComparison.Ordinal);

    internal static string FolderName(string folder) =>
        Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : folder;
}
