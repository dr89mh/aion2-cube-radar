using System.Buffers.Binary;
using System.Text.Json;
using CubeRadar.Core.Protocol;
using CubeRadar.Core.Radar;

namespace CubeRadar.Tests;

public class PlayerRadarTests
{
    private sealed record Frame(long T, ushort Op, string Name, byte[] Body, string Source);

    private static List<Frame> Frames()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "fixtures", "players", "2026-10-18_abyss.json")));
        return doc.RootElement.GetProperty("frames").EnumerateArray().Select(f => new Frame(
            f.GetProperty("t").GetInt64(), Convert.ToUInt16(f.GetProperty("op").GetString(), 16),
            f.GetProperty("name").GetString()!, Convert.FromHexString(f.GetProperty("hex").GetString()!),
            f.GetProperty("source").GetString()!)).ToList();
    }

    private static Frame Identity(string name) => Frames().First(f => f.Op == 0x4536 && f.Name == name);
    private static void Feed(RadarEngine engine, Frame f) => engine.OnFrame(f.T, f.Op, f.Body);

    [Theory]
    [InlineData("EnemyAlphaA", 14332u, PlayerFaction.Asmodian, 19u, "Trainee")]
    [InlineData("EnemyB", 11930u, PlayerFaction.Asmodian, 17u, "Eighth-Class Soldier")]
    [InlineData("AllyA", 13372u, PlayerFaction.Elyos, 14u, null)]
    [InlineData("EnemyUnknown", 10456u, PlayerFaction.Asmodian, 15u, null)]
    public void Real_identity_fields_match_observations(string name, uint id, PlayerFaction faction, uint rank, string? label)
    {
        foreach (var f in Frames().Where(f => f.Op == 0x4536 && f.Name == name))
        {
            Assert.True(PlayerMessages.TryParseIdentity(f.Body, false, out var p));
            Assert.Equal((id, name, faction, (uint?)rank, label), (p.EntityId, p.Name, p.Faction, p.RankCode, p.RankName));
        }
    }

    [Fact]
    public void Marker_snapshot_contains_labelled_enemy_with_rank_and_fresh_position()
    {
        var engine = new RadarEngine(new RadarConfig());
        engine.SetOwnFaction("Elyos");
        var start = Identity("EnemyAlphaA").T - 1000;
        engine.OnFrame(start, 0x3336, [1]); // synthetic self ID; relevant wire fields below are retained in minimised fixtures
        var map = new byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(map.AsSpan(4), 999);
        BinaryPrimitives.WriteSingleLittleEndian(map.AsSpan(16), -42000);
        BinaryPrimitives.WriteSingleLittleEndian(map.AsSpan(20), -7800);
        BinaryPrimitives.WriteSingleLittleEndian(map.AsSpan(24), 31600);
        engine.OnFrame(start, 0x2136, map);
        foreach (var f in Frames().Where(f => f.T <= 1792347698264).OrderBy(f => f.T)) Feed(engine, f);
        engine.OnFrame(1792347698264, 0x0036, new byte[8]);
        var snapshot = engine.Snapshot();
        var enemy = Assert.Single(snapshot.Players, p => p.Name == "EnemyB");
        Assert.True(enemy.IsEnemy);
        Assert.Equal("Asmodian", enemy.Faction);
        Assert.Equal("Eighth-Class Soldier", enemy.RankName);
        Assert.True(enemy.RankVerified);
        Assert.False(enemy.PositionStale);
        Assert.NotNull(enemy.DistanceM);
        Assert.True(enemy.DistanceEstimated); // synthetic own fix is deliberately older than 5 seconds
        Assert.NotNull(enemy.Compass);
        Assert.DoesNotContain(snapshot.Players, p => p.Name == "EnemyAlphaA"); // 47 36 already left view
        Assert.Equal("MapLoad", snapshot.Self!.Source); // enemy movement must not replace the own fix
        Assert.Equal(-42000, snapshot.Self.X);
        Assert.Empty(snapshot.Cubes);
    }

    [Fact]
    public void Unknown_own_faction_never_classifies_players_as_enemies()
    {
        var engine = new RadarEngine(new RadarConfig());
        Feed(engine, Identity("EnemyB"));
        Assert.Null(Assert.Single(engine.Snapshot().Players).IsEnemy);
        engine.SetOwnFaction("elyos");
        Assert.True(Assert.Single(engine.Snapshot().Players).IsEnemy);
        engine.SetOwnFaction("Asmodian");
        Assert.False(Assert.Single(engine.Snapshot().Players).IsEnemy);
        engine.SetOwnFaction("Auto");
        Assert.Null(Assert.Single(engine.Snapshot().Players).IsEnemy);
        Assert.Throws<FormatException>(() => engine.SetOwnFaction("wrong"));
    }

    [Fact]
    public void Unseen_movement_does_not_create_contacts_and_stale_positions_are_flagged()
    {
        var engine = new RadarEngine(new RadarConfig());
        var move = Frames().First(f => f.Name == "EnemyB" && f.Op == 0x1A37);
        engine.OnFrame(move.T - 1000, move.Op, move.Body);
        Assert.Empty(engine.Snapshot().Players);
        Feed(engine, Identity("EnemyB"));
        Feed(engine, move);
        Assert.False(Assert.Single(engine.Snapshot().Players).PositionStale);
        engine.OnFrame(move.T + 10_001, 0x0036, new byte[8]);
        Assert.True(Assert.Single(engine.Snapshot().Players).PositionStale);
        engine.OnFrame(move.T + 120_001, 0x0036, new byte[8]);
        Assert.Empty(engine.Snapshot().Players);
    }

    [Theory]
    [InlineData(0x2336)]
    [InlineData(0x2136)]
    public void Teleports_clear_moving_contacts_even_on_the_same_map(int opcode)
    {
        var engine = new RadarEngine(new RadarConfig());
        engine.OnFrame(1, 0x2136, new byte[8]);
        var f = Identity("EnemyB");
        Feed(engine, f);
        Assert.Single(engine.Snapshot().Players);
        engine.OnFrame(f.T + 1, (ushort)opcode, new byte[8]);
        Assert.Empty(engine.Snapshot().Players);
    }

    [Fact]
    public void Real_kill_credit_removes_the_victim_until_another_identity_arrives()
    {
        var engine = new RadarEngine(new RadarConfig());
        Feed(engine, Identity("EnemyB"));
        Feed(engine, Frames().First(f => f.Name == "EnemyB" && f.Op == 0x048D));
        Assert.Empty(engine.Snapshot().Players);
    }

    [Theory]
    [InlineData(19u, "Trainee", true)]
    [InlineData(18u, "Ninth-Class Soldier", false)]
    [InlineData(17u, "Eighth-Class Soldier", true)]
    [InlineData(10u, "First-Class Soldier", false)]
    [InlineData(9u, "PvP-Rang 9", false)]
    [InlineData(0u, null, false)]
    [InlineData(20u, null, false)]
    public void Assumed_rank_names_are_separate_from_screenshot_verification(uint code, string? name, bool verified)
    {
        Assert.Equal(name, PvpRanks.Name(code));
        Assert.Equal(verified, PvpRanks.IsVerified(code));
    }

    [Fact]
    public void Older_config_gets_player_defaults_but_explicit_opcode_moves_are_respected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "opencode", "player-config-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "radar.json");
            File.WriteAllText(path, """{"opcodes":{"self_info":"3336"},"self_id_opcodes":[]}""");
            var cfg = RadarConfig.Load(path, Path.Combine(directory, "missing-cubes.json"), directory);
            Assert.Equal(RecordKind.PlayerInfo, cfg.Opcodes[0x4536]);
            File.WriteAllText(path, """{"opcodes":{"player_info":"9999"},"self_id_opcodes":[]}""");
            cfg = RadarConfig.Load(path, Path.Combine(directory, "missing-cubes.json"), directory);
            Assert.False(cfg.Opcodes.ContainsKey(0x4536));
            Assert.Equal(RecordKind.PlayerInfo, cfg.Opcodes[0x9999]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
