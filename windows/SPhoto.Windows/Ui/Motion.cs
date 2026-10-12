using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace SPhoto.Ui;

/// <summary>
/// The app's few animations, all short and all off when Windows' "Show
/// animations" setting is off: fades for chrome and panels, a slide when
/// photos change, and a glide when a photo's space changes or it grows
/// from its thumbnail into the viewer. Sliders never animate.
/// </summary>
internal static class Motion
{
    private static readonly UISettings Settings = new();

    internal static readonly TimeSpan Short = TimeSpan.FromMilliseconds(167);
    internal static readonly TimeSpan Medium = TimeSpan.FromMilliseconds(250);

    /// Windows' "Show animations in Windows" setting.
    internal static bool Enabled
    {
        get
        {
            try
            {
                return Settings.AnimationsEnabled;
            }
            catch (Exception)
            {
                return true;
            }
        }
    }

    /// Fades an element in or out; hidden elements stop taking input.
    /// A storyboard rather than OpacityTransition: XAML refuses an
    /// OpacityTransition on an element whose RenderTransform is in use
    /// (the viewer's photo slides).
    internal static void Fade(UIElement e, bool visible, TimeSpan? duration = null, double? start = null, Action? done = null)
    {
        double to = visible ? 1 : 0;
        e.IsHitTestVisible = visible;
        // A newer fade replaces one still running, from where it got to.
        double from = start ?? e.Opacity;
        if (Running.TryGetValue(e, out var running))
        {
            running.Stop();
            Running.Remove(e);
        }
        e.Opacity = from;
        if (!Enabled || duration == TimeSpan.Zero)
        {
            e.Opacity = to;
            done?.Invoke();
            return;
        }
        var animation = new DoubleAnimation { From = from, To = to, Duration = duration ?? Short };
        Storyboard.SetTarget(animation, e);
        Storyboard.SetTargetProperty(animation, "Opacity");
        var story = new Storyboard();
        story.Children.Add(animation);
        // Hold the end value as the element's own once done.
        story.Completed += (_, _) =>
        {
            if (!Running.TryGetValue(e, out var current) || current != story) return;
            Running.Remove(e);
            e.Opacity = to;
            story.Stop();
            done?.Invoke();
        };
        Running.Add(e, story);
        story.Begin();
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<UIElement, Storyboard> Running = new();

    /// Shows or hides an element, fading it in on show.
    internal static void Show(UIElement e, bool visible)
    {
        if (!visible)
        {
            e.Visibility = Visibility.Collapsed;
            return;
        }
        if (e.Visibility == Visibility.Visible) return;
        e.Visibility = Visibility.Visible;
        Fade(e, true, start: 0);
    }

    /// The translation an element moves by, created on first use.
    internal static CompositeTransform Transform(UIElement e)
    {
        if (e.RenderTransform is CompositeTransform t) return t;
        t = new CompositeTransform();
        e.RenderTransform = t;
        return t;
    }

    /// Slides an element horizontally from its current offset to `to`.
    internal static void SlideX(UIElement e, double from, double to, TimeSpan? duration = null, Action? done = null)
    {
        var t = Transform(e);
        if (!Enabled)
        {
            t.TranslateX = to;
            done?.Invoke();
            return;
        }
        t.TranslateX = from;
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration ?? Medium,
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 },
        };
        Storyboard.SetTarget(animation, t);
        Storyboard.SetTargetProperty(animation, "TranslateX");
        var story = new Storyboard();
        story.Children.Add(animation);
        story.Completed += (_, _) =>
        {
            t.TranslateX = to;
            story.Stop();
            done?.Invoke();
        };
        story.Begin();
    }

    /// <summary>
    /// A side pane comes in from the right edge, fading in as it slides;
    /// going, it slides back out and then collapses. The content beside it
    /// makes room at once, so nothing under the pane moves twice.
    /// </summary>
    internal static void SlidePane(FrameworkElement pane, bool visible, Action? done = null)
    {
        double width = double.IsNaN(pane.Width) ? Math.Max(pane.ActualWidth, 320) : pane.Width;
        if (visible)
        {
            if (pane.Visibility == Visibility.Visible && Transform(pane).TranslateX == 0) { done?.Invoke(); return; }
            pane.Visibility = Visibility.Visible;
            Fade(pane, true, Medium, start: 0);
            SlideX(pane, width * 0.4, 0, done: done);
            return;
        }
        if (pane.Visibility != Visibility.Visible) { done?.Invoke(); return; }
        if (!Enabled)
        {
            pane.Visibility = Visibility.Collapsed;
            done?.Invoke();
            return;
        }
        Fade(pane, false, Short);
        SlideX(pane, 0, width * 0.4, Short, () =>
        {
            // Unless it was asked back in meanwhile.
            if (pane.Opacity < 0.99)
            {
                pane.Visibility = Visibility.Collapsed;
                Transform(pane).TranslateX = 0;
            }
            done?.Invoke();
        });
    }

    /// <summary>
    /// Brings an element home from where it appeared to be: its transform
    /// starts at the given scale and offset and eases back to none, so a
    /// photo whose space changed glides to its new place and size.
    /// </summary>
    internal static void Settle(UIElement e, double scale, double dx, double dy, TimeSpan? duration = null, Action? done = null)
    {
        var t = Transform(e);
        e.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        if (!Enabled || (Math.Abs(scale - 1) < 0.005 && Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5))
        {
            t.ScaleX = t.ScaleY = 1;
            t.TranslateX = t.TranslateY = 0;
            done?.Invoke();
            return;
        }
        // Layout has already given the photo its new size. The compositor
        // applies a storyboard's From on its next animation tick; keep the
        // base transform at the old rectangle until then, or one frame
        // flashes at full size before shrinking back to begin the glide.
        t.ScaleX = t.ScaleY = scale;
        t.TranslateX = dx;
        t.TranslateY = dy;
        var story = new Storyboard();
        foreach (var (property, from, to) in new[] { ("ScaleX", scale, 1.0), ("ScaleY", scale, 1.0), ("TranslateX", dx, 0.0), ("TranslateY", dy, 0.0) })
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = duration ?? Medium,
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 },
            };
            Storyboard.SetTarget(animation, t);
            Storyboard.SetTargetProperty(animation, property);
            story.Children.Add(animation);
        }
        // Hand the end values back to the transform, so later direct
        // changes (a swipe) are not overridden by a held animation.
        story.Completed += (_, _) =>
        {
            t.ScaleX = t.ScaleY = 1;
            t.TranslateX = t.TranslateY = 0;
            story.Stop();
            done?.Invoke();
        };
        story.Begin();
    }

    /// <summary>
    /// Rows coming into a list one after another: each fades in while it
    /// slides the last few pixels from dy, a little after the one before.
    /// The sidebar's groups (标签, 已归档, 文件夹) all open this way.
    /// </summary>
    internal static void Reveal(System.Collections.Generic.IReadOnlyList<UIElement> rows, double dy)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            var e = rows[i];
            var t = Transform(e);
            if (!Enabled)
            {
                e.Opacity = 1;
                t.TranslateY = 0;
                continue;
            }
            var delay = TimeSpan.FromMilliseconds(Math.Min(i, 8) * 18);
            var story = new Storyboard();
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = Short, BeginTime = delay };
            Storyboard.SetTarget(fade, e);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var slide = new DoubleAnimation
            {
                From = dy,
                To = 0,
                Duration = Medium,
                BeginTime = delay,
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 },
            };
            Storyboard.SetTarget(slide, t);
            Storyboard.SetTargetProperty(slide, "TranslateY");
            story.Children.Add(fade);
            story.Children.Add(slide);
            e.Opacity = 0;
            t.TranslateY = dy;
            story.Completed += (_, _) =>
            {
                story.Stop();
                e.Opacity = 1;
                t.TranslateY = 0;
            };
            story.Begin();
        }
    }
}
