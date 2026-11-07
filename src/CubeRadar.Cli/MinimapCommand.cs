using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using CubeRadar.Core.Minimap;
using CubeRadar.Core.Radar;

namespace CubeRadar.Cli;

/// <summary>
/// <c>cuberadar minimap</c>: checks the camera-direction reading on screenshots without the game (e.g. after a patch moved
/// the minimap or changed the cone), finds the minimap in a full screenshot, or renders sample arrows.
/// </summary>
internal static class MinimapCommand
{
    public static int Run(string[] a)
    {
        string? Opt(string name) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        if (Opt("--arrow-png") is { } arrowPng)
        {
            ArrowRenderer.SavePng(arrowPng,
            [
                new ArrowFrame(0, 87, null, nameof(CubeStatus.Visible), null, false),
                new ArrowFrame(60, 42, 14, nameof(CubeStatus.Visible), null, false),
                new ArrowFrame(135, 120, -9, nameof(CubeStatus.OutOfView), "außer Sicht (letzte Position)", false),
                new ArrowFrame(270, 64, null, nameof(CubeStatus.Visible), "Position 25 s alt", true),
                new ArrowFrame(null, 64, null, nameof(CubeStatus.Visible), "Minimap nicht erkannt", false),
                new ArrowFrame(20, 2.5, null, nameof(CubeStatus.Opening), "wird geöffnet", false),
            ]);
            Console.WriteLine($"arrow samples: {arrowPng}");
            return 0;
        }

        var target = a.FirstOrDefault(x => !x.StartsWith("--") && (File.Exists(x) || Directory.Exists(x)));
        if (target is null)
        {
            Console.Error.WriteLine("""
                cuberadar minimap <screenshot.png|folder> [--center X,Y --radius R]
                    camera direction from the minimap's view cone. Without --center the image is taken as a full game
                    screenshot and the minimap is expected where AION 2 puts it by default.
                cuberadar minimap <screenshot.png> --find      search the top-right area for the minimap (like "Minimap suchen")
                cuberadar minimap --arrow-png FILE             sample arrows as PNG (layout check)
                """);
            return 2;
        }
        var files = Directory.Exists(target) ? Directory.GetFiles(target, "*.png").Order().ToArray() : [target];

        if (a.Contains("--find"))
        {
            var image = Load(files[0]);
            double h = image.Height;
            var w = (int)(0.34 * h);
            var area = Crop(image, image.Width - w, 0, w, (int)(0.36 * h));
            var found = ViewConeDetector.FindPlacement([area], MinimapHeading.DefaultRadius * h);
            if (found is null)
            {
                Console.WriteLine("no minimap found");
                return 1;
            }
            var cx = image.Width - w + found.Cx;
            Console.WriteLine($"minimap centre {cx:0}/{found.Cy:0}, radius {found.Radius:0} px (score {found.Score:0}); " +
                              $"as settings: MinimapRight {(image.Width - cx) / h:0.0000}, MinimapTop {found.Cy / h:0.0000}, MinimapRadius {found.Radius / h:0.0000}");
            return 0;
        }

        double? centreX = null, centreY = null, radius = null;
        if (Opt("--center") is { } c)
        {
            var parts = c.Split(',');
            centreX = double.Parse(parts[0], CultureInfo.InvariantCulture);
            centreY = double.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        if (Opt("--radius") is { } r) radius = double.Parse(r, CultureInfo.InvariantCulture);
        int reliable = 0;
        foreach (var file in files)
        {
            var image = Load(file);
            double h = image.Height;
            var cx = centreX ?? image.Width - MinimapHeading.DefaultRight * h;
            var cy = centreY ?? MinimapHeading.DefaultTop * h;
            var cone = ViewConeDetector.Detect(image, cx, cy, radius ?? MinimapHeading.DefaultRadius * h);
            if (cone is { Reliable: true }) reliable++;
            Console.WriteLine(cone is null
                ? $"{Path.GetFileName(file)}  no cone"
                : $"{Path.GetFileName(file)}  {(cone.Reliable ? "yaw" : "(weak)")} {cone.YawDeg,5:0.0}° {Orientation.Compass(cone.YawDeg),-2}  " +
                  $"edges {cone.EdgeA:0}°/{cone.EdgeB:0}°  width {cone.WidthDeg:0}°  score {cone.Score:0.0}");
        }
        if (files.Length > 1) Console.WriteLine($"{reliable} of {files.Length} frames with a reliable cone");
        return 0;
    }

    private static LumaImage Load(string path)
    {
        using var source = new Bitmap(path);
        using var bmp = source.Clone(new Rectangle(Point.Empty, source.Size), PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            return LumaImage.FromBgra(pixels, data.Width, data.Height, data.Stride);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static LumaImage Crop(LumaImage image, int x, int y, int w, int h)
    {
        var crop = new LumaImage(w, h);
        for (var row = 0; row < h; row++) Array.Copy(image.Data, (y + row) * image.Width + x, crop.Data, row * w, w);
        return crop;
    }
}
