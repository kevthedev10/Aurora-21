using System.Text;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Services;
using Aurora21.UltimateTeam.Core.Services.Club;
using Aurora21.UltimateTeam.Core.Web;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// FIFA's own club creation for a hosted player (ultimateteam/onboarding.md): the 465 "no club" answer, GetHistorical,
/// the country action and CreateUser, then the loan, kit and badge steps through to a complete club. These drive the
/// server's routes with the request shapes recovered from CardsDLL; they do not prove the game completes the flow.
/// </summary>
public sealed class NativeClubCreationTests
{
    private const string G = "/ut/game/fifa21";
    private const long Now = 1700000000;
    private const long Brazil = 54;

    /// <summary>One hosted player's club, served by its own router as the player registry would.</summary>
    private sealed class HostedPlayer : IDisposable
    {
        private HostedPlayer(GameContext game, FutServices fut)
        {
            Game = game;
            Fut = fut;
            Router = new FutRouter(fut);
        }

        public GameContext Game { get; }
        public FutServices Fut { get; }
        public FutRouter Router { get; }

        public static async Task<HostedPlayer> OpenAsync(TestRuntime rt, string id, bool native = true)
        {
            var game = await rt.Game.ForPlayerAsync(PlayerIdentity.Of(id), CancellationToken.None, native);
            return new HostedPlayer(game, await FutServices.CreateAsync(game));
        }

        public (int Status, JsonNode Body) CallRaw(string method, string path, JsonNode? payload = null)
        {
            var body = payload is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(payload.ToJsonString());
            var response = Router.Handle(new FutRequest(method, path, Array.Empty<KeyValuePair<string, string>>(), body));
            return (response.Status, FutJson.Parse(Encoding.UTF8.GetString(response.Body)) ?? JsonValue.Create("null")!);
        }

        public JsonNode Call(string method, string path, JsonNode? payload = null)
        {
            var (status, body) = CallRaw(method, path, payload);
            Assert.True(status == 200, $"{method} {path} -> {status}: {body.ToJsonString()}");
            return body;
        }

        public JsonObject CallObject(string method, string path, JsonNode? payload = null) => (JsonObject)Call(method, path, payload);

        public Task<T> Read<T>(Func<Aurora21.UltimateTeam.Core.Data.UnitOfWork, ValueTask<T>> read) => Game.ReadAsync(read, CancellationToken.None);

        public void Dispose() => Game.Dispose();
    }

    private static TestRuntime Create() => TestRuntime.Create(time: new TestRuntime.FixedTime(Now));

    private static JsonObject CreateUserBody(string name = "Samba Stars", string abbreviation = "SAM") => new()
    {
        ["useFut1Data"] = false,
        ["clubName"] = name,
        ["clubAbbr"] = abbreviation,
        ["purchased"] = false,
    };

    private static JsonObject Persona(JsonObject accountInfo) => accountInfo["userAccountInfo"]!["personas"]![0]!.AsObject();

    private static Dictionary<long, long> Entries(JsonNode? document) =>
        document!["entries"]!.AsArray().OfType<JsonObject>().ToDictionary(entry => entry.Int("key"), entry => entry.Int("value"));

    [Fact]
    public async Task ANewHostedClubWaitsForFifaToCreateIt()
    {
        using var rt = Create();
        using (var player = await HostedPlayer.OpenAsync(rt, "700000000000000001"))
        {
            var (status, body) = player.CallRaw("GET", G + "/userMassInfo?screen=futlivemsggen4");
            Assert.Equal(465, status);
            Assert.False(string.IsNullOrEmpty(body.AsObject().Text("reason")));
            foreach (var path in new[] { "/v2/user/accountinfo", "/user/accountinfo" })
            {
                var persona = Persona(player.CallObject("GET", G + path));
                Assert.Empty(persona.Arr("userClubList")!);
                Assert.Equal(0, persona.Int("returningUser"));
                Assert.Equal(player.Game.Config.DisplayName, persona.Text("personaName"));
            }
            var historical = player.CallObject("GET", G + "/user/historical");
            Assert.Equal(player.Game.Config.ClubName, historical.Text("clubName"));
            Assert.Equal(player.Game.Config.ClubAbbr, historical.Text("clubAbbr"));
            Assert.False(historical["isReturningUser"]!.GetValue<bool>());
            Assert.Empty(historical.Arr("returningUserRewards")!);
            Assert.Empty(await player.Read(w => w.ListItemsAsync()));
            Assert.Empty(await player.Read(w => w.ListSquadsAsync()));
        }
        // Opening it again seeds nothing either: only FIFA's CreateUser gives it content.
        using var reopened = await HostedPlayer.OpenAsync(rt, "700000000000000001");
        Assert.Empty(await reopened.Read(w => w.ListItemsAsync()));
        Assert.Equal(465, reopened.CallRaw("GET", G + "/userMassInfo").Status);
    }

