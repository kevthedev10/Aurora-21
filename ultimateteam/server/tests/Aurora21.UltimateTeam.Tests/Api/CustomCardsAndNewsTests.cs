using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Content;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>The developers' Pro cards (migration 066; rules/cards.json aquaProCards) and FUT News.</summary>
public sealed class CustomCardsAndNewsTests
{
    private const string G = "/ut/game/fifa21";

    /// <summary>Each developer's card: its id, base card, position, club, league and nation (native ids).</summary>
    private static readonly (long Id, long Base, string Position, long Team, long League, long Nation)[] ProCards =
    {
        (167988548, 216388, "LM", 13, 13, 18),   // Ghoul: Allan Saint-Maximin, Newcastle
        (167996618, 224458, "ST", 9, 13, 38),    // Mustafa: Diogo Jota, Liverpool
        (167983460, 211300, "LM", 11, 13, 18),   // Shadow: Anthony Martial, Manchester United
        (168019987, 247827, "LM", 21, 19, 18),   // Miz: Michael Olise, moved from Reading to FC Bayern
        (167984358, 212198, "CAM", 11, 13, 38),  // Horizzon: Bruno Fernandes, Manchester United
        (167961228, 189068, "ST", 1913, 10, 34), // Atlaz: Bas Dost, moved from Eintracht Frankfurt to SC Heerenveen
    };

    /// <summary>
    /// Every good FIFA 21 trait1 bit: Power Free Kick, Solid Player, Leadership, Early Crosser, Finesse Shot, Flair,
    /// Power Header, Giant Throw-in, Outside Foot Shot, Team Player.
    /// </summary>
    private const long GoodTraits = 17007562;

