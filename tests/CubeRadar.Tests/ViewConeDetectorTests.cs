using CubeRadar.Core.Minimap;

namespace CubeRadar.Tests;

/// <summary>Synthetic minimaps: textured background, a faint wedge with two bright edges, distractors.</summary>
public class ViewConeDetectorTests
{
    private const int Size = 240;
    private const double Cx = 120, Cy = 118, Radius = 110;

    private static LumaImage Minimap(double yaw, double width = 110, int seed = 1, bool cone = true, bool distractors = true)
    {
        var rng = new Random(seed);
        var img = new LumaImage(Size, Size);
        // terrain: smooth blobs plus pixel noise
        var blobs = Enumerable.Range(0, 12).Select(_ => (X: rng.NextDouble() * Size, Y: rng.NextDouble() * Size, R: 15 + rng.NextDouble() * 35, L: 8 + rng.NextDouble() * 22)).ToList();
        double edgeA = yaw - width / 2, edgeB = yaw + width / 2;
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                double v = 18;
                foreach (var b in blobs)
                {
                    var d2 = (x - b.X) * (x - b.X) + (y - b.Y) * (y - b.Y);
                    v += b.L * Math.Exp(-d2 / (2 * b.R * b.R));
                }
                v += rng.NextDouble() * 10;
                double dx = x - Cx, dy = y - Cy;
                var r = Math.Sqrt(dx * dx + dy * dy);
                if (cone && r > 3)
                {
                    var a = ViewConeDetector.Normalize(Math.Atan2(dx, -dy) * 180 / Math.PI);
                    var inside = ViewConeDetector.Difference(a, yaw) <= width / 2;
                    if (inside) v += 12 * Math.Max(0, 1 - r / 140); // faint fill, fading outwards
                    foreach (var e in new[] { edgeA, edgeB })
                    {
                        var rad = e * Math.PI / 180;
                        // distance of the pixel from the edge ray
                        var along = dx * Math.Sin(rad) - dy * Math.Cos(rad);
                        var across = Math.Abs(dx * Math.Cos(rad) + dy * Math.Sin(rad));
                        if (along > 0 && across < 1.5) v = Math.Max(v, 150 - 30 * across);
                    }
                }
                img.Data[y * Size + x] = (float)Math.Min(255, v);
            }
        if (distractors)
        {
            // a bright non-radial border line and an icon
            for (var x = 10; x < Size - 10; x++)
            {
                var y = (int)(40 + x * 0.35);
                img.Data[y * Size + x] = 170;
                img.Data[(y + 1) * Size + x] = 170;
            }
            for (var y = 150; y < 166; y++)
                for (var x = 160; x < 176; x++)
                    img.Data[y * Size + x] = 230;
        }
        return img;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(62)]
    [InlineData(135)]
    [InlineData(212)]
    [InlineData(305)]
    [InlineData(359)]
    public void Finds_the_bisector_of_the_cone(double yaw)
    {
        var cone = ViewConeDetector.Detect(Minimap(yaw, seed: (int)yaw + 3), Cx, Cy, Radius);
        Assert.NotNull(cone);
        Assert.True(cone.Reliable, $"score {cone.Score:0.0}, width {cone.WidthDeg:0.0}");
        Assert.True(ViewConeDetector.Difference(cone.YawDeg, yaw) <= 1.5, $"yaw {cone.YawDeg:0.0} instead of {yaw}");
        Assert.InRange(cone.WidthDeg, 108, 112);
    }

    [Fact]
    public void A_centre_a_few_pixels_off_costs_little()
    {
        var img = Minimap(80, seed: 9);
        foreach (var (dx, dy) in new[] { (4, 0), (-4, 0), (0, 4), (3, -3) })
        {
            var cone = ViewConeDetector.Detect(img, Cx + dx, Cy + dy, Radius);
            Assert.NotNull(cone);
            Assert.True(cone.Reliable);
            Assert.True(ViewConeDetector.Difference(cone.YawDeg, 80) <= 4, $"offset {dx}/{dy}: yaw {cone.YawDeg:0.0}");
        }
    }

    [Fact]
    public void Without_a_cone_nothing_is_reliable()
    {
        for (var seed = 0; seed < 5; seed++)
        {
            var cone = ViewConeDetector.Detect(Minimap(0, seed: seed, cone: false), Cx, Cy, Radius);
            Assert.False(cone?.Reliable ?? false, $"seed {seed}: {cone}");
        }
    }

    [Fact]
    public void A_wedge_of_the_wrong_width_is_not_the_cone()
    {
        Assert.False(ViewConeDetector.Detect(Minimap(40, width: 70, seed: 4), Cx, Cy, Radius)?.Reliable ?? false);
        Assert.False(ViewConeDetector.Detect(Minimap(40, width: 160, seed: 4), Cx, Cy, Radius)?.Reliable ?? false);
    }

    [Fact]
    public void Placement_search_finds_the_minimap_centre_in_a_larger_screen_area()
    {
        // the minimap pasted into a bigger, darker area at an unknown offset; three frames with different camera directions
        const int ox = 70, oy = 25;
        var frames = new[] { 30.0, 150, 260 }.Select((yaw, i) =>
        {
            var map = Minimap(yaw, seed: 20 + i);
            var area = new LumaImage(400, 300);
            Array.Fill(area.Data, 18f);
            for (var y = 0; y < Size; y++)
                Array.Copy(map.Data, y * Size, area.Data, (y + oy) * 400 + ox, Size);
            return area;
        }).ToList();

        var placement = ViewConeDetector.FindPlacement(frames, radiusGuess: 105);
        Assert.NotNull(placement);
        // a few pixels off cost about a degree (A_centre_a_few_pixels_off_costs_little)
        Assert.InRange(placement.Cx, Cx + ox - 3, Cx + ox + 3);
        Assert.InRange(placement.Cy, Cy + oy - 3, Cy + oy + 3);
        Assert.Equal(3, placement.Frames);
    }
}
