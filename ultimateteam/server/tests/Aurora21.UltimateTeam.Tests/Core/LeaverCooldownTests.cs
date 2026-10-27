using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

/// <summary>The in-memory copy of the running leaver penalties matchmaking reads (owner, 2026-10-09).</summary>
public sealed class LeaverCooldownTests
{
    /// <summary>A cooldown bars searches until it ends; a ban bars them until lifted; nobody else is barred.</summary>
    [Fact]
    public void CooldownsEndAndBansLastUntilLifted()
    {
        var cooldown = new LeaverCooldown();
        cooldown.Set("a", new LeaverCooldown.Penalty(1, 4_600));
        cooldown.Set("b", new LeaverCooldown.Penalty(3, null));
        Assert.Equal(4_600, cooldown.Until("a", 4_599));
        Assert.Null(cooldown.Until("a", 4_600));
        Assert.Equal(long.MaxValue, cooldown.Until("b", 99_999_999));
        Assert.Null(cooldown.Until("c", 1_000));

        Assert.Equal([("b", new LeaverCooldown.Penalty(3, null))], cooldown.Active(5_000));
        Assert.True(cooldown.Clear("b"));
        Assert.Null(cooldown.Until("b", 5_000));
    }

    /// <summary>A shorter penalty never cuts a longer one short.</summary>
    [Fact]
    public void TheLongerPenaltyIsKept()
    {
        var cooldown = new LeaverCooldown();
        cooldown.Set("a", new LeaverCooldown.Penalty(2, 90_000));
        cooldown.Set("a", new LeaverCooldown.Penalty(1, 5_000));
        Assert.Equal(90_000, cooldown.Until("a", 1_000));
        cooldown.Set("a", new LeaverCooldown.Penalty(3, null));
        Assert.Equal(long.MaxValue, cooldown.Until("a", 1_000));
    }

    /// <summary>The ladder: 1 hour, then 1 day, then a ban.</summary>
    [Fact]
    public void TheDefaultLadderIsAnHourADayThenABan()
    {
        var rules = new QueueCooldownRules();
        Assert.Equal((2, 3600L), (rules.EarlyLeaves, rules.WindowSeconds));
        Assert.Equal(3600, rules.PenaltySeconds(1));
        Assert.Equal(86400, rules.PenaltySeconds(2));
        Assert.Null(rules.PenaltySeconds(3));
        Assert.Null(rules.PenaltySeconds(7));
    }
}