    [Fact]
    public async Task TheCountryAndCreateUserBuildTheStarterClubOnce()
    {
        using var rt = Create();
        var rules = rt.Game.Rules.Onboarding;
        using (var player = await HostedPlayer.OpenAsync(rt, "700000000000000002"))
        {
            var persona = player.Game.Config.DisplayName;
            Assert.Equal(404, player.CallRaw("POST", G + "/user/action/ONBOARDING_COUNTRY_10_SELECTED").Status);
            Assert.Equal(404, player.CallRaw("POST", G + "/user/action/ONBOARDING_COUNTRY_0_SELECTED").Status);
            // The action names the country screen's position (2 = Brazil), not a nation id.
            Assert.True(JsonNode.DeepEquals(new JsonObject(), player.Call("POST", G + "/user/action/ONBOARDING_COUNTRY_2_SELECTED")));
            Assert.Equal(Brazil, (await player.Read(w => w.OnboardingAsync())).NationId);

            var created = player.CallObject("POST", G + "/user", CreateUserBody());
            Assert.False(created["bonusPacks"]!.GetValue<bool>());
            Assert.Equal("Samba Stars", created.Obj("login")!.Text("clubName"));
            Assert.Equal("SAM", created.Obj("login")!.Text("clubAbbr"));
            Assert.NotNull(created.Obj("userData"));
            var starter = created.Arr("starterPack")!.OfType<JsonObject>().ToList();
            var players = starter.Where(item => item.Text("itemType") != "manager").ToList();
            Assert.Equal(rules.Starter.Positions.Count, players.Count);
            Assert.Single(starter, item => item.Text("itemType") == "manager");
            Assert.All(starter, item => Assert.True(item["untradeable"]!.GetValue<bool>()));
            Assert.All(players, card => Assert.InRange(card.Int("rating"), rules.Starter.MinRating, rules.Starter.MaxRating));
            Assert.All(players, card => Assert.InRange(card.Int("rareflag"), 0, rules.Starter.MaxRareflag));
            Assert.Equal(players.Count, players.Select(card => card.Int("assetId")).Distinct().Count());
            // Every other slot draws from the chosen country, which has cards for every starter position.
            Assert.All(players.Where((_, slot) => slot % 2 == 0), card => Assert.Equal(Brazil, card.Int("nation")));
            // The starting eleven stand in the formation's positions.
            var squad = created.Obj("squad")!.Arr("players")!.OfType<JsonObject>().Where(slot => slot["itemData"] is JsonObject)
                .OrderBy(slot => slot.Int("index")).ToList();
            Assert.Equal(players.Count, squad.Count);
            Assert.Equal(rules.Starter.Positions.Take(11), squad.Take(11).Select(slot => slot.Obj("itemData")!.Text("preferredPosition")));

            var mass = player.CallObject("GET", G + "/userMassInfo?screen=futlivemsggen4");
            Assert.Equal("Samba Stars", mass.Obj("userInfo")!.Text("clubName"));
            Assert.Equal(rules.Loans.PlayersPerPosition * rules.Loans.Positions.Count, mass.Obj("loanPlayers")!.Arr("loans")!.Count);
            var club = Persona(player.CallObject("GET", G + "/v2/user/accountinfo"));
            Assert.Equal("Samba Stars", club.Arr("userClubList")![0]!.AsObject().Text("clubName"));
            // The persona (the name shown on the player) stays the recorded name.
            Assert.Equal(persona, club.Text("personaName"));
            Assert.Equal(persona, player.Game.Config.DisplayName);

            // A repeated CreateUser (a lost reply) answers the same club and grants nothing.
            var items = (await player.Read(w => w.ListItemsAsync())).Count;
            var again = player.CallObject("POST", G + "/user", CreateUserBody("Other Name", "OTH"));
            Assert.Equal(players.Select(card => card.Int("id")).Order(),
                again.Arr("starterPack")!.OfType<JsonObject>().Where(item => item.Text("itemType") != "manager").Select(item => item.Int("id")).Order());
            Assert.Equal("Samba Stars", again.Obj("login")!.Text("clubName"));
            Assert.Equal(items, (await player.Read(w => w.ListItemsAsync())).Count);
            // So does a later country action.
            player.Call("POST", G + "/user/action/ONBOARDING_COUNTRY_3_SELECTED");
            Assert.Equal(Brazil, (await player.Read(w => w.OnboardingAsync())).NationId);
        }
        // The chosen name survives a reopen, and maintenance never tops the club up with the configured players.
        using var reopened = await HostedPlayer.OpenAsync(rt, "700000000000000002");
        Assert.Equal("Samba Stars", reopened.Game.Config.ClubName);
        Assert.Equal("SAM", reopened.Game.Config.ClubAbbr);
        Assert.Equal(rules.Starter.Positions.Count, (await reopened.Read(w => w.ListItemsAsync())).Count(item => item.Text("itemType") == "player"));
        Assert.Equal("Samba Stars", reopened.CallObject("GET", G + "/userMassInfo").Obj("userInfo")!.Text("clubName"));
    }

