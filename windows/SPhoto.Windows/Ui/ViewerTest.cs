using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SPhoto.Ui.Main;
using Windows.Foundation;

namespace SPhoto.Ui;

/// <summary>Explorer selection and real mouse input against the displayed viewer.</summary>
internal static class ViewerTest
{
    internal static async Task RunAsync(UiTestLog log, MainDesign design, string folder, string largePhoto)
    {
        var viewer = design.Viewer.Photo;
        var siblings = Services.PhotoLibrary.Siblings(largePhoto);
        log.Check(siblings.FindIndex(p => p.Path == largePhoto) > 0, "viewer fixture includes earlier-named siblings");
        foreach (var path in new[] { largePhoto, Path.Combine(folder, "旅行", "IMG_0002.jpg"), largePhoto })
        {
            var loaded = new TaskCompletionSource<bool>();
            void OnLoaded(bool ok)
            {
                if (string.Equals(viewer.Current?.Path, path, StringComparison.OrdinalIgnoreCase)) loaded.TrySetResult(ok);
            }
            viewer.PhotoLoaded += OnLoaded;
            try
            {
                int siblingCount = Services.PhotoLibrary.Siblings(path).Count;
                design.OpenFile(path);
                log.Check(await loaded.Task.WaitAsync(TimeSpan.FromSeconds(15)), "Explorer loads the requested image pixels");
                log.Check(await UiTestLog.WaitAsync(() => viewer.Photos.Count == siblingCount),
                          "Explorer discovers the requested folder's siblings");
                await Task.Delay(300);
                log.Check(string.Equals(viewer.Current?.Path, path, StringComparison.OrdinalIgnoreCase),
                          "folder discovery and reopening preserve the requested photo");
            }
            finally { viewer.PhotoLoaded -= OnLoaded; }
        }

        // Test both list replacement and the user's subsequent filmstrip navigation.
        var chosen = siblings.Last();
        design.Viewer.Open(siblings, chosen.Path, library: false);
        await Task.Delay(400);
        log.Check(viewer.Current?.Path == chosen.Path, "opening a populated list keeps its requested last item");
        design.Viewer.FilmForTest.Reveal(siblings[0].Path);
        log.Check(await UiTestLog.WaitAsync(() => viewer.Current?.Path == siblings[0].Path),
                  "an explicit filmstrip selection still changes the photo");
        design.OpenFile(largePhoto);
        log.Check(await UiTestLog.WaitAsync(() => viewer.Current?.Path == largePhoto && viewer.Photos.Count == siblings.Count),
                  "back to the large photo for mouse input");
        await Task.Delay(500);

        var scroller = viewer.ScrollerForTest;
        scroller.ChangeView(250, 200, 3f, disableAnimation: true);
        log.Check(await UiTestLog.WaitAsync(() => scroller.ZoomFactor > 2.9 && scroller.ScrollableWidth > 200 && scroller.ScrollableHeight > 200),
                  "zoomed fixture can pan on both axes");
        await Task.Delay(200);
        double x = scroller.HorizontalOffset, y = scroller.VerticalOffset;
        var origin = viewer.TransformToVisual(design.XamlRoot.Content).TransformPoint(
            new Point(viewer.ActualWidth * .55, viewer.ActualHeight * .4));
        double scale = design.XamlRoot.RasterizationScale;
        var start = new NativePoint { X = (int)(origin.X * scale), Y = (int)(origin.Y * scale) };
        ClientToScreen(App.Window.Hwnd, ref start);
        GetCursorPos(out var saved);
        try
        {
            // Keep the synthetic test window above other apps while injecting
            // input; a background window otherwise tests whichever app is on top.
            SetWindowPos(App.Window.Hwnd, (IntPtr)(-1), 0, 0, 0, 0, 0x13);
            App.Window.BringToFront();
            await MoveToAsync(start.X, start.Y);
            GetCursorPos(out var actual);
            log.Check(GetAncestor(WindowFromPoint(actual), 2) == App.Window.Hwnd, "mouse input targets the synthetic viewer window");
            SendMouse(2); // left down
            log.Check(await UiTestLog.WaitAsync(() => viewer.IsDraggingForTest), "the viewer captures a zoomed mouse press");
            for (int i = 1; i <= 6; i++)
            {
                MoveTo(start.X - 20 * i, start.Y - 12 * i);
                await Task.Delay(35);
            }
            await WaitForCursorAsync(start.X - 120, start.Y - 72);
            log.Check(await UiTestLog.WaitAsync(() => scroller.HorizontalOffset > x + 60 / scale && scroller.VerticalOffset > y + 30 / scale),
                      "left-button drag pans the zoomed photo horizontally and vertically");
            log.Note($"pan offsets {x:F1},{y:F1} -> {scroller.HorizontalOffset:F1},{scroller.VerticalOffset:F1}");
            log.Check(viewer.Current?.Path == largePhoto, "panning never switches to a sibling");
            SendMouse(4); // left up
            log.Check(await UiTestLog.WaitAsync(() => !viewer.IsDraggingForTest), "release ends the active pointer gesture");
            await Task.Delay(100);
            double afterX = scroller.HorizontalOffset, afterY = scroller.VerticalOffset;
            await MoveToAsync(start.X, start.Y);
            log.Check(Math.Abs(scroller.HorizontalOffset - afterX) < 2 && Math.Abs(scroller.VerticalOffset - afterY) < 2,
                      "moving after release does not keep dragging");
            for (int click = 0; click < 2; click++)
            {
                SendMouse(2);
                SendMouse(4);
                await Task.Delay(50);
            }
            log.Check(await UiTestLog.WaitAsync(() => scroller.ZoomFactor <= 1.01), "double click still fits the photo after panning");
            viewer.FitToWindow();
            await Task.Delay(200);
            int delta = viewer.Index > 0 ? -1 : 1;
            string following = viewer.Photos[viewer.Index + delta].Path;
            int distance = (int)(viewer.ActualWidth * scale * .35) * -delta;
            bool PastThreshold() => Motion.Transform(viewer.PictureForTest).TranslateX * -delta > viewer.ActualWidth / 5;
            await MoveToAsync(start.X, start.Y);
            SendMouse(2);
            await UiTestLog.WaitAsync(() => viewer.IsDraggingForTest);
            MoveTo(start.X + distance, start.Y);
            log.Check(await UiTestLog.WaitAsync(PastThreshold),
                      "at fit size the photo follows a sideways drag");
            viewer.ReleasePointerCaptures();
            log.Check(await UiTestLog.WaitAsync(() => !viewer.IsDraggingForTest), "capture loss cancels a fit-size swipe");
            SendMouse(4);
            await Task.Delay(200);
            log.Check(viewer.Current?.Path == largePhoto, "a cancelled swipe never navigates");

            await MoveToAsync(start.X, start.Y);
            SendMouse(2);
            await UiTestLog.WaitAsync(() => viewer.IsDraggingForTest);
            MoveTo(start.X + distance, start.Y);
            await UiTestLog.WaitAsync(PastThreshold);
            SendMouse(4);
            log.Check(await UiTestLog.WaitAsync(() => viewer.Current?.Path == following), "releasing a fit-size swipe still shows the neighboring photo");
        }
        finally
        {
            SendMouse(4);
            SetWindowPos(App.Window.Hwnd, (IntPtr)(-2), 0, 0, 0, 0, 0x13);
            SetCursorPos(saved.X, saved.Y);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { internal int X, Y; }
    private static async Task MoveToAsync(int x, int y)
    {
        MoveTo(x, y);
        await WaitForCursorAsync(x, y);
        await Task.Delay(100);
    }
    private static Task<bool> WaitForCursorAsync(int x, int y) => UiTestLog.WaitAsync(() =>
        GetCursorPos(out var point) && Math.Abs(point.X - x) <= 2 && Math.Abs(point.Y - y) <= 2, 3000);
    private static void MoveTo(int x, int y)
    {
        uint absoluteX = (uint)Math.Clamp((x - GetSystemMetrics(76)) * 65535L / (GetSystemMetrics(78) - 1), 0, 65535);
        uint absoluteY = (uint)Math.Clamp((y - GetSystemMetrics(77)) * 65535L / (GetSystemMetrics(79) - 1), 0, 65535);
        SendMouse(0xC001, absoluteX, absoluteY); // move, absolute, virtual desktop
    }
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal UIntPtr Extra;
    }
    [StructLayout(LayoutKind.Explicit)] private struct Input
    {
        [FieldOffset(0)] internal uint Type;
        [FieldOffset(8)] internal MouseInput Mouse;
    }
    private static void SendMouse(uint flags, uint x = 0, uint y = 0)
    {
        var inputs = new[] { new Input { Mouse = new MouseInput { X = (int)x, Y = (int)y, Flags = flags } } };
        if (SendInput(1, inputs, Marshal.SizeOf<Input>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
}
