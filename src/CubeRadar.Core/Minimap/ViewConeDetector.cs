namespace CubeRadar.Core.Minimap;

/// <summary>A greyscale image (luma 0–255), row-major. Filled from a screen capture by the caller.</summary>
public sealed class LumaImage
{
    public LumaImage(int width, int height)
    {
        Width = width;
        Height = height;
        Data = new float[width * height];
    }

    public int Width { get; private set; }
    public int Height { get; private set; }
    public float[] Data { get; private set; }

    /// <summary>Converts 32-bit BGRA pixels (as GDI+ locks a Format32bppArgb bitmap) into this image, resizing it if needed.</summary>
    public void LoadBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        if (width * height != Data.Length) Data = new float[width * height];
        Width = width;
        Height = height;
        for (var y = 0; y < height; y++)
        {
            var row = pixels.Slice(y * stride, width * 4);
            var o = y * width;
            for (var x = 0; x < width; x++)
                Data[o + x] = 0.114f * row[x * 4] + 0.587f * row[x * 4 + 1] + 0.299f * row[x * 4 + 2];
        }
    }

    public static LumaImage FromBgra(ReadOnlySpan<byte> pixels, int width, int height, int stride)
    {
        var image = new LumaImage(width, height);
        image.LoadBgra(pixels, width, height, stride);
        return image;
    }

    public LumaImage Clone()
    {
        var copy = new LumaImage(Width, Height);
        Array.Copy(Data, copy.Data, Data.Length);
        return copy;
    }

    /// <summary>Bilinear sample; NaN outside the image.</summary>
    public float Sample(double x, double y)
    {
        if (x < 0 || y < 0 || x >= Width - 1 || y >= Height - 1) return float.NaN;
        var x0 = (int)x;
        var y0 = (int)y;
        var fx = (float)(x - x0);
        var fy = (float)(y - y0);
        var i = y0 * Width + x0;
        var top = Data[i] + (Data[i + 1] - Data[i]) * fx;
        var bottom = Data[i + Width] + (Data[i + Width + 1] - Data[i + Width]) * fx;
        return top + (bottom - top) * fy;
    }
}

/// <summary>
/// Camera direction read off the view cone. Angles are compass degrees on the minimap: 0 = up (north on AION 2's north-up
/// minimap), 90 = right (east). <see cref="Score"/> is the ridge contrast of the weaker edge (luma units).
/// </summary>
public sealed record ViewCone(double YawDeg, double EdgeA, double EdgeB, double WidthDeg, double Score)
{
    public bool Reliable => Score >= ViewConeDetector.MinScore && WidthDeg is >= ViewConeDetector.MinWidth and <= ViewConeDetector.MaxWidth;
}

/// <summary>
/// Finds the camera's view cone on AION 2's minimap (docs/FINDINGS.md, M1). The minimap is north-up with the player in its
/// centre; the cone is a faint wedge of about 110° whose two edges are bright radial lines from the player. Map
/// features rarely run radially through the centre over most of the radius, and never as a pair at that angle, so the
/// detector looks for two such ridges 100–122° apart and takes their bisector. Only pixels are read.
/// </summary>
public static class ViewConeDetector
{
    /// <summary>Measured on 80 frames (2026-11-07): 108–114°.</summary>
    public const double MinWidth = 100, MaxWidth = 122;
    /// <summary>Real cones scored 11–77; screens without a minimap (skill window, loading screen) up to 8.4.</summary>
    public const double MinScore = 12;

    /// <summary>The cone around (cx, cy); null if there is no candidate at all. Check <see cref="ViewCone.Reliable"/>.</summary>
    /// <param name="radius">Radius of the visible map around the player in pixels; edges are read from 45 % to 95 % of it.</param>
    public static ViewCone? Detect(LumaImage image, double cx, double cy, double radius, int angleStep = 1, double radialStep = 2)
    {
        var steps = 360 / angleStep;
        var profile = RidgeProfile(image, cx, cy, radius * 0.45, radius * 0.95, angleStep, radialStep);
        var smooth = new double[steps];
        for (var i = 0; i < steps; i++)
            smooth[i] = (profile[(i + steps - 1) % steps] + 2 * profile[i] + profile[(i + 1) % steps]) / 4;

        int bestA = -1, bestW = 0;
        var bestScore = 0.0;
        var minW = (int)Math.Ceiling(MinWidth / angleStep);
        var maxW = (int)Math.Floor(MaxWidth / angleStep);
        for (var a = 0; a < steps; a++)
        {
            if (smooth[a] <= bestScore) continue;
            for (var w = minW; w <= maxW; w++)
            {
                var score = Math.Min(smooth[a], smooth[(a + w) % steps]);
                if (score <= bestScore) continue;
                bestScore = score;
                bestA = a;
                bestW = w;
            }
        }
        if (bestA < 0) return null;
        var edgeA = Refine(smooth, bestA) * angleStep;
        var edgeB = Refine(smooth, (bestA + bestW) % steps) * angleStep;
        var width = Normalize(edgeB - edgeA);
        return new ViewCone(Normalize(edgeA + width / 2), Normalize(edgeA), Normalize(edgeB), width, bestScore);
    }

