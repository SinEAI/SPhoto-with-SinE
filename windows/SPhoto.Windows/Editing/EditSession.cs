using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SPhoto.Services;

namespace SPhoto.Editing;

/// <summary>
/// Editing one photo, independent of how a design lays it out: the decoded
/// photo, the current edit, undo history and saving. Lives on the UI thread;
/// rendering happens on the <see cref="RenderWorker"/>.
/// </summary>
internal sealed class EditSession
{
    private const int HistoryLimit = 200;

    private readonly List<EditState> _history = new() { EditState.Original };
    private int _historyPos;
    private readonly DispatcherQueueTimer _commitTimer;
    private int _fullWidth, _fullHeight;
    private long _sourceLength, _sourceMtime;

    internal EditSession(string path)
    {
        Path = path;
        _commitTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _commitTimer.Interval = TimeSpan.FromMilliseconds(450);
        _commitTimer.IsRepeating = false;
        _commitTimer.Tick += (_, _) => CommitNow();
    }

    internal string Path { get; }
    internal string Name => System.IO.Path.GetFileName(Path);
    internal Pixels? Source { get; private set; }
    internal EditState State { get; private set; } = EditState.Original;
    internal EditState Saved { get; private set; } = EditState.Original;

    /// The crop ratio the user picked: 0 free, -1 the photo's own, else w / h.
    internal double Ratio { get; set; }

    /// The last file this session saved.
    internal string? LastSaved { get; private set; }

    internal bool IsLoaded => Source is not null;
    internal bool IsDirty => !State.Equals(Saved);
    internal bool CanUndo => _historyPos > 0 || !State.Equals(_history[_historyPos]);
    internal bool CanRedo => _historyPos < _history.Count - 1;

    /// Raised after every change to State; the bool says whether it came
    /// from a continuous gesture (a slider drag) rather than a single step.
    internal event Action<bool>? Changed;

    internal async Task LoadAsync()
    {
        var before = new FileInfo(Path);
        _sourceLength = before.Length;
        _sourceMtime = before.LastWriteTimeUtc.Ticks;
        var decoded = await Task.Run(() => ImageIO.DecodeWithSizeAsync(Path, ImageIO.MaxEditSide, bgra: false));
        EnsureOriginalUnchanged();
        Source = decoded.Pixels;
        _fullWidth = decoded.FullWidth;
        _fullHeight = decoded.FullHeight;
        Changed?.Invoke(false);
    }

    /// The full-resolution size under a geometry.
    internal (int Width, int Height) OutputSize(Geometry geometry)
    {
        if (Source is null) return (1, 1);
        Native.sp_geometry_output_size(ref geometry, _fullWidth, _fullHeight, out var w, out var h);
        return (w, h);
    }

    /// Applies a change. Single steps commit to the history at once;
    /// continuous ones commit when they pause.
    internal void Apply(EditState state, bool continuous = false)
    {
        State = state;
        if (continuous)
        {
            _commitTimer.Stop();
            _commitTimer.Start();
        }
        else
        {
            CommitNow();
        }
        Changed?.Invoke(continuous);
    }

    internal void CommitNow()
    {
        _commitTimer.Stop();
        if (State.Equals(_history[_historyPos])) return;
        _history.RemoveRange(_historyPos + 1, _history.Count - _historyPos - 1);
        _history.Add(State);
        if (_history.Count > HistoryLimit) _history.RemoveAt(0);
        _historyPos = _history.Count - 1;
    }

    internal void Undo()
    {
        CommitNow();
        if (_historyPos == 0) return;
        State = _history[--_historyPos];
        Changed?.Invoke(false);
    }

    internal void Redo()
    {
        CommitNow();
        if (_historyPos >= _history.Count - 1) return;
        State = _history[++_historyPos];
        Changed?.Invoke(false);
    }

    internal void Reset()
    {
        Ratio = 0;
        Apply(EditState.Original);
    }

    // ----- Geometry steps that need the frame size -----

    internal void Turn(bool clockwise)
    {
        var g = CropMath.Turn(State.Geometry, clockwise);
        // A chosen ratio stays chosen: 4:5 is still 4:5 after turning.
        if (Ratio != 0) g = FitRatio(g, Ratio);
        Apply(State.WithGeometry(g));
    }

    internal void Flip() => Apply(State.WithGeometry(CropMath.Flip(State.Geometry)));