    [Fact]
    public async Task CreateUserWithoutACountryUsesTheScreensDefaultAndKeepsTypedNamesWithinLimits()
    {
        using var rt = Create();
        using var player = await HostedPlayer.OpenAsync(rt, "700000000000000003");
        var created = player.CallObject("POST", G + "/user", CreateUserBody("  A Club Name Far Longer Than Thirty Characters  ", "ABCDEFG"));
        Assert.Equal("A Club Name Far Longer Than Th", created.Obj("login")!.Text("clubName"));
        Assert.Equal("ABCDE", created.Obj("login")!.Text("clubAbbr"));
        Assert.Equal(rt.Game.Rules.Onboarding.CountryNations[0], (await player.Read(w => w.OnboardingAsync())).NationId);
    }

    [Fact]
    public async Task TheClubNameScreenNamesACreatedClubOnce()
    {
        using var rt = Create();
        const string Rename = G + "/user/club?profaneNameChange=false";
        using (var player = await HostedPlayer.OpenAsync(rt, "700000000000000004"))
        {
            // FIFA creates the club straight after the country screen, with GetHistorical's default name.
            var historical = player.CallObject("GET", G + "/user/historical");
            player.Call("POST", G + "/user/action/ONBOARDING_COUNTRY_1_SELECTED");
            var created = player.CallObject("POST", G + "/user", CreateUserBody(historical.Text("clubName"), historical.Text("clubAbbr")));
            // The user document it reads then offers the club-name screen.
            Assert.Equal(1, created.Obj("login")!.Int("clubNameChangeAllowed"));
            Assert.Equal(1, player.CallObject("GET", G + "/user").Int("clubNameChangeAllowed"));
            Assert.Equal(1, player.CallObject("GET", G + "/userMassInfo").Obj("userInfo")!.Int("clubNameChangeAllowed"));

            Assert.Equal(400, player.CallRaw("PUT", Rename, new JsonObject { ["clubName"] = "  ", ["clubAbbr"] = "AUR" }).Status);
            Assert.Equal(400, player.CallRaw("PUT", Rename, new JsonObject { ["clubName"] = "Aurora Athletic" }).Status);
            Assert.True(JsonNode.DeepEquals(new JsonObject(),
                player.Call("PUT", Rename, new JsonObject { ["clubName"] = "Aurora Athletic", ["clubAbbr"] = "AURATH" })));
            var user = player.CallObject("GET", G + "/user");
            Assert.Equal(("Aurora Athletic", "AURAT", 0L), (user.Text("clubName"), user.Text("clubAbbr"), user.Int("clubNameChangeAllowed")));
            Assert.Equal("Aurora Athletic", player.CallObject("GET", G + "/userMassInfo").Obj("userInfo")!.Text("clubName"));
            // One name only: the screen is not offered again, and another rename is refused.
            Assert.Equal(403, player.CallRaw("PUT", Rename, new JsonObject { ["clubName"] = "Second Name", ["clubAbbr"] = "SEC" }).Status);
            Assert.Equal("Aurora Athletic", player.Game.Config.ClubName);
        }
        using var reopened = await HostedPlayer.OpenAsync(rt, "700000000000000004");
        Assert.Equal(("Aurora Athletic", "AURAT"), (reopened.Game.Config.ClubName, reopened.Game.Config.ClubAbbr));
        Assert.Equal(0, reopened.CallObject("GET", G + "/user").Int("clubNameChangeAllowed"));
    }

