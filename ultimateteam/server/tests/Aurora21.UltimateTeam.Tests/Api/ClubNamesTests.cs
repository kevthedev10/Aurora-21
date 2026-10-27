using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Sessions;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Each shared-server player's own club name and abbreviation (production Python server CLUB_IDENTITY's CLUB_NAME), instead of
/// the placeholder "Local FC"/"LCL" every account shared.
/// </summary>
public sealed class ClubNamesTests
{
    private const string G = "/ut/game/fifa21";
    private static readonly ClubNameRules Rules = new() { NameSuffix = " FC", NameMaxLength = 15, AbbreviationLength = 3 };

    [Theory]
    [InlineData("SampleUser", "SampleUser FC", "SAM")]
    [InlineData("Team", "Team FC", "TEA")]
    // Too long for the suffix: the name alone, cut to fifteen characters.
    [InlineData("a_very-long.name", "a_very-long.nam", "AVE")]
    [InlineData("Horizzon", "Horizzon FC", "HOR")]
    // Separators at either end go; too little left takes the account's fallback name.
    [InlineData("_.x-", "Player000001 FC", "PLA")]
    [InlineData(null, "Player000001 FC", "PLA")]
    public void TheNameIsTheRecordedNameAndTheAbbreviationItsFirstLetters(string? displayName, string name, string abbreviation) =>
        Assert.Equal((name, abbreviation), ClubNames.For("100000000000000001", displayName, Rules));

    [Fact]
    public void AShortAbbreviationIsPaddedFromTheAccountsHash()
    {
        // Fifteen characters with one letter among them: the abbreviation is that letter and two letters from the hash.
        var (name, abbreviation) = ClubNames.For("123456789", "a..............b", Rules);
        Assert.Equal("a..............", name);
        Assert.Equal(3, abbreviation.Length);
        Assert.StartsWith("A", abbreviation, StringComparison.Ordinal);
        Assert.All(abbreviation, letter => Assert.InRange(letter, 'A', 'Z'));
        // A pure function of the account.
        Assert.Equal(abbreviation, ClubNames.For("123456789", "a..............b", Rules).Abbreviation);
    }

    [Fact]
    public void ASharedPlayersClubIsNamedAfterItAndTheLocalClubKeepsItsName()
    {
        using var rt = TestRuntime.Create(prepareRoot: root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "state"));
            File.WriteAllText(Path.Combine(root, "state", "player-names.json"), """{ "111111": "Team ✨" }""");
        });
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var named = players.Acquire(PlayerIdentity.Of("111111"));
        using var unnamed = players.Acquire(PlayerIdentity.Of("222222"));

        Assert.Equal(("Team FC", "TEA"), (named.Session.Game.Config.ClubName, named.Session.Game.Config.ClubAbbr));
        Assert.Equal(("Player222222 FC", "PLA"), (unnamed.Session.Game.Config.ClubName, unnamed.Session.Game.Config.ClubAbbr));
        Assert.Equal(("Team", "Player222222"), (named.Session.Game.Config.DisplayName, unnamed.Session.Game.Config.DisplayName));
        Assert.Equal("Team", named.Session.Game.InUnit(uow => uow.DisplayNameAsync()));
        Assert.Equal("Player222222", unnamed.Session.Game.InUnit(uow => uow.DisplayNameAsync()));
        // Every document that names the club uses it: the user, My Stadium and the account info.
        var router = named.Session.Router;
        var user = router.Handle(new Aurora21.UltimateTeam.Core.Web.FutRequest("GET", $"{G}/user", Array.Empty<KeyValuePair<string, string>>(), Array.Empty<byte>()));
        var document = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(user.Body))!.AsObject();
        Assert.Equal(("Team FC", "TEA"), ((string)document["clubName"]!, (string)document["clubAbbr"]!));
        Assert.Equal("Team FC", (string)document["myStadium"]!["name"]!);

        // The local single-player club keeps the configured name.
        Assert.Equal(("Local FC", "LCL"), (rt.Game.Config.ClubName, rt.Game.Config.ClubAbbr));
    }

    [Fact]
    public void AClubNameConfiguredForTheServerIsKept()
    {
        using var rt = TestRuntime.Create(prepareRoot: root =>
        {
            var config = JsonNode.Parse(File.ReadAllText(Path.Combine(TestRuntime.DataRoot, "config.example.json")))!.AsObject();
            config["club_name"] = "Aurora United";
            config["club_abbr"] = "AUR";
            var folder = Path.Combine(root, "user-config");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "config.json"), config.ToJsonString());
        });
        using var players = new PlayerRegistry(rt.Fut, new PlayerRegistryOptions { MaxOpenClubs = 4 }, shared: true);
        using var player = players.Acquire(PlayerIdentity.Of("111111"));
        Assert.Equal(("Aurora United", "AUR"), (player.Session.Game.Config.ClubName, player.Session.Game.Config.ClubAbbr));
    }
}
