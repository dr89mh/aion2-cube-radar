using System.Text.Json;

namespace CubeRadar.Cli;

/// <summary>Per-user radar settings in <c>data/settings.json</c>: north axis, sound, overlay and update check.</summary>
internal sealed class RadarSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "+y" readable
    };
    private readonly object _gate = new();
    private string _path = "";

    public string? North { get; set; }
    /// <summary>Auto uses self identity; manual selection works when starting mid-session.</summary>
    public string OwnFaction { get; set; } = "Auto";
    public bool ShowEnemies { get; set; } = true;
    public int? OverlayX { get; set; }
    public int? OverlayY { get; set; }
    public bool OverlayVisible { get; set; } = true;
    public int OverlayRangeM { get; set; } = 120;
    /// <summary>Hide the overlay while the mouse cursor is visible (in-game menus / UI mode).</summary>
    public bool OverlayHideInMenus { get; set; } = true;
    /// <summary>Show the overlay only while AION 2 is the foreground window.</summary>
    public bool OverlayOnlyWhenGameFocused { get; set; } = true;
    /// <summary>Restart the overlay with admin rights, so Alt (cursor in the game) is not taken for a menu (see Elevation).</summary>
    public bool OverlayAdminRights { get; set; } = true;
    public int OverlayScalePct { get; set; } = 100;
    /// <summary>Opacity of the radar itself (header and map), 40–100 %.</summary>
    public int OverlayOpacityPct { get; set; } = 90;
    /// <summary>Opacity of the enemy name list below the radar, 40–100 %; null (settings from before the split) = like the radar.</summary>
    public int? OverlayListOpacityPct { get; set; }
    /// <summary>Sound when a new cube appears.</summary>
    public bool SoundEnabled { get; set; } = true;
    /// <summary>Optional .wav played instead of the beep.</summary>
    public string? SoundFile { get; set; }
    /// <summary>Volume of the sound, 5–100 %.</summary>
    public int SoundVolumePct { get; set; } = 80;
    /// <summary>Look for a newer version on GitHub at start and every 6 hours (single exe only, see Updater).</summary>
    public bool UpdateCheckEnabled { get; set; } = true;

    public static RadarSettings Load(string path)
    {
        RadarSettings s;
        try
        {
            s = File.Exists(path) ? JsonSerializer.Deserialize<RadarSettings>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            s = new();
        }
        s._path = path;
        return s;
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(this, Json));
            }
            catch (IOException)
            {
                // settings are a convenience; never let them stop the radar
            }
        }
    }
}
