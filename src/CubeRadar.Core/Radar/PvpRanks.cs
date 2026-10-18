namespace CubeRadar.Core.Radar;

/// <summary>19 and 17 match the user's screenshots. Descending order is an explicit user-authorized assumption,
/// not a decoded full rank table. Do not invent officer titles for codes without visual confirmation.</summary>
public static class PvpRanks
{
    private static readonly string[] Soldiers = ["First", "Second", "Third", "Fourth", "Fifth", "Sixth", "Seventh", "Eighth", "Ninth"];

    public static bool IsVerified(uint? code) => code is 19 or 17;

    public static string? Name(uint? code) => code switch
    {
        19 => "Trainee",
        >= 10 and <= 18 => Soldiers[(int)code.Value - 10] + "-Class Soldier",
        >= 1 and <= 9 => $"PvP-Rang {code}",
        _ => null,
    };
}
