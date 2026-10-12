using System;
using System.Collections.Generic;
using System.Linq;

namespace SPhoto.Library;

/// <summary>
/// One photo as the stacking rule sees it. Taken is the EXIF shooting date
/// only (a file date says nothing about bursts, so null is never stacked
/// automatically); DHash is null until the analyzer has looked at the photo.
/// </summary>
public sealed record StackInput(string Key, DateTime? Taken, string? Camera, ulong? DHash, float? Quality);

/// <summary>A stack: its cover and every member (cover included) in time order.</summary>
public sealed record StackGroup(string Cover, IReadOnlyList<string> Members);

/// <summary>What the user changed by hand; it always beats the rule.</summary>
public sealed class StackOverrides
{
    public static readonly StackOverrides None = new();

    /// Keys with the same manual group id form one stack regardless of the
    /// rule, and take no part in automatic runs.
    public IReadOnlyDictionary<string, string> ManualGroups { get; init; } = new Dictionary<string, string>();

    /// Keys that must stay on their own.
    public IReadOnlySet<string> KeepApart { get; init; } = new HashSet<string>();

    /// Covers chosen by hand. The value orders the choices: when a stack holds
    /// several, the highest (the latest) wins.
    public IReadOnlyDictionary<string, long> Covers { get; init; } = new Dictionary<string, long>();
}

/// <summary>
/// Groups bursts and near-identical shots into stacks. Deliberately
/// conservative (宁可不叠也不叠错): two shots that are not stacked cost a
/// glance, two different moments hidden under one cover cost a lost photo.
/// </summary>
public static class StackBuilder
{
    /// A burst: shots this close in time stack even with some movement.
    public static readonly TimeSpan BurstGap = TimeSpan.FromSeconds(2);

    /// Within a burst the frames may differ this many dHash bits (of 64);
    /// a hand-held burst of a moving subject stays well under it.
    public const int BurstDistance = 12;

    /// Retakes: shots up to this far apart stack only when nearly identical.
    public static readonly TimeSpan RetakeGap = TimeSpan.FromSeconds(30);

    /// How alike retakes must be: the same framing, a slightly different moment.
    public const int RetakeDistance = 6;

    /// Every member must stay this close to the first shot of its run, so a
    /// slow pan cannot chain a whole afternoon into one stack.
    public const int DriftDistance = 16;

    public static IReadOnlyList<StackGroup> Build(IReadOnlyList<StackInput> photos, StackOverrides? overrides = null)
    {
        overrides ??= StackOverrides.None;
        var groups = new List<List<StackInput>>(photos.Count);
        var auto = new List<StackInput>();
        var manual = new Dictionary<string, List<StackInput>>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in photos)
        {
            if (!seen.Add(p.Key)) continue;
            if (overrides.KeepApart.Contains(p.Key))
            {
                groups.Add(new List<StackInput> { p });
            }
            else if (overrides.ManualGroups.TryGetValue(p.Key, out var id))
            {
                if (!manual.TryGetValue(id, out var list)) manual[id] = list = new List<StackInput>();
                list.Add(p);
            }
            else if (p.Taken is not null && p.Camera is not null && p.DHash is not null)
            {
                auto.Add(p);
            }
            else
            {
                groups.Add(new List<StackInput> { p });
            }
        }
        groups.AddRange(manual.Values);

        foreach (var camera in auto.GroupBy(p => p.Camera!, StringComparer.Ordinal))
        {
            var sorted = camera.OrderBy(p => p.Taken).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
            var run = new List<StackInput> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                var p = sorted[i];
                if (Joins(run, p))
                {
                    run.Add(p);
                }
                else
                {
                    groups.Add(run);
                    run = new List<StackInput> { p };
                }
            }
            groups.Add(run);
        }

        var result = new List<StackGroup>(groups.Count);
        foreach (var members in groups)
        {
            var ordered = InTimeOrder(members);
            var keys = new string[ordered.Count];
            for (int i = 0; i < keys.Length; i++) keys[i] = ordered[i].Key;
            result.Add(new StackGroup(PickCover(ordered, overrides).Key, keys));
        }
        return result;
    }

    /// Whether p continues the run: close to the previous shot by one of the
    /// two rules, and still close to the first.
    private static bool Joins(List<StackInput> run, StackInput p)
    {
        var last = run[^1];
        var gap = p.Taken!.Value - last.Taken!.Value;
        int distance = DHash.Distance(last.DHash!.Value, p.DHash!.Value);
        bool near = (gap <= BurstGap && distance <= BurstDistance) || (gap <= RetakeGap && distance <= RetakeDistance);
        return near && DHash.Distance(run[0].DHash!.Value, p.DHash!.Value) <= DriftDistance;
    }

    /// Sorts in place: by EXIF date (undated last), then key.
    private static List<StackInput> InTimeOrder(List<StackInput> members)
    {
        if (members.Count > 1)
        {
            members.Sort((a, b) =>
            {
                if (a.Taken is null || b.Taken is null)
                {
                    if (a.Taken is not null) return -1;
                    if (b.Taken is not null) return 1;
                }
                else
                {
                    int c = a.Taken.Value.CompareTo(b.Taken.Value);
                    if (c != 0) return c;
                }
                return string.CompareOrdinal(a.Key, b.Key);
            });
        }
        return members;
    }

    /// <summary>
    /// The hand-picked cover when there is one, else the best quality, ties
    /// and unanalysed photos going to the earliest. members in time order.
    /// </summary>
    internal static StackInput PickCover(IReadOnlyList<StackInput> members, StackOverrides overrides)
    {
        StackInput? chosen = null;
        long rank = long.MinValue;
        foreach (var m in members)
        {
            if (overrides.Covers.TryGetValue(m.Key, out var r) && r > rank)
            {
                chosen = m;
                rank = r;
            }
        }
        if (chosen is not null) return chosen;
        var best = members[0];
        foreach (var m in members)
        {
            if ((m.Quality ?? float.NegativeInfinity) > (best.Quality ?? float.NegativeInfinity)) best = m;
        }
        return best;
    }
}
