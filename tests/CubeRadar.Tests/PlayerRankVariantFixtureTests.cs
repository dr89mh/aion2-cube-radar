using System.Text.Json;
using CubeRadar.Core.Protocol;
using CubeRadar.Core.Radar;

namespace CubeRadar.Tests;

public class PlayerRankVariantFixtureTests
{
    private sealed record Frame(long T, ushort Op, string Name, byte[] Body);

    private static List<Frame> Frames(string file = "2026-10-18_rank_variants.json")
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "players", file)));
        Assert.True(doc.RootElement.GetProperty("anonymized").GetBoolean());
        return doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => new Frame(
            f.GetProperty("t").GetInt64(), Convert.ToUInt16(f.GetProperty("op").GetString(), 16),
            f.GetProperty("name").GetString()!, Convert.FromHexString(f.GetProperty("hex").GetString()!))).ToList();
    }

    [Theory]
    [InlineData("EnemyC", 15, "Sixth-Class Soldier", 0x26, true)]
    [InlineData("EnemyEcho", 15, "Sixth-Class Soldier", 0x21, true)]
    [InlineData("FoeAA", 16, "Seventh-Class Soldier", 0x21, true)]
    [InlineData("EnemyF", 19, "Trainee", 0x21, true)]
    [InlineData("EnemyG", 14, "Fifth-Class Soldier", 0x26, true)]
    [InlineData("EnemyH", 15, "Sixth-Class Soldier", 0x26, true)]
    [InlineData("EnemyFutureA", null, null, 0x24, false)]
    public void New_capture_fields_match_supported_layouts_only(string name, int? code, string? label, int tag, bool verified)
    {
        var identities = Frames().Where(f => f.Op == 0x4536 && f.Name == name).ToList();
        Assert.NotEmpty(identities);
        foreach (var f in identities)
        {
            var suffix = f.Body.AsSpan().LastIndexOf(new byte[] { 0xF4, 0xCB, 0x1F }) - 1;
            Assert.Equal(tag, f.Body[suffix]);
            Assert.True(PlayerMessages.TryParseIdentity(f.Body, false, out var player));
            Assert.Equal(name, player.Name);
            Assert.Equal(PlayerFaction.Asmodian, player.Faction);
            Assert.Equal(code is { } c ? (uint?)c : null, player.RankCode);
            Assert.Equal(label, player.RankName);
            Assert.Equal(verified, PvpRanks.IsVerified(player.RankCode));
        }
    }

    [Fact]
    public void Same_screenshot_rank_survives_suffix_length_and_final_flag_changes()
    {
        var frames = Frames().Where(f => f.Op == 0x4536 && f.Name == "EnemyC").ToList();
        Assert.Equal(3, frames.Count);
        Assert.Contains(frames, f => f.Body[^1] == 1);
        Assert.Contains(frames, f => f.Body[^1] == 0);
        foreach (var f in frames)
        {
            Assert.True(PlayerMessages.TryParseIdentity(f.Body, false, out var player));
            Assert.Equal(15u, player.RankCode);
        }
    }

    [Fact]
    public void Snapshot_exposes_newly_observed_ranks_without_provisional_marker()
    {
        var engine = new RadarEngine(new RadarConfig());
        engine.SetOwnFaction("Elyos");
        foreach (var f in Frames().Where(f => f.Op == 0x4536).OrderBy(f => f.T))
        {
            engine.OnFrame(f.T, f.Op, f.Body);
            var player = Assert.Single(engine.Snapshot().Players, p => p.Name == f.Name);
            Assert.True(player.IsEnemy);
            if (f.Name is "EnemyC" or "EnemyEcho" or "EnemyH")
            {
                Assert.Equal("Sixth-Class Soldier", player.RankName);
                Assert.True(player.RankVerified);
            }
            if (f.Name == "FoeAA")
            {
                Assert.Equal("Seventh-Class Soldier", player.RankName);
                Assert.True(player.RankVerified);
            }
            if (f.Name == "EnemyG")
            {
                Assert.Equal("Fifth-Class Soldier", player.RankName);
                Assert.True(player.RankVerified); // code 14 corroborated later by another screenshot-labelled player
            }
            if (f.Name == "EnemyFutureA") Assert.Null(player.RankCode); // unsupported tag must not be guessed
        }
    }

    [Fact]
    public void Same_fifth_rank_player_changes_between_both_anchor_variants_without_changing_rank()
    {
        var frames = Frames("2026-10-18_fifth_rank.json").Where(f => f.Op == 0x4536 && f.Name == "EnemyJ").ToList();
        Assert.Equal(5, frames.Count);
        var tags = new HashSet<byte>();
        foreach (var f in frames)
        {
            var at = f.Body.AsSpan().LastIndexOf(new byte[] { 0xF4, 0xCB, 0x1F }) - 1;
            tags.Add(f.Body[at]);
            Assert.True(PlayerMessages.TryParseIdentity(f.Body, false, out var player));
            Assert.Equal(2062u, player.EntityId);
            Assert.Equal(14u, player.RankCode);
            Assert.Equal("Fifth-Class Soldier", player.RankName);
            Assert.True(PvpRanks.IsVerified(player.RankCode));
        }
        Assert.Equal(new byte[] { 0x21, 0x26 }, tags.Order().ToArray());
    }

    [Theory]
    [InlineData("FoeBB")]
    [InlineData("EnemyKKK")]
    public void Additional_seventh_rank_screenshots_match_code_16(string name)
    {
        var f = Assert.Single(Frames("2026-10-18_fifth_rank.json"), f => f.Op == 0x4536 && f.Name == name);
        Assert.True(PlayerMessages.TryParseIdentity(f.Body, false, out var player));
        Assert.Equal(16u, player.RankCode);
        Assert.Equal("Seventh-Class Soldier", player.RankName);
    }
}
