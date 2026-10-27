using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Sessions;
using Aurora21.UltimateTeam.Protocol.Tdf;
using Aurora21.UltimateTeam.Server.Blaze;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Protocol;

public sealed class BlazeLoginIdentityTests
{
    private const string Code = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void LoginBindsOnlyToRegisteredUniqueAuth()
    {
        var player = PlayerIdentity.Of("111111");
        var sessions = new NativeSessions(new NativeAuthSettings(900, 7200));
        sessions.RegisterLocalTestCode(Code, player);

        Assert.Equal(player, BlazeLoginIdentity.PlayerFor(new TdfWriter().Str("AUTH", Code).ToArray(), sessions));
        Assert.Null(BlazeLoginIdentity.PlayerFor(new TdfWriter().Str("AUTH", new string('0', 40)).ToArray(), sessions));
        Assert.Null(BlazeLoginIdentity.PlayerFor(new TdfWriter().Int("AUTH", 123).ToArray(), sessions));
        Assert.Null(BlazeLoginIdentity.PlayerFor(new TdfWriter().Str("AUTH", Code).Str("AUTH", Code).ToArray(), sessions));
        Assert.Null(BlazeLoginIdentity.PlayerFor(new byte[] { 0xff }, sessions));
        sessions.Revoke(player.Id);
        Assert.Null(BlazeLoginIdentity.PlayerFor(new TdfWriter().Str("AUTH", Code).ToArray(), sessions));
    }
}