    /// <summary>
    /// Ridge strength per angle: luma on the ray minus the mean of two parallel rays 4 px to either side, taken at many
    /// radii; the 35th percentile over the radii, so most of the ray has to be a bright line (a single icon does not count).
    /// </summary>
    public static double[] RidgeProfile(LumaImage image, double cx, double cy, double rMin, double rMax, int angleStep = 1, double radialStep = 2)
    {
        const double side = 4;
        var steps = 360 / angleStep;
        var profile = new double[steps];
        var values = new List<double>(64);
        for (var i = 0; i < steps; i++)
        {
            values.Clear();
            var rad = i * angleStep * Math.PI / 180;
            double dx = Math.Sin(rad), dy = -Math.Cos(rad);
            double px = -dy * side, py = dx * side; // perpendicular offset
            for (var r = rMin; r <= rMax; r += radialStep)
            {
                double x = cx + dx * r, y = cy + dy * r;
                var c = image.Sample(x, y);
                var s1 = image.Sample(x + px, y + py);
                var s2 = image.Sample(x - px, y - py);
                if (float.IsNaN(c) || float.IsNaN(s1) || float.IsNaN(s2)) continue;
                values.Add(c - (s1 + s2) / 2);
            }
            if (values.Count < 4) continue;
            values.Sort();
            profile[i] = values[(int)(values.Count * 0.35)];
        }
        return profile;
    }

    /// <summary>Where the minimap is: the centre and radius at which the cone shows best over all frames.</summary>
    public sealed record Placement(double Cx, double Cy, double Radius, double Score, int Frames);

    /// <summary>
    /// Searches the frames (the same screen area, captured a moment apart) for the minimap centre: a coarse grid over the
    /// whole area, then a fine search around the best candidates, each scored by the summed cone strength. Null if no
    /// candidate shows a reliable cone in at least half of the frames.
    /// </summary>
    public static Placement? FindPlacement(IReadOnlyList<LumaImage> frames, double radiusGuess)
    {
        if (frames.Count == 0) return null;
        var image = frames[0];

        (double Total, int Hits) Score(double cx, double cy, double r, int angleStep, double radialStep)
        {
            var total = 0.0;
            var hits = 0;
            foreach (var f in frames)
            {
                if (Detect(f, cx, cy, r, angleStep, radialStep) is { Reliable: true } cone)
                {
                    total += Math.Min(cone.Score, 60);
                    hits++;
                }
            }
            return (total, hits);
        }

        // 1. coarse grid with the guessed radius (the edges are read between 45 and 95 % of it, so it need not be exact)
        var coarse = new List<(double Cx, double Cy, double Total)>();
        var step = Math.Max(3, radiusGuess / 22);
        var margin = radiusGuess * 0.95 + 5;
        for (var cy = margin; cy <= image.Height - margin; cy += step)
            for (var cx = margin; cx <= image.Width - margin; cx += step)
            {
                var (total, hits) = Score(cx, cy, radiusGuess, 2, 4);
                if (hits > 0) coarse.Add((cx, cy, total));
            }

        // 2. pixel steps around the best candidates
        Placement? best = null;
        var reach = (int)Math.Ceiling(step);
        foreach (var c in coarse.OrderByDescending(c => c.Total).Take(4))
        {
            double x0 = Math.Round(c.Cx), y0 = Math.Round(c.Cy);
            for (var dy = -reach; dy <= reach; dy++)
                for (var dx = -reach; dx <= reach; dx++)
                {
                    var (total, hits) = Score(x0 + dx, y0 + dy, radiusGuess, 1, 3);
                    if (hits * 2 < frames.Count || total <= (best?.Score ?? 0)) continue;
                    best = new Placement(x0 + dx, y0 + dy, radiusGuess, total, hits);
                }
        }
        if (best is null) return null;

        // 3. the radius (different UI scale) at that centre
        foreach (var r in new[] { radiusGuess * 0.8, radiusGuess * 0.9, radiusGuess * 1.1, radiusGuess * 1.2 })
        {
            if (best.Cx - r * 0.95 < 0 || best.Cy - r * 0.95 < 0 || best.Cx + r * 0.95 >= image.Width || best.Cy + r * 0.95 >= image.Height) continue;
            var (total, hits) = Score(best.Cx, best.Cy, r, 1, 3);
            if (hits >= best.Frames && total > best.Score * 1.05) best = best with { Radius = r, Score = total, Frames = hits };
        }
        return best;
    }

    public static double Normalize(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }

    /// <summary>Smallest angle between two compass directions, 0–180°.</summary>
    public static double Difference(double a, double b)
    {
        var d = Math.Abs(Normalize(a) - Normalize(b));
        return d > 180 ? 360 - d : d;
    }

    /// <summary>Sub-step peak position by a parabola through the neighbours.</summary>
    private static double Refine(double[] p, int i)
    {
        var n = p.Length;
        double l = p[(i + n - 1) % n], c = p[i], r = p[(i + 1) % n];
        var denominator = l - 2 * c + r;
        if (denominator >= 0) return i;
        return i + Math.Clamp(0.5 * (l - r) / denominator, -0.5, 0.5);
    }
}