    [Fact]
    public async Task TheLoanKitAndBadgeStepsCompleteTheCreatedClub()
    {
        using var rt = Create();
        var loanRules = rt.Game.Rules.Onboarding.Loans;
        using (var player = await HostedPlayer.OpenAsync(rt, "700000000000000005"))
        {
            player.Call("POST", G + "/user/action/ONBOARDING_COUNTRY_1_SELECTED");
            player.Call("POST", G + "/user", CreateUserBody("Three Lions", "TLS"));

            // Loan offers: base cards only, ranked per position, none of a footballer the club owns.
            var offers = player.CallObject("GET", G + "/loan/players").Arr("loans")!.OfType<JsonObject>().ToList();
            Assert.Equal(loanRules.PlayersPerPosition * loanRules.Positions.Count, offers.Count);
            Assert.All(offers, offer => Assert.InRange(offer.Obj("itemData")!.Int("rareflag"), 0, loanRules.MaxRareflag));
            Assert.All(offers.GroupBy(offer => offer.Obj("itemData")!.Text("preferredPosition")),
                group => Assert.Equal(Enumerable.Range(1, loanRules.PlayersPerPosition).Select(rank => (long)rank), group.Select(offer => offer.Int("ranking"))));
            // FIFA shows a loan card's matches left through its contract count, so an offer carries the games there too.
            Assert.All(offers, offer => Assert.Equal((loanRules.Games, loanRules.Games), (offer.Obj("itemData")!.Int("loans"), offer.Obj("itemData")!.Int("contract"))));
            var striker = offers.First(offer => offer.Obj("itemData")!.Text("preferredPosition") == "ST");
            var loan = player.CallObject("PUT", $"{G}/loan/player/{striker.Int("defId")}");
            Assert.Equal(loanRules.Games, loan.Int("loans"));
            Assert.Equal(loanRules.Games, loan.Int("contract"));
            Assert.Equal(striker.Obj("itemData")!.Int("resourceId"), loan.Int("resourceId"));
            Assert.Equal(404, player.CallRaw("PUT", $"{G}/loan/player/1").Status);
            player.Call("PUT", G + "/item", new JsonObject { ["itemData"] = new JsonArray(new JsonObject { ["id"] = loan.Int("id"), ["pile"] = "club" }) });

            // The loan-explore save (onboarding=true) with the loan up front records the loan marker FIFA resumes from.
            var squad = (await player.Read(w => w.ListSquadsAsync()))[0];
            squad["players"]!.AsArray().OfType<JsonObject>().Single(slot => slot.Int("index") == 9)["itemId"] = loan.Int("id");
            player.Call("PUT", G + "/squad/1?onboarding=true", squad);
            var afterLoan = player.CallObject("GET", G + "/userMassInfo");
            Assert.Equal(loan.Int("resourceId") & 0xFFFFFF, Entries(afterLoan["loanPlayerClientData"])[0]);
            // The stored loan keeps no contracts; FIFA is still sent its games as the contract count in the squad.
            Assert.Equal(0, (await player.Read(w => w.GetItemAsync(loan.Int("id"))))!.Int("contract"));
            var squadLoan = afterLoan.Obj("squad")!.Arr("players")!.OfType<JsonObject>()
                .Select(slot => slot.Obj("itemData")).Single(item => item is not null && item.Int("id") == loan.Int("id"))!;
            Assert.Equal((loanRules.Games, loanRules.Games), (squadLoan.Int("loans"), squadLoan.Int("contract")));

            // Kits: a random choice per club, the same on every visit; the chosen pair is granted and equipped.
            var kits = player.CallObject("GET", G + "/onboarding/kits");
            Assert.Equal(rt.Game.Rules.Onboarding.Kits.HomeChoices, kits.Arr("homeItemDataList")!.Count);
            Assert.Equal(rt.Game.Rules.Onboarding.Kits.AwayChoices, kits.Arr("awayItemDataList")!.Count);
            Assert.True(JsonNode.DeepEquals(kits, player.Call("GET", G + "/onboarding/kits")));
            var home = kits.Arr("homeItemDataList")![1]!.AsObject().Int("resourceId");
            var away = kits.Arr("awayItemDataList")![2]!.AsObject().Int("resourceId");
            Assert.Equal(404, player.CallRaw("POST", G + "/onboarding/kits", new JsonObject { ["homeKitId"] = away, ["awayKitId"] = home }).Status);
            var chosenKits = player.CallObject("POST", G + "/onboarding/kits", new JsonObject { ["homeKitId"] = home, ["awayKitId"] = away });
            Assert.True(JsonNode.DeepEquals(chosenKits, player.Call("POST", G + "/onboarding/kits", new JsonObject { ["homeKitId"] = home, ["awayKitId"] = away })));
            var ownedHome = (await player.Read(w => w.GetItemAsync(chosenKits.Int("homeKitId"))))!;
            var ownedAway = (await player.Read(w => w.GetItemAsync(chosenKits.Int("awayKitId"))))!;
            Assert.Equal((home, "activeHomeKit"), (ownedHome.Int("resourceId"), ownedHome.Text("itemState")));
            Assert.Equal((away, "activeAwayKit"), (ownedAway.Int("resourceId"), ownedAway.Text("itemState")));
            Assert.True(ownedHome["untradeable"]!.GetValue<bool>());
            var afterKits = Entries(player.CallObject("GET", G + "/userMassInfo")["onboardingClientData"]);
            Assert.Equal((home, away), (afterKits[1], afterKits[2]));

            // Badge: a random choice per club; the pick is granted, equipped and completes onboarding.
            var badges = player.CallObject("GET", G + "/onboarding/badges").Arr("badgeItemDataList")!;
            Assert.Equal(rt.Game.Rules.Onboarding.Badges.Choices, badges.Count);
            var badge = badges[4]!.AsObject().Int("resourceId");
            Assert.False(player.Fut.Onboarding.IsComplete());
            var chosenBadge = player.CallObject("POST", $"{G}/onboarding/badge/{badge}");
            Assert.True(JsonNode.DeepEquals(chosenBadge, player.Call("POST", $"{G}/onboarding/badge/{badge}")));
            var ownedBadge = (await player.Read(w => w.GetItemAsync(chosenBadge.Int("badgeId"))))!;
            Assert.Equal((badge, "activeBadge"), (ownedBadge.Int("resourceId"), ownedBadge.Text("itemState")));
            Assert.True(player.Fut.Onboarding.IsComplete());

            var complete = player.CallObject("GET", G + "/userMassInfo");
            Assert.Empty(complete.Obj("loanPlayers")!.Arr("loans")!);
            Assert.Equal(new Dictionary<long, long> { [0] = 3, [1] = home, [2] = away, [3] = badge }, Entries(complete["onboardingClientData"]));
            Assert.Equal(loan.Int("resourceId") & 0xFFFFFF, Entries(complete["loanPlayerClientData"])[0]);
        }
        using var reopened = await HostedPlayer.OpenAsync(rt, "700000000000000005");
        Assert.Equal(rt.Game.Rules.Onboarding.Starter.Positions.Count + 1, (await reopened.Read(w => w.ListItemsAsync())).Count(item => item.Text("itemType") == "player"));
        Assert.Empty(reopened.CallObject("GET", G + "/loan/players").Arr("loans")!);
    }

