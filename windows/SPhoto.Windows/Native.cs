using System;
using System.Runtime.InteropServices;

namespace SPhoto;

/// <summary>P/Invoke declarations for core/include/sphoto/sphoto.h.</summary>
internal static class Native
{
    private const string Dll = "sphoto_core";

    internal static string Version => Marshal.PtrToStringAnsi(sp_version()) ?? "";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sp_version();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_make_test_image(byte[] dst, int width, int height);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sp_egl_context_create();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sp_egl_context_destroy(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sp_gles_renderer_create();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_renderer_set_image(IntPtr renderer, byte[] rgba, int width, int height);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_param_count();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_param_info(int index, out IntPtr name, out float min, out float max);

    // sp_params is sp_param_count() floats in a row, so a float[] of that
    // length stands in for it.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sp_renderer_set_params(IntPtr renderer, float[] values);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_filter_count();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sp_filter_id(int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_renderer_set_filter(IntPtr renderer,
        [MarshalAs(UnmanagedType.LPStr)] string? id, float strength);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sp_renderer_set_geometry(IntPtr renderer, ref Geometry geometry);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_renderer_output_size(IntPtr renderer, out int width, out int height);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_renderer_render_rgba(IntPtr renderer, byte[] dst, int width, int height);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sp_renderer_destroy(IntPtr renderer);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_renderer_set_edit(IntPtr renderer, ref Edit edit);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_geometry_output_size(ref Geometry geometry, int srcWidth, int srcHeight,
                                                       out int width, out int height);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sp_render_cpu(ref Edit edit, byte[] src, int width, int height,
                                             byte[] dst, int outWidth, int outHeight);
}

/// <summary>
/// sp_edit. sp_params is <see cref="ParamCount"/> floats; App checks at
/// startup that the core agrees. Every array must be allocated at its full
/// size before marshalling (see EditState.ToNative).
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
internal struct Edit
{
    internal const int ParamCount = 12;
    /// sizeof(sp_edit); SelfTest checks Marshal.SizeOf agrees.
    internal const int NativeSize = 488;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = ParamCount)]
    public float[] Adjust;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string Filter;
    public float FilterStrength;
    public Geometry Geometry;
    /// rgb (all channels), red, green, blue.
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
    public Curve[] Curves;
    public Hsl Hsl;
}

/// <summary>sp_curve: Count points (0 or 1 is the identity), x ascending.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Curve
{
    public int Count;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public float[] X;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public float[] Y;

    internal static Curve Identity => new() { X = new float[8], Y = new float[8] };
}

/// <summary>sp_hsl: one amount per colour band, red to magenta.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Hsl
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public float[] Hue;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public float[] Saturation;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public float[] Luminance;

    internal static Hsl None => new() { Hue = new float[8], Saturation = new float[8], Luminance = new float[8] };
}

/// <summary>sp_geometry.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Geometry
{
    public int QuarterTurns;
    public int FlipHorizontal;
    public float Vertical, Horizontal, Straighten;
    public float CropX, CropY, CropWidth, CropHeight;
}
