using System.Buffers.Binary;

namespace CubeRadar.Core.Protocol;

public enum PlayerFaction { Unknown, Elyos, Asmodian }

/// <summary>A named player. <see cref="PcId"/> is the raw u32 after the name (0 when cut off). <see cref="Faction"/> stays
/// <see cref="PlayerFaction.Unknown"/> unless pcId, race and gender agree; <see cref="RankCode"/> is null unless the
/// observed tail matches exactly, and <see cref="RankName"/> is only set for the two codes seen in-game.</summary>
public readonly record struct PlayerIdentity(uint EntityId, string Name, uint PcId, PlayerFaction Faction, uint? RankCode,
    string? RankName);

/// <summary>
/// Passive decoders for player records (Global, docs/FINDINGS.md O6). The name prefix is verified on real records
/// (fixtures/players); everything after it is checked so strictly that a mismatch degrades to "unknown", never to a guess:
/// <code>
/// identity  id varint, mask1 u32, mask2 u8 (bit 0 set), name length u8 (1..36), name UTF-8, then
///   other   pcId u32, race u8, gender u8, …, [8 × 00, 21 f4 cb 1f, u32, rank u32, u32, 5–6 × 00]
///   self    server u16, pcId u32, race u8, …
/// position  id varint, absolute: 2 bytes | else: flags u8, [u8 if flags &amp; 1]; then x y z f32
/// removed   id varint, ≥ 2 bytes (meaning unknown)
/// </code>
/// The faction is never derived from the entity id or a server band; level and all other fields are not decoded.
/// </summary>
public static class PlayerMessages
{
    /// <summary>u32 533460001. The variants <c>24 f4 cb 1f</c> / <c>26 f4 cb 1f</c> occur too, but what follows them is
    /// unknown, so they are not anchors.</summary>
    private static ReadOnlySpan<byte> RankAnchor => [0x21, 0xF4, 0xCB, 0x1F];

    /// <param name="isSelf">Own record layout (server id before pcId) instead of the other-player one.</param>
    public static bool TryParseIdentity(ReadOnlySpan<byte> body, bool isSelf, out PlayerIdentity identity)
    {
        identity = default;
        var o = 0;
        if (!TryId(body, ref o, out var id) || o + 6 > body.Length) return false;
        o += 4; // mask1
        if ((body[o++] & 1) == 0) return false;
        int n = body[o++];
        if (n is < 1 or > 36 || o + n > body.Length || !Wire.TryName(body.Slice(o, n), out var name)) return false;
        o += n;

        uint pcId = 0;
        var faction = PlayerFaction.Unknown;
        uint? rank = null;
        if (isSelf)
        {
            // The server id only has to be plausible; its band is not used to tell the faction. No rank for the own player.
            if (Wire.TryU16(body, ref o, out var server) && Wire.TryU32(body, ref o, out pcId) &&
                Wire.TryU8(body, ref o, out var race) && server is >= 1000 and <= 2999)
                faction = Faction(pcId, race, gender: null);
        }
        else if (Wire.TryU32(body, ref o, out pcId) && Wire.TryU8(body, ref o, out var race) &&
                 Wire.TryU8(body, ref o, out var gender))
        {
            faction = Faction(pcId, race, gender);
            rank = RankTail(body[o..]);
        }
        identity = new PlayerIdentity(id, name, pcId, faction, rank, RankName(rank));
        return true;
    }

    /// <param name="absolute">2 bytes before x y z; otherwise a flags byte, plus one more byte when bit 0 is set.</param>
    public static bool TryParsePosition(ReadOnlySpan<byte> body, bool absolute, out PositionFix fix)
    {
        fix = default;
        var o = 0;
        if (!TryId(body, ref o, out var id)) return false;
        if (absolute) o += 2;
        else
        {
            if (!Wire.TryU8(body, ref o, out var flags)) return false;
            if ((flags & 1) != 0) o++;
        }
        if (o + 12 > body.Length) return false;
        var x = BinaryPrimitives.ReadSingleLittleEndian(body[o..]);
        var y = BinaryPrimitives.ReadSingleLittleEndian(body[(o + 4)..]);
        var z = BinaryPrimitives.ReadSingleLittleEndian(body[(o + 8)..]);
        if (!PositionMessages.IsWorldPosition(x, y, z)) return false;
        fix = new PositionFix(id, x, y, z, PositionKind.OtherPlayer);
        return true;
    }

    /// <summary>The bytes after the id are required but not interpreted.</summary>
    public static bool TryParseRemoved(ReadOnlySpan<byte> body, out uint entityId)
    {
        entityId = 0;
        var o = 0;
        if (!TryId(body, ref o, out var id) || o + 2 > body.Length) return false;
        entityId = id;
        return true;
    }

    /// <summary>A non-zero id. <c>Wire.TryVarint</c> drops the bits a 5th byte carries past 32; such an id is rejected
    /// here instead of wrapping.</summary>
    private static bool TryId(ReadOnlySpan<byte> b, ref int o, out uint id)
    {
        var start = o;
        if (Wire.TryVarint(b, ref o, out id) && id != 0 && (o - start < 5 || b[start + 4] <= 0x0F)) return true;
        id = 0;
        return false;
    }

    /// <summary>pcId 5..36 carries race and gender: race = (pcId − 5) % 4 / 2 + 1, gender = (pcId − 5) % 2 + 1. Race 1 is
    /// Elyos (the own record of a character confirmed Elyos in-game has 1), 2 Asmodian. The own record is not checked for
    /// a gender (<paramref name="gender"/> null).</summary>
    private static PlayerFaction Faction(uint pcId, byte race, byte? gender)
    {
        if (pcId is < 5 or > 36) return PlayerFaction.Unknown;
        var k = pcId - 5;
        if (race != k % 4 / 2 + 1 || (gender is { } g && g != k % 2 + 1)) return PlayerFaction.Unknown;
        return race == 1 ? PlayerFaction.Elyos : PlayerFaction.Asmodian;
    }

    /// <summary>
    /// Observed on Global for other players: 8 × 00, the anchor, u32 (unknown), rank u32, u32 (unknown), then nothing but
    /// zeros, the anchor 21 or 22 bytes before the end. <paramref name="tail"/> starts after the parsed prefix, so the zero
    /// run cannot borrow prefix bytes. An anchor that occurs more than once is ambiguous; codes above 255 are not accepted.
    /// </summary>
    private static uint? RankTail(ReadOnlySpan<byte> tail)
    {
        var at = tail.IndexOf(RankAnchor);
        if (at < 8 || tail[(at + 1)..].IndexOf(RankAnchor) >= 0 || tail.Length - at is not (21 or 22)) return null;
        // Some real records have a small control byte (0..3) between the zero run and the anchor, e.g. AllyA=2.
        var zeroRun = !tail[(at - 8)..at].ContainsAnyExcept((byte)0);
        if (!zeroRun && at >= 9 && tail[at - 1] <= 3)
            zeroRun = !tail[(at - 9)..(at - 1)].ContainsAnyExcept((byte)0);
        if (!zeroRun || tail[(at + 16)..].ContainsAnyExcept((byte)0)) return null;
        var code = Wire.U32(tail, at + 8);
        return code <= 255 ? code : null;
    }

    /// <summary>
    /// NOT a rank table. These are observations for two codes only, each matched to the rank shown in-game on a
    /// screenshot; every other code keeps its number without a name until it is corroborated the same way.
    /// </summary>
    private static string? RankName(uint? code) => code switch
    {
        19 => "Trainee",
        17 => "Eighth-Class Soldier",
        _ => null,
    };
}
