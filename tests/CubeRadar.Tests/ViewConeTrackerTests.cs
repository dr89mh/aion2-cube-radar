using CubeRadar.Core.Minimap;

namespace CubeRadar.Tests;

public class ViewConeTrackerTests
{
    private static ViewCone Cone(double yaw, double score, double width = 110) =>
        new(yaw, yaw - width / 2, yaw + width / 2, width, score);

    [Fact]
    public void StrongReadingsAreAlwaysTaken()
    {
        var t = new ViewConeTracker();
        var r = t.Update(Cone(200, 30), 0);
        Assert.NotNull(r);
        Assert.Equal(200, r.Value.YawDeg);
        Assert.False(r.Value.Weak);
    }

    [Fact]
    public void WeakReadingsContinueATrack()
    {
        var t = new ViewConeTracker();
        t.Update(Cone(200, 30), 0);
        var r = t.Update(Cone(215, 6), 80);
        Assert.NotNull(r);
        Assert.True(r.Value.Weak);
        // the track follows a turning camera through weak frames only
        for (var i = 2; i < 30; i++) Assert.NotNull(t.Update(Cone(215 + 10 * (i - 1), 6), i * 80));
    }

    [Fact]
    public void WeakReadingFarFromTheTrackIsRejected()
    {
        var t = new ViewConeTracker();
        t.Update(Cone(200, 30), 0);
        Assert.Null(t.Update(Cone(300, 8), 80));
    }

    [Fact]
    public void TrackEndsAfterHoldTime()
    {
        var t = new ViewConeTracker();
        t.Update(Cone(200, 30), 0);
        Assert.Null(t.Update(Cone(205, 6), ViewConeTracker.HoldMs + 100));
    }

    [Fact]
    public void TooWeakOrWrongWidthIsRejectedEvenOnATrack()
    {
        var t = new ViewConeTracker();
        t.Update(Cone(200, 30), 0);
        Assert.Null(t.Update(Cone(200, 2), 80));
        Assert.Null(t.Update(Cone(200, 30, width: 130), 160));
    }

    [Fact]
    public void ConsistentWeakReadingsStartATrack()
    {
        var t = new ViewConeTracker();
        for (var i = 0; i < ViewConeTracker.AnchorFrames - 1; i++) Assert.Null(t.Update(Cone(100 + i, 10), i * 80));
        var r = t.Update(Cone(102, 10), ViewConeTracker.AnchorFrames * 80);
        Assert.NotNull(r);
        Assert.True(r.Value.Weak);
        // and the new track then carries fainter readings too
        Assert.NotNull(t.Update(Cone(108, 5), (ViewConeTracker.AnchorFrames + 1) * 80));
    }

    [Fact]
    public void ScatteredOrFaintReadingsDoNotStartATrack()
    {
        var t = new ViewConeTracker();
        double[] yaws = [100, 140, 100, 160, 100, 120];
        for (var i = 0; i < yaws.Length; i++) Assert.Null(t.Update(Cone(yaws[i], 10), i * 80));
        var u = new ViewConeTracker();
        for (var i = 0; i < 10; i++) Assert.Null(u.Update(Cone(100, 7), i * 80)); // below the anchor score
    }

    [Fact]
    public void WrapsAroundNorth()
    {
        var t = new ViewConeTracker();
        t.Update(Cone(350, 30), 0);
        Assert.NotNull(t.Update(Cone(10, 6), 80));
    }
}
