using System;

namespace SPhoto.Ui;

internal static class Zoom
{
    /// <summary>
    /// ScrollViewer refuses some zoom limits (it threw on a minimum of
    /// 0.05); keep the maximum in a range it is known to take.
    /// </summary>
    internal static float SafeMax(double wanted) => (float)Math.Clamp(wanted, 2, 64);
}