    [Fact]
    public void TheDevelopersProCardsAreExclusiveMaxedCardsAndTheirBaseCardsAreNot()
    {
        using var rt = TestRuntime.Create();
        var cards = rt.Game.Cards;
        foreach (var (id, baseId, position, team, league, nation) in ProCards)
        {
            var card = rt.Game.Players.Find(id)!;
            Assert.Equal((99L, 10L, baseId, position), (card.Rating, card.Rareflag, card.IntOr("assetId", 0, 0), card.Position));
            Assert.Equal((team, league, nation), (card.Int("teamid"), card.Int("leagueId"), card.Int("nation")));
            Assert.True(card.Bool("untradeable"));
            // 5-star weak foot and skill moves (067), every good trait and Set Play Specialist, never Injury Prone (bit 2).
            Assert.Equal((5L, 5L), (card.Int("weakFoot"), card.Int("skillMoves")));
            Assert.Equal(GoodTraits, card.Int("trait1") & GoodTraits);
            Assert.Equal(0, card.Int("trait1") & 4);
            Assert.Equal(1, card.Int("trait2") & 1);
            Assert.True(cards.IsAquaPro(id) && cards.IsGrantOnly(id));
            Assert.Equal(id, cards.ProCardOfBase(baseId));
            // Outfield defaults remain maxed. Metadata also honours the actual
            // stored GK fields, rather than replacing all 34 slots with 99.
            var attributes = card.Get("attributes")!.AsObject().ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value!.GetValue<long>());
            Assert.Equal(rt.Game.Rules.Cards.InGameAttributeOrder.Select(name => attributes.GetValueOrDefault(name)),
                cards.Metadata(id)["ingameattribs"]!.AsArray().Select(value => value!.GetValue<long>()));

            // The base card is an ordinary card: not grant-only, its own attributes in the details screen and in squads.
            Assert.False(cards.IsAquaPro(baseId) || cards.IsGrantOnly(baseId));
            var baseRating = rt.Game.Players.Find(baseId)!.Rating;
            Assert.Equal(baseRating, cards.Metadata(baseId).Int("rating"));
            Assert.Equal(baseRating, cards.SquadSlotAttributes(rt.Fut.Items.BuildPlayer(baseId, 900, "club"))!.Int("rating"));
            Assert.Equal(99, cards.SquadSlotAttributes(rt.Fut.Items.BuildPlayer(id, 901, "club"))!.Int("rating"));

            // Nothing but a grant offers one: not the concept searches, not the onboarding loans.
            Assert.DoesNotContain(rt.Fut.Native.DefinitionVariants(baseId), item => item.Int("resourceId") == id);
            Assert.DoesNotContain(id, rt.Fut.Onboarding.LoanPlayerResourceIds());
        }
        // Bruno's portrait is his FIFA 21 Team of the Year art.
        Assert.Equal(201538790, rt.Game.Players.Find(167984358)!.Int("dynamicImageResourceId"));
        // The unused Pro cards are gone: 99 Neymar Jr, 99 Declan Rice, 99 Paul Pogba.
        Assert.All(new long[] { 167963031, 168006538, 167968024 }, id => Assert.Null(rt.Game.Players.Find(id)));
        var concepts = rt.CallObject("GET", $"{G}/defid?type=player&pos=ST&count=200")["itemData"]!.AsArray().OfType<JsonObject>();
        Assert.DoesNotContain(concepts, item => ProCards.Any(card => card.Id == item.Int("resourceId")));
    }

    [Fact]
    public async Task ABaseCardShowsTheProRecordOnlyToTheClubThatOwnsItsProCard()
    {
        using var rt = TestRuntime.Create();
        const long baseJota = 224458, proJota = 167996618;
        JsonObject Record(long id) => rt.Call("GET", $"{G}/attributes/metadata?defIds={id}").AsArray().Single()!.AsObject();
        Assert.Equal(rt.Game.Players.Find(baseJota)!.Rating, Record(baseJota).Int("rating"));
        Assert.Equal(99, Record(proJota).Int("rating"));

        // Once this club owns the Pro card, the details screen's request for the base asset gets the Pro record.
        await rt.Game.RunAsync("grant-pro", w => rt.Fut.Items.MakePlayerAsync(w, proJota, "unassigned"), CancellationToken.None);
        var owned = Record(baseJota);
        Assert.Equal((baseJota, 99L), (owned.Int("defId"), owned.Int("rating")));
        var direct = Record(proJota);
        direct["defId"] = baseJota;
        Assert.True(JsonNode.DeepEquals(direct, owned));
    }

    [Fact]
    public async Task PublishedProEditsReachR3MetadataBaseAliasesAndSquads()
    {
        using var rt = TestRuntime.Create();
        const long resource = 167984358, baseId = 212198; // Bruno Fernandes Pro.
        JsonObject Record(long id) => rt.Call("GET", $"{G}/attributes/metadata?defIds={id}").AsArray().Single()!.AsObject();
        var owned = await rt.Game.RunAsync("grant-pro", w => rt.Fut.Items.MakePlayerAsync(w, resource, "club"), CancellationToken.None);
        var savedSquad = rt.CallObject("GET", $"{G}/squad/active");
        var squadSlot = savedSquad["players"]!.AsArray()[0]!.AsObject();
        squadSlot["itemData"] = owned.DeepClone();
        squadSlot["itemId"] = owned.Int("id");
        Assert.Equal(200, rt.CallRaw("PUT", $"{G}/squad/{savedSquad.Int("id")}", savedSquad).Status);
        Assert.Equal(99, rt.CallObject("GET", $"{G}/squad/active")["players"]![0]!["itemData"]!["rating"]!.GetValue<long>());
        var previousVersion = Record(resource).Int("version");
        var area = new PlayerCardArea();
        var changes = new ContentChangesets(rt.Game, [area]);
        var key = new JsonObject { ["resourceId"] = resource };
        var live = (await rt.Game.UnitOfWork.RunReadAsync(0, w => area.ReadAsync(w, key)))!;
        var card = live["card"]!.DeepClone().AsObject();
        card["rating"] = 75;
        card["face_slot_0"] = 70;
        card["acceleration"] = 70;
        card["sprint_speed"] = 70;
        card["gk_diving"] = 40;
        await changes.SetChangeAsync(PlayerCardArea.AreaName, key, ContentOperation.Update,
            new JsonObject { ["card"] = card }, "tester");
        Assert.Equal(99, Record(resource).Int("rating")); // Staging never reaches FIFA.
        Assert.True((await changes.ValidateAsync()).Ok);
        await changes.PublishAsync("edit Bruno Pro", "tester");

        void EditedRecord(JsonObject record, long requestedId)
        {
            Assert.Equal(requestedId, record.Int("defId"));
            Assert.Equal(75, record.Int("rating"));
            Assert.Equal(70, record["attributeArray"]![0]!.GetValue<long>());
            Assert.Equal(70, record["ingameattribs"]![0]!.GetValue<long>());
            Assert.Equal(70, record["ingameattribs"]![1]!.GetValue<long>());
            Assert.Equal(40, record["ingameattribs"]![29]!.GetValue<long>());
            Assert.True(record.Int("version") > 0);
            Assert.NotEqual(previousVersion, record.Int("version"));
        }
        // Repeated inspection through either ID must keep the published values.
        foreach (var id in new[] { resource, baseId, resource }) EditedRecord(Record(id), id);
        var item = rt.Fut.Items.BuildPlayer(resource, 901, "club");
        Assert.Equal(75, item.Int("rating"));
        EditedRecord(rt.Game.Cards.SquadSlotAttributes(item)!, resource);
        Assert.Equal(Record(resource).Int("version"), rt.Game.Cards.AttributeRecordVersion(resource));
        Assert.True(item.Bool("untradeable"));
        Assert.True(rt.Game.Cards.IsGrantOnly(resource));
        // The client-saved itemData had 99; fresh squad responses must use the published definition.
        foreach (var path in new[] { "squad/active", $"squad/{savedSquad.Int("id")}" })
        {
            var slot = rt.CallObject("GET", $"{G}/{path}")["players"]![0]!.AsObject();
            Assert.Equal(owned.Int("id"), slot["itemData"]!.AsObject().Int("id"));
            Assert.Equal(75, slot["itemData"]!.AsObject().Int("rating"));
            EditedRecord(slot["playerAttributes"]!.AsObject(), resource);
        }
    }

    [Fact]
    public void EveryProCardHasItsPortrait()
    {
        var folder = Path.Combine(TestRuntime.DataRoot, "custom-playerheads");
        foreach (var (id, _, _, _, _, _) in ProCards)
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder, $"p{id}.dds"));
            Assert.Equal("DDS DXT5", System.Text.Encoding.ASCII.GetString(bytes, 0, 4) + System.Text.Encoding.ASCII.GetString(bytes, 84, 4));
            Assert.Equal((256, 220), (BitConverter.ToInt32(bytes, 12), BitConverter.ToInt32(bytes, 16)));
        }
    }

    [Fact]
    public void FutNewsIsAnnouncedAndServed()
    {
        using var rt = TestRuntime.Create();
        var news = rt.Game.Rules.Club.News;

        var messages = rt.CallObject("GET", $"{G}/livemessage");
        var message = messages["messageList"]!.AsArray().Single()!.AsObject();
        Assert.Equal((news.MessageId, news.Title), (message.Int("messageId"), message.Text("title")));
        Assert.StartsWith(news.Title + "\n\n", message.Text("message"));
        Assert.Equal(1, messages.Int("messagesAvailable"));

        Assert.Equal(news.Enabled, rt.CallObject("GET", $"{G}/hub").Bool("liveMessagesAvailable"));
        var massInfo = rt.CallObject("GET", $"{G}/usermassinfo");
        Assert.Equal(news.Enabled, massInfo.Bool("liveMessagesAvailable"));
        Assert.Equal(new[] { "liveMessagesAvailable", "credits" }, massInfo.Select(field => field.Key).TakeLast(2));
    }

    [Fact]
    public async Task ProCardsReachFifaWithTheirOwnStarsInUnassignedAndTheClub()
    {
        // A Pro card shares its base player's asset, so without its own native fields FIFA shows the base player's stars (the
        // 4-star Bruno Fernandes on his 5-star Pro card, 2026-09-29).
        using var rt = TestRuntime.Create();
        foreach (var (id, _, _, _, _, _) in ProCards)
        {
            await rt.Game.RunAsync("grant-pro", w => rt.Fut.Items.MakePlayerAsync(w, id, "unassigned"), CancellationToken.None);
        }
        void FiveStars(IEnumerable<JsonObject> items, string where) => Assert.All(items.Where(item => rt.Game.Cards.IsAquaPro(item.Int("resourceId"))), item =>
            Assert.True((4L, 5L) == (item.Int("skillmoves", -1), item.Int("weakfootabilitytypecode", -1)), $"{where} {item.Int("resourceId")}: {item.ToJsonString()}"));
        var unassigned = rt.CallObject("GET", $"{G}/purchased/items")["itemData"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(ProCards.Length, unassigned.Count(item => rt.Game.Cards.IsAquaPro(item.Int("resourceId"))));
        FiveStars(unassigned, "unassigned");

        rt.CallObject("PUT", $"{G}/item", new JsonObject
        {
            ["itemData"] = new JsonArray(unassigned.Select(item => (JsonNode?)new JsonObject { ["id"] = item.Int("id"), ["pile"] = "club" }).ToArray()),
        });
        var club = rt.CallObject("POST", $"{G}/club", new JsonObject { ["type"] = "player", ["count"] = 200 })["itemData"]!.AsArray().OfType<JsonObject>().ToList();
        Assert.Equal(ProCards.Length, club.Count(item => rt.Game.Cards.IsAquaPro(item.Int("resourceId"))));
        FiveStars(club, "club");
    }
}
