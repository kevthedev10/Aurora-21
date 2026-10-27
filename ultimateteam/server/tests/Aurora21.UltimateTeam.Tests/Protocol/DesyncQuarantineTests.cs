using Microsoft.Extensions.Logging.Abstractions;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

/// <summary>
/// The desync quarantine (seasons.json _desyncQuarantineComment): a client whose online matches desync early against enough
/// different opponents pairs only with other quarantined clients until the quarantine runs out; healthy players are never
/// kept apart by one-off desyncs.
/// </summary>
public sealed class DesyncQuarantineTests
{
    private long _now = 1_790_000_000;

    private DesyncQuarantine Quarantine(DesyncQuarantineRules? rules = null) =>
        new(() => rules ?? new DesyncQuarantineRules(), () => _now, NullLogger.Instance);

    [Fact]
    public void ThreeEarlyDesyncsWithThreeOpponentsKeepAPlayerApart()
    {
        var quarantine = Quarantine();
        // Both players report the same match: it counts once.
        quarantine.Note("broken", "a", "id:1", 40);
        quarantine.Note("a", "broken", "id:1", 41);
        // The same opponent again adds no opponent.
        _now += 60;
        quarantine.Note("broken", "a", "id:2", 30);
        _now += 60;
        quarantine.Note("broken", "b", "id:3", 10);
        Assert.False(quarantine.Quarantined("broken"));
        _now += 60;
        quarantine.Note("c", "broken", "id:4", 25);
        Assert.True(quarantine.Quarantined("broken"));
        // Its opponents each desynced once: they stay with everyone, and a match against the quarantined player counts for no one.
        Assert.False(quarantine.Quarantined("a") || quarantine.Quarantined("b") || quarantine.Quarantined("c"));
        Assert.False(quarantine.Compatible("broken", "a"));
        Assert.True(quarantine.Compatible("a", "b"));
        quarantine.Note("d", "broken", "id:5", 5);
        quarantine.Note("d", "e", "id:6", 5);
        quarantine.Note("d", "f", "id:7", 5);
        Assert.False(quarantine.Quarantined("d"));

        // Two quarantined players pair with each other.
        foreach (var (opponent, match) in new[] { ("x", "id:8"), ("y", "id:9"), ("z", "id:10") })
        {
            quarantine.Note("other", opponent, match, 30);
        }
        Assert.True(quarantine.Compatible("broken", "other"));

        // The quarantine runs out on its own.
        _now += new DesyncQuarantineRules().QuarantineSeconds;
        Assert.False(quarantine.Quarantined("broken"));
        Assert.True(quarantine.Compatible("broken", "a"));
    }

    [Fact]
    public void LateOldOrSwitchedOffDesyncsNeverQuarantine()
    {
        var quarantine = Quarantine();
        // A desync late in a match is the rare one-off any pair can hit (live 10-07: 770 s in).
        quarantine.Note("p", "a", "id:1", 770);
        quarantine.Note("p", "b", "id:2", 121);
        quarantine.Note("p", "c", "id:3", null);
        quarantine.Note("p", "d", "id:4", 60);
        Assert.False(quarantine.Quarantined("p"));
        // Desyncs further apart than the window never add up.
        _now += 3601;
        quarantine.Note("p", "e", "id:5", 60);
        _now += 3601;
        quarantine.Note("p", "f", "id:6", 60);
        Assert.False(quarantine.Quarantined("p"));

        // Switched off, nothing is noted and everyone pairs.
        var off = Quarantine(new DesyncQuarantineRules { Enabled = false });
        foreach (var (opponent, match) in new[] { ("a", "id:1"), ("b", "id:2"), ("c", "id:3") })
        {
            off.Note("p", opponent, match, 10);
        }
        Assert.False(off.Quarantined("p"));
        Assert.True(off.Compatible("p", "a"));
    }
}
