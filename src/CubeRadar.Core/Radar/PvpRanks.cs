namespace CubeRadar.Core.Radar;

/// <summary>19, 17, 16, 15 and 14 match the user's screenshots. Descending order is an explicit user-authorized assumption,
/// not a decoded full rank table. Do not invent officer titles for codes without visual confirmation.</summary>
public static class PvpRanks
{
    private static readonly string[] Soldiers = ["First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth"];

    public static bool IsVerified(uint? code) => code is 19 or 17 or 16 or 15 or 14;

    /// <summary>Single-character map badge: soldier class, not the wire rank code. Unknown/officer codes never masquerade
    /// as soldier classes. The full label and its verification state remain in the contact list.</summary>
    public static string Symbol(uint? code) => code switch
    {
        19 => "T",
        >= 10 and <= 18 => (code.Value - 9).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => "?",
    };

    public static string? Name(uint? code) => code switch
    {
        19 => "Trainee",
        >= 10 and <= 18 => Soldiers[(int)code.Value - 10] + "-Class Soldier",
        >= 1 and <= 9 => $"PvP-Rang {code}",
        _ => null,
    };
}