    [Fact]
    public async Task AClubMadeBeforeNativeCreationKeepsItsClub()
    {
        using var rt = Create();
        using (var seeded = await HostedPlayer.OpenAsync(rt, "700000000000000006", native: false))
        {
            Assert.NotEmpty(await seeded.Read(w => w.ListItemsAsync()));
        }
        using var player = await HostedPlayer.OpenAsync(rt, "700000000000000006");
        Assert.NotNull(player.CallObject("GET", G + "/userMassInfo").Obj("userInfo"));
        Assert.NotEmpty(Persona(player.CallObject("GET", G + "/v2/user/accountinfo")).Arr("userClubList")!);
        // Its CreateUser is FIFA's retry path: it answers the existing club and grants nothing.
        var items = (await player.Read(w => w.ListItemsAsync())).Count;
        player.Call("POST", G + "/user", CreateUserBody());
        Assert.Equal(items, (await player.Read(w => w.ListItemsAsync())).Count);
        Assert.Null((await player.Read(w => w.OnboardingAsync())).ClubName);
        // FIFA did not create it, so it is not offered the club-name screen.
        Assert.Equal(0, player.CallObject("GET", G + "/user").Int("clubNameChangeAllowed"));
        Assert.Equal(403, player.CallRaw("PUT", G + "/user/club?profaneNameChange=false", new JsonObject { ["clubName"] = "New", ["clubAbbr"] = "NEW" }).Status);
    }

    [Fact]
    public async Task AServerThatSeedsClubsNeverAnswersCreateUser()
    {
        using var rt = Create();
        using var player = await HostedPlayer.OpenAsync(rt, "700000000000000007", native: false);
        var user = player.CallObject("POST", G + "/user", CreateUserBody());
        Assert.Null(user["starterPack"]);
        Assert.Equal(200, player.CallRaw("GET", G + "/userMassInfo").Status);
    }

    [Fact]
    public void TheStarterPolicyIsRepeatableAndDiffersByCountry()
    {
        using var rt = Create();
        var onboarding = rt.Fut.Onboarding;
        Assert.Equal(onboarding.StarterPlayerIds(Brazil), onboarding.StarterPlayerIds(Brazil));
        Assert.NotEqual(onboarding.StarterPlayerIds(Brazil), onboarding.StarterPlayerIds(14));
        Assert.NotNull(onboarding.StarterManagerId(Brazil));
    }
}
