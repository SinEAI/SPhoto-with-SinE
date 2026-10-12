using System;
using SPhoto.Editing;

namespace SPhoto.Services;

/// <summary>
/// Renders edits of one photo. Uses the core's OpenGL ES renderer through
/// ANGLE when the GPU is available, and the core's CPU pipeline otherwise,
/// so editing still works (more slowly) on a PC without a usable GPU.
/// All calls must come from the UI thread, which owns the GL context.
/// </summary>
internal sealed class PhotoRenderer : IDisposable
{
    private readonly IntPtr _context;
    private readonly IntPtr _renderer;
    private Pixels? _image;
    private Edit _edit = EditState.Original.ToNative();

    internal PhotoRenderer()
    {
        _context = Native.sp_egl_context_create();
        if (_context != IntPtr.Zero) _renderer = Native.sp_gles_renderer_create();
        if (_renderer == IntPtr.Zero && _context != IntPtr.Zero)
        {
            Native.sp_egl_context_destroy(_context);
            _context = IntPtr.Zero;
        }
    }

    internal bool IsGpu => _renderer != IntPtr.Zero;

    internal Pixels? Image => _image;

    internal void SetImage(Pixels rgba)
    {
        _image = rgba;
        if (IsGpu) Native.sp_renderer_set_image(_renderer, rgba.Data, rgba.Width, rgba.Height);
    }

    internal void SetEdit(EditState state)
    {
        _edit = state.ToNative();
        if (IsGpu) Native.sp_renderer_set_edit(_renderer, ref _edit);
    }

    /// The full-resolution size of the photo under this geometry.
    internal (int Width, int Height) OutputSize(Geometry geometry)
    {
        if (_image is null) return (1, 1);
        Native.sp_geometry_output_size(ref geometry, _image.Width, _image.Height, out var w, out var h);
        return (w, h);
    }

    /// Renders the current edit into width x height RGBA pixels.
    internal bool Render(byte[] rgba, int width, int height)
    {
        if (_image is null) return false;
        if (IsGpu) return Native.sp_renderer_render_rgba(_renderer, rgba, width, height) == 0;
        return Native.sp_render_cpu(ref _edit, _image.Data, _image.Width, _image.Height, rgba, width, height) == 0;
    }

    /// <summary>
    /// Renders an edit at full resolution on the CPU. Thread-safe: the save
    /// runs on a worker thread while the editor stays responsive.
    /// </summary>
    internal static Pixels RenderFull(Pixels image, EditState state)
    {
        var edit = state.ToNative();
        var g = edit.Geometry;
        Native.sp_geometry_output_size(ref g, image.Width, image.Height, out var w, out var h);
        var dst = new byte[(long)w * h * 4];
        if (Native.sp_render_cpu(ref edit, image.Data, image.Width, image.Height, dst, w, h) != 0)
        {
            throw new InvalidOperationException("编辑内核渲染失败");
        }
        return new Pixels(dst, w, h);
    }

    public void Dispose()
    {
        if (_renderer != IntPtr.Zero) Native.sp_renderer_destroy(_renderer);
        if (_context != IntPtr.Zero) Native.sp_egl_context_destroy(_context);
    }
}
