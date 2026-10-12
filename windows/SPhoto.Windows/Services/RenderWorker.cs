using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SPhoto.Editing;

namespace SPhoto.Services;

/// <summary>A rendered frame, already in the BGRA order XAML bitmaps use.</summary>
internal sealed record RenderedFrame(byte[] Bgra, int Width, int Height, EditState State);

/// <summary>
/// Renders edits on a thread of its own, which owns the GL context, so the
/// UI thread never waits for the GPU or for pixels to be read back. A
/// preview request replaces any earlier one from the same caller that has
/// not started yet: dragging a slider costs one render per frame the
/// worker can manage, never a growing queue.
/// </summary>
internal sealed class RenderWorker : IDisposable
{
    private sealed class PreviewRequest
    {
        internal required Pixels Image;
        internal required EditState State;
        internal required int Width, Height;
        internal required Action<RenderedFrame?> Done;
    }

    private readonly Thread _thread;
    private readonly object _lock = new();
    private readonly Queue<Action<PhotoRenderer>> _jobs = new();
    private readonly Dictionary<object, PreviewRequest> _previews = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource<bool> _ready = new();
    private bool _stop;
    private PhotoRenderer? _renderer;

    internal RenderWorker()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "SPhoto render" };
        _thread.Start();
    }

    /// True once the worker knows it has a GPU; false for the CPU fallback.
    internal Task<bool> IsGpuAsync => _ready.Task;

    private void Loop()
    {
        try
        {
            _renderer = new PhotoRenderer();
            _ready.TrySetResult(_renderer.IsGpu);
        }
        catch (Exception e)
        {
            _ready.TrySetException(e);
            return;
        }
        while (true)
        {
            _wake.WaitOne();
            while (true)
            {
                Action<PhotoRenderer>? job = null;
                PreviewRequest? preview = null;
                lock (_lock)
                {
                    if (_stop) goto done;
                    if (_jobs.Count > 0) job = _jobs.Dequeue();
                    else
                    {
                        foreach (var pair in _previews)
                        {
                            preview = pair.Value;
                            _previews.Remove(pair.Key);
                            break;
                        }
                    }
                }
                if (job is not null) job(_renderer);
                else if (preview is not null) RenderPreview(preview);
                else break;
            }
        }
    done:
        _renderer.Dispose();
    }

    private void RenderPreview(PreviewRequest request)
    {
        RenderedFrame? frame = null;
        try
        {
            frame = RenderNow(_renderer!, request.Image, request.State, request.Width, request.Height);
        }
        catch (Exception)
        {
            // Reported to the caller as no frame.
        }
        request.Done(frame);
    }

    /// Renders on the worker thread (call only from a job).
    internal static RenderedFrame? RenderNow(PhotoRenderer renderer, Pixels image, EditState state, int width, int height)
    {
        if (!ReferenceEquals(renderer.Image, image)) renderer.SetImage(image);
        renderer.SetEdit(state);
        var rgba = new byte[width * height * 4];
        if (!renderer.Render(rgba, width, height)) return null;
        // RGBA to BGRA, opaque.
        for (int i = 0; i < rgba.Length; i += 4)
        {
            (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);
            rgba[i + 3] = 255;
        }
        return new RenderedFrame(rgba, width, height, state);
    }

    /// <summary>
    /// Asks for a preview. done runs on the worker thread; owner identifies
    /// the caller so a newer request replaces an older one.
    /// </summary>
    internal void RequestPreview(object owner, Pixels image, EditState state, int width, int height, Action<RenderedFrame?> done)
    {
        lock (_lock)
        {
            _previews[owner] = new PreviewRequest { Image = image, State = state, Width = width, Height = height, Done = done };
        }
        _wake.Set();
    }

    /// Runs work on the worker thread with its renderer.
    internal Task<T> RunAsync<T>(Func<PhotoRenderer, T> work)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            _jobs.Enqueue(r =>
            {
                try
                {
                    result.SetResult(work(r));
                }
                catch (Exception e)
                {
                    result.SetException(e);
                }
            });
        }
        _wake.Set();
        return result.Task;
    }

    public void Dispose()
    {
        lock (_lock) _stop = true;
        _wake.Set();
    }
}
