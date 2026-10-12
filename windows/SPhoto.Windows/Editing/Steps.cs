using System;

namespace SPhoto.Editing;

/// <summary>
/// Sliders move in whole steps, -10 to +10 (0 to 10 for one-sided
/// adjustments), so a drag lands on a value the user can find again. Each
/// step is a fixed amount of the core's value: ±10 is about half of what
/// the core can do, which covers everyday edits without the extremes that
/// only look broken.
/// </summary>
internal sealed record StepScale(int Min, int Max, double PerStep, string Unit = "")
{
    internal float ToValue(int step) => (float)(step * PerStep);

    /// The nearest step for a core value (values from older recipes may
    /// sit between steps; they show rounded and stay as they are until moved).
    internal int ToStep(double value) => (int)Math.Clamp(Math.Round(value / PerStep), Min, Max);

    internal string Format(int step)
    {
        if (step == 0) return "0";
        if (Unit == "EV") return (step * PerStep).ToString("+0.0;-0.0") + " EV";
        if (Unit == "°") return step.ToString("+0;-0") + "°";
        return step.ToString("+0;-0");
    }
}

internal static class Steps
{
    /// The scale for an adjustment of the core.
    internal static StepScale For(Adjustment a)
    {
        // Exposure in fixed 0.2 EV steps: photographers count stops.
        if (a.Key == "exposure") return new StepScale(-10, 10, 0.2, "EV");
        double perStep = 0.05 * (a.Max - a.Min) / (a.Min < 0 ? 2 : 1);
        return new StepScale(a.Min < 0 ? -10 : 0, 10, perStep);
    }

    internal static readonly StepScale Straighten = new(-10, 10, 1, "°");
    internal static StepScale Perspective => new(-10, 10, 0.05);
    internal static readonly StepScale FilterStrength = new(0, 10, 0.1);
}
