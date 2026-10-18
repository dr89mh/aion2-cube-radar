using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using CubeRadar.Core.Protocol;

namespace CubeRadar.Tests;

/// <summary>Player identity / position / removal records: the real name prefixes (fixtures/players) plus synthetic bodies
/// for every check, because faction and rank must stay unknown unless the bytes match exactly.</summary>
public class PlayerMessageTests
{
    private static readonly byte[] Anchor = [0x21, 0xF4, 0xCB, 0x1F];

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static byte[] Xyz(float x, float y, float z)
    {
        var b = new byte[12];
        BinaryPrimitives.WriteSingleLittleEndian(b, x);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(4), y);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8), z);
        return b;
    }

    /// <summary>id, mask1 (as in the fixture), mask2, name length u8, name bytes.</summary>
    private static byte[] Prefix(byte[] id, byte[] name, byte mask2 = 0x07) =>
        [.. id, 0x07, 0x20, 0xA0, 0x01, mask2, (byte)name.Length, .. name];

    private static byte[] Prefix(uint id, string name, byte mask2 = 0x07) =>
        Prefix(Synth.Varint(id), Encoding.UTF8.GetBytes(name), mask2);

    private static byte[] Other(uint pcId, byte race, byte gender, params byte[] tail) =>
        [.. Prefix(70_000, "Xxxxx"), .. U32(pcId), race, gender, .. tail];

    private static byte[] Self(ushort server, uint pcId, byte race, params byte[] tail) =>
        [.. Prefix(4242, "Yyyyy"), .. U16(server), .. U32(pcId), race, .. tail];

    /// <summary>Filler, <paramref name="gap"/> × 00, anchor, u32, rank u32, u32, <paramref name="zeros"/> × 00. With the
    /// defaults exactly the observed shape, the anchor 21 bytes before the end. The filler bytes are odd, never 00.</summary>
    private static byte[] RankTail(uint rank, int zeros = 5, int gap = 8, byte[]? anchor = null) =>
        [.. Synth.Body(12, 3), .. new byte[gap], .. (anchor ?? Anchor), .. U32(0xDEAD), .. U32(rank), .. U32(0x0102),
            .. new byte[zeros]];

    private static PlayerIdentity Parse(byte[] body, bool isSelf)
    {
        Assert.True(PlayerMessages.TryParseIdentity(body, isSelf, out var p), Wire.Hex(body));
        return p;
    }

    [Fact]
    public void Real_prefixes_match_the_fixture()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "players", "2026-10-15_global_identity_prefixes.json")));
        var ops = new List<string>();
        foreach (var f in doc.RootElement.GetProperty("frames").EnumerateArray())
        {
            var op = f.GetProperty("op").GetString()!;
            ops.Add(op);
            var isSelf = op == "3336"; // else 4536, another player
            var body = Convert.FromHexString(f.GetProperty("hex").GetString()!);
            var end = f.GetProperty("nameEndOffset").GetInt32();

            var p = Parse(body, isSelf);
            Assert.Equal(f.GetProperty("entityId").GetUInt32(), p.EntityId);
            Assert.Equal(f.GetProperty("name").GetString(), p.Name);
            // Own character confirmed Elyos in-game; the other one's race and gender agree with its pcId.
            Assert.Equal(PlayerFaction.Elyos, p.Faction);
            Assert.InRange(p.PcId, 5u, 36u);
            Assert.Null(p.RankCode); // 80-byte prefixes, no tail
            Assert.Null(p.RankName);

            // The other layout never yields a faction from the same bytes.
            Assert.Equal(PlayerFaction.Unknown, Parse(body, !isSelf).Faction);

            // The name ends exactly at nameEndOffset; without the metadata nothing is trusted.
            Assert.False(PlayerMessages.TryParseIdentity(body.AsSpan(0, end - 1), isSelf, out _));
            var bare = Parse(body[..end], isSelf);
            Assert.Equal(p.Name, bare.Name);
            Assert.Equal(0u, bare.PcId);
            Assert.Equal(PlayerFaction.Unknown, bare.Faction);
        }
        Assert.Equal(new[] { "4536", "3336" }, ops);
    }

    [Theory]
    [InlineData(5, 1, 1, PlayerFaction.Elyos)]
    [InlineData(6, 1, 2, PlayerFaction.Elyos)]
    [InlineData(7, 2, 1, PlayerFaction.Asmodian)]
    [InlineData(8, 2, 2, PlayerFaction.Asmodian)]
    [InlineData(9, 1, 1, PlayerFaction.Elyos)]
    [InlineData(36, 2, 2, PlayerFaction.Asmodian)]
    public void Other_player_faction_from_consistent_pcid_race_and_gender(int pcId, int race, int gender,
        PlayerFaction expected)
    {
        var p = Parse(Other((uint)pcId, (byte)race, (byte)gender), false);
        Assert.Equal(70_000u, p.EntityId);
        Assert.Equal("Xxxxx", p.Name);
        Assert.Equal((uint)pcId, p.PcId);
        Assert.Equal(expected, p.Faction);
        Assert.Null(p.RankCode);
    }

    [Theory]
    [InlineData(5, 2, 1)]  // race disagrees with pcId
    [InlineData(7, 1, 1)]
    [InlineData(5, 1, 2)]  // gender disagrees with pcId
    [InlineData(8, 2, 1)]
    [InlineData(5, 1, 0)]  // gender outside 1/2
    [InlineData(6, 1, 3)]
    [InlineData(5, 0, 1)]  // race outside 1/2
    [InlineData(5, 3, 1)]
    [InlineData(4, 2, 2)]  // pcId below 5 (would match if pcId − 5 wrapped around)
    [InlineData(37, 1, 1)] // pcId above 36 (would match the formula)
    [InlineData(0, 0, 0)]
    [InlineData(1000, 1, 1)]
    public void Conflicting_metadata_keeps_the_player_but_not_the_faction(int pcId, int race, int gender)
    {
        var p = Parse(Other((uint)pcId, (byte)race, (byte)gender), false);
        Assert.Equal("Xxxxx", p.Name);
        Assert.Equal((uint)pcId, p.PcId);
        Assert.Equal(PlayerFaction.Unknown, p.Faction);
    }

    [Theory]
    [InlineData(1000, 5, 1, PlayerFaction.Elyos)]
    [InlineData(2999, 5, 1, PlayerFaction.Elyos)] // the server band does not decide the faction
    [InlineData(1500, 7, 2, PlayerFaction.Asmodian)]
    [InlineData(999, 5, 1, PlayerFaction.Unknown)]
    [InlineData(3000, 5, 1, PlayerFaction.Unknown)]
    [InlineData(0, 5, 1, PlayerFaction.Unknown)]
    [InlineData(1500, 5, 2, PlayerFaction.Unknown)]
    [InlineData(1500, 37, 1, PlayerFaction.Unknown)]
    public void Self_faction_needs_a_plausible_server_and_consistent_race(int server, int pcId, int race,
        PlayerFaction expected)
    {
        // The body ends right after race: the own record does not need a gender byte.
        var p = Parse(Self((ushort)server, (uint)pcId, (byte)race), true);
        Assert.Equal(4242u, p.EntityId);
        Assert.Equal("Yyyyy", p.Name);
        Assert.Equal((uint)pcId, p.PcId);
        Assert.Equal(expected, p.Faction);
    }

    [Fact]
    public void Truncated_metadata_keeps_the_name_but_no_faction()
    {
        var other = Other(5, 1, 1);
        Assert.Equal(PlayerFaction.Elyos, Parse(other, false).Faction);
        for (var cut = other.Length - 6; cut < other.Length; cut++)
        {
            var p = Parse(other[..cut], false);
            Assert.Equal("Xxxxx", p.Name);
            Assert.Equal(PlayerFaction.Unknown, p.Faction);
        }
        var self = Self(1500, 5, 1);
        Assert.Equal(PlayerFaction.Elyos, Parse(self, true).Faction);
        for (var cut = self.Length - 7; cut < self.Length; cut++)
        {
            var p = Parse(self[..cut], true);
            Assert.Equal("Yyyyy", p.Name);
            Assert.Equal(PlayerFaction.Unknown, p.Faction);
        }
    }

    [Fact]
    public void Broken_prefixes_are_rejected()
    {
        var id = Synth.Varint(70_000);
        byte[][] bad =
        [
            [],
            Prefix(0, "Xxxxx"),                                    // entity id 0
            Prefix(70_000, "Xxxxx", mask2: 0x06),                  // mask2 bit 0 clear
            Prefix(70_000, "Xxxxx", mask2: 0xFE),
            Prefix(70_000, "Xxxxx")[..^8],                         // cut inside mask1
            Prefix(70_000, "Xxxxx")[..^6],                         // no name length
            Prefix(70_000, "Xxxxx")[..^1],                         // name cut short
            Prefix(id, []),                                        // empty name
            Prefix(id, Enumerable.Repeat((byte)'a', 37).ToArray()), // 37 bytes
            Prefix(id, [0x41, 0xC3, 0x28]),                        // invalid UTF-8
            Prefix(id, [0x41, 0xC3]),                              // UTF-8 sequence cut off
            Prefix(id, [0x41, 0xED, 0xA0, 0x80]),                  // encoded surrogate
            Prefix(70_000, "Xx xx"),                               // not only letters and digits
            Prefix(70_000, "Xx-x"),
            Prefix(70_000, "12345"),                               // no letter
            Prefix(70_000, "X" + new string('x', 16)),             // 17 characters
        ];
        foreach (var b in bad)
        {
            Assert.False(PlayerMessages.TryParseIdentity(b, false, out var p), Wire.Hex(b));
            Assert.Equal(default, p);
            Assert.False(PlayerMessages.TryParseIdentity(b, true, out _), Wire.Hex(b));
        }
    }

    [Fact]
    public void Name_length_counts_utf8_bytes()
    {
        var sixteen = "X" + new string('x', 15);
        Assert.Equal(sixteen, Parse(Prefix(70_000, sixteen), false).Name);
        Assert.Equal("Ölwen7", Parse(Prefix(70_000, "Ölwen7"), false).Name);
        var hangul = new string('가', 12); // 36 bytes
        Assert.Equal(hangul, Parse(Prefix(70_000, hangul), false).Name);
        Assert.False(PlayerMessages.TryParseIdentity(Prefix(70_000, hangul + '가'), false, out _)); // 39 bytes
    }

    private static (bool Identity, bool Position, bool Removed) ParseId(byte[] id) => (
        PlayerMessages.TryParseIdentity(Prefix(id, Encoding.UTF8.GetBytes("Xxxxx")), false, out _),
        PlayerMessages.TryParsePosition([.. id, 0, 0, .. Xyz(12_326f, -85_154f, 476f)], true, out _),
        PlayerMessages.TryParseRemoved([.. id, 0x01, 0x00], out _));

    [Fact]
    public void Entity_ids_must_be_positive_and_fit_a_uint()
    {
        byte[] max = [0xFF, 0xFF, 0xFF, 0xFF, 0x0F];
        Assert.Equal((true, true, true), ParseId(max));
        Assert.True(PlayerMessages.TryParseRemoved([.. max, 0x01, 0x00], out var removed));
        Assert.Equal(uint.MaxValue, removed);
        Assert.Equal(uint.MaxValue, Parse(Prefix(max, Encoding.UTF8.GetBytes("Xxxxx")), false).EntityId);

        Assert.Equal((false, false, false), ParseId([0xFF, 0xFF, 0xFF, 0xFF, 0x1F]));       // 2^33 − 1, would wrap to uint.MaxValue
        Assert.Equal((false, false, false), ParseId([0x80, 0x80, 0x80, 0x80, 0x10]));       // 2^32, would wrap to 0
        Assert.Equal((false, false, false), ParseId([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01])); // longer than 5 bytes
        Assert.Equal((false, false, false), ParseId([0x00]));
    }

    [Theory]
    [InlineData(19, 5, "Trainee")]
    [InlineData(19, 6, "Trainee")]
    [InlineData(17, 5, "Eighth-Class Soldier")]
    [InlineData(17, 6, "Eighth-Class Soldier")]
    public void Observed_rank_codes_are_named(int code, int zeros, string name)
    {
        var p = Parse(Other(5, 1, 1, RankTail((uint)code, zeros)), false);
        Assert.Equal((uint?)code, p.RankCode);
        Assert.Equal(name, p.RankName);
        Assert.Equal(PlayerFaction.Elyos, p.Faction);
        Assert.Equal("Xxxxx", p.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(20)]
    [InlineData(255)]
    public void Other_rank_codes_keep_the_number_but_no_name(int code)
    {
        var p = Parse(Other(5, 1, 1, RankTail((uint)code)), false);
        Assert.Equal((uint?)code, p.RankCode);
        Assert.Null(p.RankName);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void Small_control_byte_between_zero_run_and_rank_anchor_is_bounded(int control, bool accepted)
    {
        var tail = RankTail(18);
        var at = tail.AsSpan().IndexOf(Anchor);
        byte[] withControl = [.. tail[..at], (byte)control, .. tail[at..]];
        var p = Parse(Other(5, 1, 1, withControl), false);
        Assert.Equal(accepted ? 18u : (uint?)null, p.RankCode);
        Assert.Null(p.RankName);
    }

    [Theory]
    [InlineData(256u)]
    [InlineData(0x1300u)]
    [InlineData(533_460_001u)]
    [InlineData(uint.MaxValue)]
    public void Rank_codes_above_255_are_not_accepted(uint code)
    {
        var p = Parse(Other(5, 1, 1, RankTail(code)), false);
        Assert.Null(p.RankCode);
        Assert.Null(p.RankName);
    }

    [Fact]
    public void Self_rank_stays_unknown()
    {
        var p = Parse(Self(1500, 5, 1, RankTail(19)), true);
        Assert.Null(p.RankCode);
        Assert.Null(p.RankName);
        Assert.Equal(PlayerFaction.Elyos, p.Faction);
    }

    public static TheoryData<string, byte[]> FalseTails => new()
    {
        { "no tail", [] },
        { "anchor 20 bytes before the end", RankTail(19, zeros: 4) },
        { "anchor 23 bytes before the end", RankTail(19, zeros: 7) },
        { "cut by one byte", RankTail(19)[..^1] },
        { "cut inside the fields", RankTail(19)[..^8] },
        { "only 7 zero bytes before the anchor", RankTail(19, gap: 7) },
        { "non-zero last byte", [.. RankTail(19, zeros: 4), 0x01] },
        { "non-zero byte right after the fields", [.. RankTail(19, zeros: 0), 0x01, 0, 0, 0, 0] },
        { "variant 24 f4 cb 1f", RankTail(19, anchor: [0x24, 0xF4, 0xCB, 0x1F]) },
        { "variant 26 f4 cb 1f", RankTail(19, anchor: [0x26, 0xF4, 0xCB, 0x1F]) },
        { "anchor also earlier in the body", [.. Synth.Body(10, 9), .. Anchor, .. RankTail(19)] },
        { "anchor right after the prefix", [.. Anchor, .. RankTail(19)] },
    };

    [Theory]
    [MemberData(nameof(FalseTails))]
    public void Rank_stays_unknown_unless_the_tail_matches_exactly(string why, byte[] tail)
    {
        var p = Parse(Other(5, 1, 1, tail), false);
        Assert.True(p.RankCode is null, why);
        Assert.Null(p.RankName);
        Assert.Equal(PlayerFaction.Elyos, p.Faction); // the prefix is unaffected
        Assert.Equal("Xxxxx", p.Name);
    }

    [Fact]
    public void Rank_tail_must_not_reach_into_the_prefix()
    {
        // pcId 0, race 0, gender 0 end the prefix with six zero bytes; two more give the anchor eight zeros before it.
        byte[] tail = [0, 0, .. Anchor, .. U32(0xDEAD), .. U32(19), .. U32(0x0102), 0, 0, 0, 0, 0];
        var p = Parse(Other(0, 0, 0, tail), false);
        Assert.Null(p.RankCode);
        Assert.Equal(PlayerFaction.Unknown, p.Faction);
        // With the whole zero run after the prefix the same tail is accepted.
        Assert.Equal((uint?)19, Parse(Other(0, 0, 0, [.. new byte[6], .. tail]), false).RankCode);
    }

    [Theory]
    [InlineData(true, new byte[] { 0x12, 0x34 })] // absolute: two skipped bytes
    [InlineData(false, new byte[] { 0x00 })]      // flags without bit 0
    [InlineData(false, new byte[] { 0x02 })]
    [InlineData(false, new byte[] { 0x01, 0x7F })] // flags bit 0: one extra byte
    [InlineData(false, new byte[] { 0x03, 0xFF })]
    public void Positions_read_xyz_after_the_header(bool absolute, byte[] header)
    {
        byte[] body = [.. Synth.Varint(70_000), .. header, .. Xyz(12_326.3f, -85_154.4f, 476f)];
        Assert.True(PlayerMessages.TryParsePosition(body, absolute, out var fix));
        Assert.Equal(new PositionFix(70_000, 12_326.3f, -85_154.4f, 476f, PositionKind.OtherPlayer), fix);
        Assert.False(PlayerMessages.TryParsePosition(body.AsSpan(0, body.Length - 1), absolute, out _));
        Assert.False(PlayerMessages.TryParsePosition(body.AsSpan(0, 3), absolute, out _));
    }

    [Theory]
    [InlineData(float.NaN, -85_154f, 476f)]
    [InlineData(12_326f, float.NaN, 476f)]
    [InlineData(12_326f, float.PositiveInfinity, 476f)]
    [InlineData(12_326f, -85_154f, float.NegativeInfinity)]
    [InlineData(500_000f, -85_154f, 476f)] // outside the map
    [InlineData(12_326f, -85_154f, 200_000f)]
    [InlineData(10f, -20f, 3f)]            // small vector near the origin
    public void Positions_must_be_finite_world_coordinates(float x, float y, float z)
    {
        Assert.False(PlayerMessages.TryParsePosition([.. Synth.Varint(70_000), 0, 0, .. Xyz(x, y, z)], true, out var fix));
        Assert.Equal(default, fix);
        Assert.False(PlayerMessages.TryParsePosition([.. Synth.Varint(70_000), 0, .. Xyz(x, y, z)], false, out _));
    }

    [Fact]
    public void Removal_needs_a_positive_id_and_two_more_bytes()
    {
        Assert.True(PlayerMessages.TryParseRemoved([.. Synth.Varint(70_000), 0x01, 0x00], out var id));
        Assert.Equal(70_000u, id);
        Assert.True(PlayerMessages.TryParseRemoved([.. Synth.Varint(70_000), 0x04, 0x03, 0x00], out id));
        Assert.Equal(70_000u, id);

        Assert.False(PlayerMessages.TryParseRemoved([.. Synth.Varint(70_000), 0x01], out id));
        Assert.Equal(0u, id);
        Assert.False(PlayerMessages.TryParseRemoved(Synth.Varint(70_000), out _));
        Assert.False(PlayerMessages.TryParseRemoved([0x00, 0x01, 0x00], out _));
        Assert.False(PlayerMessages.TryParseRemoved([], out _));
    }
}