    internal void ResetGeometry()
    {
        Ratio = 0;
        Apply(State.WithGeometry(default));
    }

    internal void SetRatio(double ratio)
    {
        Ratio = ratio;
        if (ratio != 0) Apply(State.WithGeometry(FitRatio(State.Geometry, ratio)));
        else Changed?.Invoke(false);
    }

    internal Geometry FitRatio(Geometry g, double ratio)
    {
        var (fw, fh) = OutputSize(CropMath.Uncropped(g));
        return CropMath.FitRatio(g, ratio < 0 ? (double)fw / fh : ratio, fw, fh);
    }

    /// The locked crop ratio in frame units (width / height), or 0.
    internal double NormalizedRatio()
    {
        if (Ratio == 0) return 0;
        if (Ratio < 0) return 1;
        var (fw, fh) = OutputSize(CropMath.Uncropped(State.Geometry));
        return Ratio * fh / fw;
    }

    // ----- Saving -----

    /// Where "save a copy" writes: "name_已编辑.jpg" next to the original
    /// (PNG stays PNG), numbered when that exists.
    internal string CopyPath()
    {
        var folder = System.IO.Path.GetDirectoryName(Path) ?? "";
        var ext = ImageIO.IsPng(Path) ? ".png" : ".jpg";
        var stem = System.IO.Path.GetFileNameWithoutExtension(Path) + "_已编辑";
        var target = System.IO.Path.Combine(folder, stem + ext);
        for (int n = 2; File.Exists(target); n++) target = System.IO.Path.Combine(folder, $"{stem} ({n}){ext}");
        return target;
    }

    /// Renders at full size on a worker thread and writes target, copying
    /// the shooting date from the original. Throws on failure.
    internal async Task SaveToAsync(string target)
    {
        if (Source is null) throw new InvalidOperationException("照片还没有打开");
        if (string.Equals(System.IO.Path.GetFullPath(target), System.IO.Path.GetFullPath(Path),
                          StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("不能覆盖原图，请换一个文件名");
        }
        CommitNow();
        var state = State;
        var source = Source;
        var pixels = await Task.Run(async () =>
        {
            // The GPU-limited working image is enough for ordinary photos.
            // Larger originals are decoded at their own size only for CPU export.
            if (source.Width != _fullWidth || source.Height != _fullHeight)
            {
                EnsureOriginalUnchanged();
                if ((long)_fullWidth * _fullHeight > Array.MaxLength / 4)
                    throw new InvalidOperationException("图像像素过多，无法按原始尺寸导出");
                source = await ImageIO.DecodeAsync(Path, int.MaxValue, bgra: false);
                EnsureOriginalUnchanged();
                if (source.Width != _fullWidth || source.Height != _fullHeight)
                    throw new InvalidOperationException("原图尺寸已改变，请重新打开后再保存");
            }
            return PhotoRenderer.RenderFull(source, state);
        });
        await ImageIO.EncodeAsync(pixels, target, Path);
        Saved = state;
        LastSaved = target;
        Changed?.Invoke(false);
    }

    private void EnsureOriginalUnchanged()
    {
        var file = new FileInfo(Path);
        if (!file.Exists || file.Length != _sourceLength || file.LastWriteTimeUtc.Ticks != _sourceMtime)
            throw new InvalidOperationException("原图已改变，请重新打开后再保存");
    }

    internal async Task<string> SaveCopyAsync()
    {
        var target = CopyPath();
        await SaveToAsync(target);
        return target;
    }

    /// <summary>
    /// True when the session may close: nothing unsaved, or the user chose.
    /// Offers to save a copy first.
    /// </summary>
    internal async Task<bool> ConfirmCloseAsync(XamlRoot root)
    {
        CommitNow();
        if (!IsLoaded || !IsDirty) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "保存修改吗？",
            Content = "修改还没有保存。保存副本会另存一张新照片，原图保持不变。",
            PrimaryButtonText = "保存副本",
            SecondaryButtonText = "不保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                try
                {
                    await SaveCopyAsync();
                    return true;
                }
                catch (Exception e)
                {
                    await Views.Dialogs.ShowAsync(root, "保存失败", e.Message);
                    return false;
                }
            case ContentDialogResult.Secondary:
                return true;
            default:
                return false;
        }
    }
}
