namespace CubeRadar.Core.Minimap;

/// <summary>
/// Keeps the camera direction across frames where the cone is real but faint. On bright terrain (Verteron's fields)
/// the cone's edges score 4–12, below <see cref="ViewConeDetector.MinScore"/>, while screens without a minimap stay
/// below 2 almost always (up to 8.4 seen once). A weak reading is therefore taken only when it continues a track
/// (close to the last accepted direction, which is at most <see cref="HoldMs"/> old) or when several strong-ish weak
/// readings in a row agree closely with each other (<see cref="AnchorFrames"/>, above everything seen without a minimap).
/// </summary>
public sealed class ViewConeTracker
{
    /// <summary>Lowest score a weak reading may have.</summary>
    public const double WeakMinScore = 4;
    /// <summary>A weak reading may differ this much from the last accepted direction (one frame is about 80 ms).</summary>
    public const double MaxStepDeg = 30;
    /// <summary>A track ends when nothing was accepted for this long.</summary>
    public const long HoldMs = 1500;
    /// <summary>Without a track, this many weak readings of at least <see cref="AnchorMinScore"/> …</summary>
    public const int AnchorFrames = 4;
    public const double AnchorMinScore = 9;
    /// <summary>… within <see cref="AnchorWindowMs"/> that lie within this spread start a new track.</summary>
    public const double AnchorSpreadDeg = 6;
    public const long AnchorWindowMs = 1000;

    public readonly record struct Result(double YawDeg, double Score, bool Weak);

    private readonly List<(long T, double Yaw)> _candidates = [];
    private double _yaw;
    private long _at;
    private bool _tracking;

    /// <summary>Feeds one frame's detection (null if there was none); returns the accepted direction or null.</summary>
    public Result? Update(ViewCone? cone, long nowMs)
    {
        if (_tracking && nowMs - _at > HoldMs) _tracking = false;
        if (cone is null || cone.WidthDeg is < ViewConeDetector.MinWidth or > ViewConeDetector.MaxWidth)
        {
            _candidates.Clear();
            return null;
        }
        if (cone.Reliable) return Accept(cone, nowMs, weak: false);
        if (cone.Score < WeakMinScore)
        {
            _candidates.Clear();
            return null;
        }
        if (_tracking && Math.Abs(Diff(cone.YawDeg, _yaw)) <= MaxStepDeg) return Accept(cone, nowMs, weak: true);
        if (cone.Score < AnchorMinScore)
        {
            _candidates.Clear();
            return null;
        }
        _candidates.RemoveAll(c => nowMs - c.T > AnchorWindowMs);
        _candidates.Add((nowMs, cone.YawDeg));
        if (_candidates.Count >= AnchorFrames && _candidates.All(c => Math.Abs(Diff(c.Yaw, cone.YawDeg)) <= AnchorSpreadDeg))
            return Accept(cone, nowMs, weak: true);
        return null;
    }

    /// <summary>Forgets the track, e.g. when the game window or the minimap placement changed.</summary>
    public void Reset()
    {
        _tracking = false;
        _candidates.Clear();
    }

    private Result Accept(ViewCone cone, long nowMs, bool weak)
    {
        _yaw = cone.YawDeg;
        _at = nowMs;
        _tracking = true;
        _candidates.Clear();
        return new Result(cone.YawDeg, cone.Score, weak);
    }

    private static double Diff(double a, double b) => ((a - b) % 360 + 540) % 360 - 180;
}
