using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Models;
using Aurora21.UltimateTeam.Core.Rules;
using Aurora21.UltimateTeam.Core.Services.Club;

namespace Aurora21.UltimateTeam.Core.Services.Modes;

/// <summary>
/// Squad Building Challenges. The authored sbc.json vocabulary (rules and rewards) is
/// translated to the client's elgReq/awards contract using rules/sbc.json; rewards are
/// applied once and packs are delivered unopened to My Packs.
/// </summary>
public sealed partial class SbcService
{
    private readonly FutServices _fut;
    private readonly ILogger _log;
    // Keyed by the catalog entry itself, so a content reload (new entries) never serves an old name.
    private readonly ConditionalWeakTable<PlayerMeta, StrongBox<(string Primary, string Common, string Full)>> _normalizedNames = new();

    public SbcService(FutServices fut)
    {
        _fut = fut;
        _log = fut.Game.Logger("UltimateTeam.Sbc");
    }

    private GameContext Game => _fut.Game;
    private SbcRules Rules => Game.Rules.Sbc;

    /// <summary>An authored reward entry: its upper-cased type, attributes and numeric value.</summary>
    private sealed record Reward(JsonObject Entry, string Kind, JsonObject Attributes, long Value)
    {
        public static IEnumerable<Reward> In(JsonObject source) =>
            ((source.Obj("rewards") ?? new JsonObject()).Arr("entries") ?? new JsonArray()).OfType<JsonObject>().Select(From);

        public static Reward From(JsonObject entry)
        {
            var attributes = AttributesOf(entry);
            return new Reward(entry, JsonValues.Text(entry.Get("type")).ToUpperInvariant(), attributes,
                JsonValues.Int(entry.GetOr("value", attributes.GetOr("value", 0))));
        }

        public long Count => Attributes.Int("count", 1);
    }

    /// <summary>One authored requirement as an eligibility slot (when the client can check it) and a description line.</summary>

    private static JsonObject AttributesOf(JsonObject entry) => entry.Obj("attributes") ?? new JsonObject();

    private static IEnumerable<JsonObject> RequirementRules(JsonObject source) =>
        ((source.Obj("requirements") ?? new JsonObject()).Arr("rules") ?? new JsonArray()).OfType<JsonObject>();

    /// <summary>
    /// Whether the game leaves a set out because it is not on the live list (migration 142) or is switched off there: it is
    /// listed nowhere, and its challenges cannot be saved or submitted.
    /// </summary>
    public bool IsHidden(SbcSet set) => !Game.Sbc.IsLive(set.Key);

    private bool IsHidden(SbcChallenge challenge) => !Game.Sbc.IsLive(challenge.SetKey);

    /// <summary>A PACK reward's pack id: <c>packId</c> names one directly (the panel's SBC editor); otherwise its pack key.</summary>
    /// <summary>The pack a PACK reward pays: its packId (the catalogue refuses a pack reward without one).</summary>
    public static long RewardPackId(JsonObject attributes) => attributes.Int("packId");

    /// <summary>The name as NFKD-normalized, lower-case letters and digits, for fuzzy matching.</summary>
    public static string NormalizeName(JsonNode? value)
    {
        var text = (JsonValues.HasValue(value) ? JsonValues.Text(value) : string.Empty).Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber)
            {
                builder.Append(Rune.ToLowerInvariant(rune).ToString());
            }
        }
        return builder.ToString();
    }

    /// <summary>Resolves an authored player reward to the closest real FIFA 21 card.</summary>
    public long PlayerResourceId(JsonObject entry)
    {
        var attributes = AttributesOf(entry);
        var wanted = NormalizeName(attributes.Get("name"));
        // A player picked in the panel names only its card: pay exactly that card. Authored sbc.json players carry a name
        // (and a rating and position) and are matched by them below, with their resourceId as the fallback.
        if (wanted.Length == 0)
        {
            var picked = attributes.Int("resourceId");
            return Game.Players.Contains(picked) ? picked : 0;
        }
        var wantedRating = attributes.Int("rating");
        var wantedPosition = attributes.Str("position").ToUpperInvariant();
        (long Score, long Id)? best = null;
        foreach (var (id, meta) in Game.Players.Entries)
        {
            if (Game.Cards.IsGrantOnly(id))
            {
                continue;
            }
            if (NameDistance(wanted, NormalizedNames(meta)) is not { } identity)
            {
                continue;
            }
            var score = identity * 1000 + Math.Abs(meta.Rating - wantedRating) * 10;
            if (wantedPosition.Length > 0 && meta.Position.ToUpperInvariant() != wantedPosition)
            {
                score += 8;
            }
            if (meta.Rareflag <= 1)
            {
                score += 3;
            }
            if (best is null || (score, id).CompareTo(best.Value) < 0)
            {
                best = (score, id);
            }
        }
        if (best is not null)
        {
            return best.Value.Id;
        }
        var fallback = attributes.Int("resourceId");
        return Game.Players.Contains(fallback) ? fallback : 0;
    }

    /// <summary>0 for an exact name match, 1 or 2 for a partial primary or common name match, null for no match.</summary>
    private static long? NameDistance(string wanted, (string Primary, string Common, string Full) names)
    {
        bool Same(string name) => name.Length > 0 && name == wanted;
        // Partial matches support labels such as "R9 Ronaldo" without matching only on a shared first name.
        bool Overlaps(string name) =>
            name.Length >= 3 && (wanted.Contains(name, StringComparison.Ordinal) || name.Contains(wanted, StringComparison.Ordinal));
        return Same(names.Primary) || Same(names.Common) || Same(names.Full) ? 0
            : Overlaps(names.Primary) ? 1
            : Overlaps(names.Common) ? 2
            : null;
    }

    private (string Primary, string Common, string Full) NormalizedNames(PlayerMeta meta) =>
        _normalizedNames.GetValue(meta, entry => new((
            NormalizeName(entry.Get("name")),
            NormalizeName(entry.Get("commonName")),
            NormalizeName(entry.Get("firstName")) + NormalizeName(entry.Get("lastName"))))).Value;

    /// <summary>Overlays the SBC's advertised edition onto the closest native card.</summary>
    private static JsonObject AuthoredPlayerItem(JsonObject item, JsonObject attributes)
    {
        var name = attributes.Str("name");
        if (name.Length > 0)
        {
            foreach (var key in new[] { "name", "playerName", "commonName", "commonname", "knownAs" })
            {
                item[key] = name;
            }
        }
        var rating = attributes.Int("rating");
        if (rating != 0)
        {
            item["rating"] = rating;
        }
        var position = attributes.Str("position").ToUpperInvariant();
        if (position.Length > 0)
        {
            item["preferredPosition"] = position;
        }
        item["untradeable"] = true;
        item["untradeableCount"] = Math.Max(1, item.Int("untradeableCount"));
        return item;
    }

    /// <summary>One reward in the createRewardFromSbcData shape, or null for an unknown reward type.</summary>
    public JsonObject? AwardJson(JsonObject entry, string setKey) =>
        AwardJson(Reward.From(entry), Game.Sbc.Sets.FirstOrDefault(set => set.Key == setKey));

    private JsonObject? AwardJson(Reward reward, SbcSet? set) => reward.Kind switch
    {
        "PACK" => PackAward(reward, set),
        // FIFA's SBC award parser knows only "coin", "item" and "pack"; "coins" drew a nameless default pack (10-05).
        "COINS" => TemplateLibrary.Core.Object("awards/sbc-currency", new TemplateArgs { ["type"] = "coin", ["value"] = reward.Value }),
        "XP" => TemplateLibrary.Core.Object("awards/sbc-currency", new TemplateArgs { ["type"] = "xp", ["value"] = reward.Value }),
        "PLAYER" => PlayerAward(reward),
        "MANAGER_LEAGUE" => ManagerLeagueCardOf(reward) is { } card ? ManagerLeagueAward(card, reward.Attributes.Str("name", card.Name)) : null,
        _ => null,
    };

    /// <summary>The manager league card a MANAGER_LEAGUE reward names by its native league (<c>attributes.league</c>), or null.</summary>
    private ManagerLeagueCard? ManagerLeagueCardOf(Reward reward) => Game.Cards.ManagerLeagueCardForLeague(reward.Attributes.Int("league"));

    private JsonObject ManagerLeagueAward(ManagerLeagueCard card, string name)
    {
        var item = _fut.Items.BuildConsumable(card.ResourceId, 0, "unassigned");
        var untradeable = Game.Rules.Tradeability.Untradeable(RewardSources.Sbc);
        return TemplateLibrary.Core.Object("awards/sbc-item", new TemplateArgs
        {
            ["resourceId"] = card.ResourceId,
            ["halId"] = card.ResourceId,
            ["untradeable"] = untradeable,
            ["name"] = name,
            ["item"] = _fut.Native.PackItem(_fut.Items.MarkReward(item, RewardSources.Sbc, untradeable)),
            ["rating"] = item.Int("rating"),
        });
    }

    private JsonObject PackAward(Reward reward, SbcSet? set)
    {
        var attributes = reward.Attributes;
        var packId = RewardPackId(attributes);
        var config = Game.Packs.GetPackConfig(packId);
        var award = _fut.Awards.Pack(packId, reward.Count);
        // The tile says the tradeability its payout grants (FIFA draws the "(Untradeable)" tag from these keys): the
        // set's repeatable rule or the tradeability lever, exactly what GrantAsync pays the pack.
        var untradeable = RewardsUntradeable(set) || Game.Rules.Tradeability.Untradeable(RewardSources.Sbc, packId);
        award["untradeable"] = untradeable;
        award["isUntradeable"] = untradeable;
        if (JsonValues.HasValue(config.Get("player_pick_count")))
        {
            var minRating = config.Int("min_rating");
            award.Update(TemplateLibrary.Core.Object("awards/sbc-player-pick", new TemplateArgs
            {
                ["name"] = RuleText.Format(Rules.PlayerPickRewardName, ("rating", minRating)),
                ["choiceCount"] = config.Int("player_pick_count", 4),
                ["minRating"] = minRating,
            }));
        }
        else if (JsonValues.HasValue(attributes.Get("name")))
        {
            award.Update(TemplateLibrary.Core.Object("awards/sbc-pack-name", new TemplateArgs { ["name"] = JsonValues.Text(attributes.Get("name")) }));
        }
        return award;
    }

    private JsonObject PlayerAward(Reward reward)
    {
        var attributes = reward.Attributes;
        var resourceId = PlayerResourceId(reward.Entry);
        var meta = Game.Players.Find(resourceId);
        return TemplateLibrary.Core.Object("awards/sbc-player", new TemplateArgs
        {
            ["resourceId"] = resourceId,
            ["name"] = JsonValues.Str(attributes.GetOr("name", Game.Players.Find(resourceId)?.Get("name") ?? JsonValue.Create(string.Empty))),
            ["item"] = resourceId != 0
                ? _fut.Native.PlayerItem(AuthoredPlayerItem(_fut.Items.BuildPlayer(resourceId, 0, "unassigned"), attributes))
                : new JsonObject(),
            ["rating"] = attributes.IntKeep("rating", meta?.Rating ?? 0),
            ["position"] = attributes.Str("position", meta?.Position ?? string.Empty),
        });
    }

    private List<JsonObject> Awards(JsonObject source, SbcSet? set) =>
        Reward.In(source).Select(reward => AwardJson(reward, set)).OfType<JsonObject>().ToList();

    /// <summary>The flat reward block SBCRewards.big draws tiles from (not the nested awards array).</summary>
    private JsonObject RewardTiles(IReadOnlyList<JsonObject> awards, bool completed)
    {
        var packs = awards.Where(award => award.Text("type") == "pack").ToList();
        var items = awards.Where(award => award.Text("type") is "item" or "player").ToList();
        var artIds = packs.Select(award => JsonValues.Text(award.GetOr("packAssetId", Game.Packs.Rules.StoreArtId(award.Int("packId"), Game.Packs.GetPackConfig(award.Int("packId")))))).ToList();
        return TemplateLibrary.Core.Object("sbc/reward-tiles", new TemplateArgs
        {
            ["coins"] = awards.Where(award => award.Text("type") == "coin").Sum(award => award.Int("value")),
            ["packs"] = string.Join(", ", packs.Select(award => JsonValues.Text(award.GetOr("name", "Pack")))),
            ["packsAmount"] = packs.Sum(award => award.Int("count", 1)),
            ["packAssetIds"] = string.Join(",", artIds),
            ["packAssetId"] = artIds.Count > 0 ? long.Parse(artIds[0], CultureInfo.InvariantCulture) : 0,
            ["items"] = string.Join(", ", items.Select(award => JsonValues.Text(award.GetOr("name", string.Empty)))),
            ["itemsAmount"] = items.Count,
            ["itemData"] = new JsonArray(items.Where(award => award.Obj("itemData") is { Count: > 0 }).Select(award => award["itemData"]!.DeepClone()).ToArray()),
            ["completed"] = completed,
        });
    }

    /// <summary>
    /// Translates authored rules to elgReq: each slot carries the rule key/value, an optional
    /// PLAYER_COUNT and a SCOPE.
    /// </summary>
    public JsonArray EligibilityRequirements(SbcChallenge challenge)
    {
        var output = new JsonArray();
        var slot = 0;
        void Add(SbcEligibilityKey key, long value) =>
            output.Add(TemplateLibrary.Core.Object("sbc/eligibility", new TemplateArgs { ["key"] = (long)key, ["slot"] = slot, ["value"] = value }));
        foreach (var requirement in Requirements(challenge).Where(requirement => requirement.Key is not null))
        {
            slot++;
            Add(requirement.Key!.Value, requirement.Value);
            if (requirement.PlayerCount > 0 || requirement.Key is SbcEligibilityKey.LeagueId or SbcEligibilityKey.NationId or SbcEligibilityKey.ClubId or SbcEligibilityKey.Rarity or SbcEligibilityKey.RarityGroup or SbcEligibilityKey.Quality)
            {
                Add(SbcEligibilityKey.PlayerCount, requirement.PlayerCount);
            }
            Add(SbcEligibilityKey.Scope, (long)requirement.Scope);
        }
        // No slot names the player count alone: FIFA lists "Number of players in the Squad" itself, and a PLAYER_COUNT slot
        // with no rule beside it counts the players matching nothing, so it never turned green ("Players: Exactly 1" on
        // First Exchange, 10-03). Submit checks the count separately.
        return output;
    }

    /// <summary>The authored requirement rules this server understands (SbcText); ignored rule types are neither shown nor checked.</summary>
    private IEnumerable<SbcRequirement> Requirements(SbcChallenge challenge) => SbcText.Requirements(Rules, challenge.Source);

    /// <summary>Human-readable requirement summary used as the challenge bio.</summary>
    public string Describe(SbcChallenge challenge) => SbcText.Describe(Rules, challenge.Source);

    private string SetDescription(SbcSet set)
    {
        var description = SbcText.SetDescription(Rules, set.Source);
        var limit = CompletionLimit(set);
        var labels = TemplateLibrary.Core.Object("sbc/completion-limit");
        var label = labels.Text(!set.Source.Bool("repeatable") ? "totalOnce"
            : IsDaily(set) ? limit == 1 ? "dailyOnce" : "daily"
            : limit <= 0 ? "unlimited" : "total")
            .Replace("{limit}", limit.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(description) ? label : $"{description}\n{label}";
    }

    private long RequiredPlayers(SbcChallenge challenge) => SbcText.RequiredPlayers(Rules, challenge.Source);

    private long ImageId(long setId) => Rules.Images.ImageIdBase + (setId - SbcCatalog.FirstSetId);

    /// <summary>The set whose tile art has this image id, or null.</summary>
    public SbcSet? SetForImage(long imageId) =>
        Game.Sbc.FindSet((int)(SbcCatalog.FirstSetId + imageId - Rules.Images.ImageIdBase));

    /// <summary>
    /// A set tile's pre-rendered badge: the set's own art, else its tab's, else the fallback tab's.
    /// EA no longer hosts FIFA 21's set images, and a JSON reply left the tile spinning.
    /// </summary>
    public byte[]? SetImage(long imageId)
    {
        var folder = Path.Combine(Game.Paths.DataRoot, Rules.Images.ArtFolder);
        var set = SetForImage(imageId);
        var candidates = new List<string>();
        if (set is not null)
        {
            // A panel-edited set names its badge: one of the art files.
            if (set.Source.Text("badge") is { Length: > 0 } badge) candidates.Add(ArtFileKey().Replace(badge, "_") + ".png");
            candidates.Add(ArtFileKey().Replace(set.Key, "_") + ".png");
            candidates.Add($"tab-{set.Source.Int("tabId")}.png");
        }
        candidates.Add($"tab-{Rules.Images.FallbackTabId}.png");
        return candidates.Select(name => Path.Combine(folder, name)).Where(File.Exists).Select(File.ReadAllBytes).FirstOrDefault();
    }

    // A repeatable set starts its next round when every challenge has the same completion count.
    // During a round, challenges already ahead of the minimum are complete and cannot pay twice.
    private static long CompletedRounds(SbcSet set, IReadOnlyDictionary<string, long> counts) =>
        set.Challenges.Count == 0 ? 0 : set.Challenges.Min(challenge => counts.GetValueOrDefault(challenge.Key));

    private static long CompletionLimit(SbcSet set) => set.Source.Int("maxCompletions", IsDaily(set) ? 1 : 0);

    private HashSet<string> CurrentCompletions(IReadOnlyDictionary<string, long> counts)
    {
        var done = counts.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var set in Game.Sbc.Sets.Where(set => set.Source.Bool("repeatable")))
        {
            var rounds = CompletedRounds(set, counts);
            if (CompletionLimit(set) is > 0 && rounds >= CompletionLimit(set)) continue;
            foreach (var challenge in set.Challenges)
                if (counts.GetValueOrDefault(challenge.Key) <= rounds) done.Remove(challenge.Key);
        }
        return done;
    }

    private JsonObject SetJson(SbcSet set, HashSet<string> done, IReadOnlySet<int> tagged,
        IReadOnlyDictionary<string, long> counts)
    {
        var challenges = set.Challenges;
        var completed = challenges.Count(challenge => done.Contains(challenge.Key));
        var setDone = challenges.Count > 0 && completed == challenges.Count;
        // The set's own reward (the group reward) is the tile preview; a set without one shows its last
        // challenge's. A multi-part swap pays only as a set reward, so no single part can claim it.
        var setAwards = Awards(set.Source, set);
        var tileAwards = setAwards.Count > 0 ? setAwards
            : challenges.Count > 0 ? Awards(challenges[^1].Source, set) : new List<JsonObject>();
        var json = RewardTiles(tileAwards, setDone);
        json.Update(TemplateLibrary.Core.Object("sbc/set", new TemplateArgs
        {
            ["id"] = set.Id,
            ["tagged"] = tagged.Contains(set.Id),
            ["name"] = JsonValues.Text(set.Source.GetOr("name", string.Empty), "None"),
            ["description"] = SetDescription(set),
            ["priority"] = set.Source.Int("tabId") * 100 + 1,
            ["hidden"] = challenges.Count == 1,
            // Daily limits are described in the bio; their native badge is non-repeatable.
            ["repeatable"] = set.Source.Bool("repeatable") && !IsDaily(set)
                && (CompletionLimit(set) <= 0 || CompletedRounds(set, counts) < CompletionLimit(set)),
            ["challengesCount"] = challenges.Count,
            ["completedCount"] = completed,
            ["timesCompleted"] = set.Source.Bool("repeatable") ? CompletedRounds(set, counts) : setDone ? 1 : 0,
            ["imageId"] = ImageId(set.Id),
            ["awards"] = ClubPresentationService.ToArray(tileAwards),
        }));
        return json;
    }

    private JsonObject ChallengeJson(SbcChallenge challenge, HashSet<string> done, IReadOnlyDictionary<string, long> counts)
    {
        var completed = done.Contains(challenge.Key);
        var awards = Awards(challenge.Source, Game.Sbc.FindSet(challenge.SetId));
        var json = RewardTiles(awards, completed);
        json.Update(TemplateLibrary.Core.Object("sbc/challenge", new TemplateArgs
        {
            ["id"] = challenge.Id,
            ["setId"] = challenge.SetId,
            ["name"] = JsonValues.Text(challenge.Source.GetOr("name", string.Empty), "None"),
            ["description"] = Describe(challenge),
            ["formation"] = Formation(challenge),
            ["priority"] = challenge.Source.Int("order", 1),
            ["status"] = completed ? "COMPLETED" : "NOT_STARTED",
            ["type"] = ChallengeType(Bricks(challenge)),
            ["timesCompleted"] = counts.GetValueOrDefault(challenge.Key),
            ["imageId"] = ImageId(challenge.SetId),
            ["requirements"] = EligibilityRequirements(challenge),
            ["awards"] = ClubPresentationService.ToArray(awards),
        }));
        return json;
    }

    public Task<JsonObject> SetsResponseAsync(CancellationToken cancellationToken = default) =>
        Game.ReadAsync(SetsResponseAsync, cancellationToken);

    private async ValueTask<JsonObject> SetsResponseAsync(IUnitOfWork uow)
    {
        var counts = await CurrentCountsAsync(uow);
        var done = CurrentCompletions(counts);
        var tagged = await TaggedSetsAsync(uow);
        var tabNames = new Dictionary<long, string>();
        foreach (var tab in (Game.Sbc.Root.Arr("tabs") ?? new JsonArray()).OfType<JsonObject>())
        {
            tabNames[tab.IntKeep("id")] = JsonValues.Text(tab.GetOr("name", $"Tab {JsonValues.Text(tab.Get("id"), "None")}"), "None");
        }
        // A tab whose sets are all hidden is not listed.
        var categories = Game.Sbc.Sets
            .Where(set => !IsHidden(set))
            .GroupBy(set => set.Source.Int("tabId"))
            .OrderBy(group => group.Key)
            .Select(group => (JsonNode)TemplateLibrary.Core.Object("sbc/category", new TemplateArgs
            {
                ["id"] = group.Key,
                ["name"] = tabNames.GetValueOrDefault(group.Key, $"Tab {group.Key}"),
                ["sets"] = new JsonArray(group.Select(set => (JsonNode)SetJson(set, done, tagged, counts)).ToArray()),
            }));
        return new JsonObject { ["categories"] = new JsonArray(categories.ToArray()) };
    }

    /// <summary>Favourite sets are club flags named by this prefix and the set id.</summary>
    private const string TaggedSetFlagPrefix = "sbc_tagged_set:";

    private static async ValueTask<HashSet<int>> TaggedSetsAsync(IUnitOfWork uow) =>
        (await uow.FlagsWithPrefixAsync(TaggedSetFlagPrefix))
            .Select(flag => int.TryParse(flag.AsSpan(TaggedSetFlagPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id != 0).ToHashSet();

    /// <summary>
    /// PUT/POST /sbs/sets/tag favourites or unfavourites sets ({"sets":[{"setId":904500,"tagged":true}]}); every method
    /// answers with the favourite sets, which the set list also reports as taggedByUser. Unknown set ids are ignored.
    /// </summary>
    public Task<JsonObject> TagSetsAsync(JsonNode? payload, bool write, CancellationToken cancellationToken = default) =>
        Game.RunAsync("sbc-tag-sets", async uow =>
        {
            if (write && (payload as JsonObject)?.Arr("sets") is { } entries)
            {
                await uow.LockClubAsync();
                var add = new List<string>();
                var remove = new List<string>();
                foreach (var entry in entries.OfType<JsonObject>())
                {
                    if (!JsonValues.TryInt(entry.Get("setId"), out var setId) || setId is < 1 or > int.MaxValue || Game.Sbc.FindSet((int)setId) is null)
                    {
                        continue;
                    }
                    var flag = TaggedSetFlagPrefix + setId.ToString(CultureInfo.InvariantCulture);
                    (entry.Bool("tagged", true) ? add : remove).Add(flag);
                }
                if (add.Count > 0) await uow.SetFlagsAsync(add);
                if (remove.Count > 0) await uow.ClearFlagsAsync(remove);
            }
            return TemplateLibrary.Core.Object("sbc/tagged-sets", new TemplateArgs
            {
                ["sets"] = new JsonArray((await TaggedSetsAsync(uow)).Order()
                    .Select(id => (JsonNode)new JsonObject { ["setId"] = id, ["tagged"] = true }).ToArray()),
            });
        }, cancellationToken);

    public Task<JsonObject> ChallengesResponseAsync(int setId, CancellationToken cancellationToken = default) =>
        Game.ReadAsync(uow => ChallengesResponseAsync(uow, setId), cancellationToken);

    private async ValueTask<JsonObject> ChallengesResponseAsync(IUnitOfWork uow, int setId)
    {
        var counts = await CurrentCountsAsync(uow);
        var done = CurrentCompletions(counts);
        var challenges = Game.Sbc.FindSet(setId) is { } set && !IsHidden(set) ? set.Challenges : Array.Empty<SbcChallenge>();
        return new JsonObject { ["challenges"] = new JsonArray(challenges.Select(challenge => (JsonNode)ChallengeJson(challenge, done, counts)).ToArray()) };
    }

    /// <summary>The client's squad buffer: CardsDLL's save request (vtable 0x4635b0) holds 23 item ids and dream flags.</summary>
    private const int SbcSquadBuffer = 23;

    /// <summary>Brick ids span brickItemIdBase + resource id; resource ids stay far below this span.</summary>
    private const long BrickItemIdSpan = 100000000;

    private readonly ConditionalWeakTable<SbcChallenge, StrongBox<SbcBricks?>> _bricks = new();

    // Keyed by the player catalogue itself, so a content reload resolves placeholders against the new cards.
    private readonly ConditionalWeakTable<object, ConcurrentDictionary<SbcBrickDummy, long>> _dummyCards = new();

    /// <summary>This challenge's locked slots (bricks), or null when every slot is free.</summary>
    private SbcBricks? Bricks(SbcChallenge challenge) =>
        _bricks.GetValue(challenge, entry => new(SbcBricks.Parse(challenge.Source, Rules.SquadSize))).Value;

    /// <summary>The challenge's formation: its authored (retail) formation when FIFA has slots for it, else the default.</summary>
    public string Formation(SbcChallenge challenge) =>
        challenge.Source.Str("formation") is { Length: > 0 } formation && Game.Chemistry.Formations.ContainsKey(formation) ? formation : Rules.Formation;

    private long BrickItemId(long resourceId) => Rules.BrickItemIdBase + resourceId;

    /// <summary>The rareflag FIFA reads as a custom brick card (eCardRarity.CUSTOM_BRICK_CARD).</summary>
    private const int CustomBrickRareflag = 29;

    private bool IsBrickItemId(long itemId) => itemId >= Rules.BrickItemIdBase && itemId < Rules.BrickItemIdBase + BrickItemIdSpan;

    /// <summary>
    /// The card a locked slot carries: an authored player, or for a retail placeholder the lowest-rated common card of its
    /// league and nation, else its lowest-rated non-special one (what the placeholder contributes to chemistry). Null for an empty locked slot.
    /// </summary>
    private long? BrickResource(SbcBricks bricks, int index)
    {
        if (!bricks.IsBrick(index))
        {
            return null;
        }
        if (bricks.CustomPlayers.TryGetValue(index, out var resourceId))
        {
            return resourceId;
        }
        if (!bricks.Dummies.TryGetValue(index, out var dummy))
        {
            return null;
        }
        var cards = _dummyCards.GetValue(Game.Players, _ => new ConcurrentDictionary<SbcBrickDummy, long>());
        var found = cards.GetOrAdd(dummy, wanted => Game.Players.Entries
            .Where(entry => entry.Value.Int("leagueId") == wanted.League && entry.Value.Int("nation") == wanted.Nation
                && !entry.Value.IsSpecial && !Game.Cards.IsGrantOnly(entry.Key))
            .OrderBy(entry => entry.Value.Rareflag).ThenBy(entry => entry.Value.Rating).ThenBy(entry => entry.Key)
            .Select(entry => entry.Key)
            .FirstOrDefault());
        return found > 0 ? found : null;
    }

    /// <summary>
    /// The challenge's "type" string. CardsDLL's challenge parser (0x36fbd0) maps OPEN_CHALLENGE to 0, BRICK_CHALLENGE to 2
    /// (the FUT_BRICK_WALL_CHALLENGE screen: locked empty slots) and CUSTOM_BRICK_CHALLENGE to 3; a "BRICK" slot is only
    /// honoured on a brick challenge, so without this the client never draws a locked slot.
    /// </summary>
    private string ChallengeType(SbcBricks? bricks) => bricks is null ? "OPEN_CHALLENGE"
        : Enumerable.Range(0, (int)Rules.SquadSize).Any(index => BrickResource(bricks, index) is not null) ? "CUSTOM_BRICK_CHALLENGE" : "BRICK_CHALLENGE";

    private string PlayerType(SbcBricks? bricks, int index) => bricks is null || bricks.IsFree(index) ? "DEFAULT"
        : BrickResource(bricks, index) is null ? "BRICK" : "CUSTOM_BRICK";

    /// <summary>
    /// A slot's own elgReq. CardsDLL's playerRequirements parser (0x2eb09a) reads index, playerType and elgReq; the elgReq
    /// entry parser (0x2ea780) keeps only NATION_ID, LEAGUE_ID and CLUB_ID (eligibilitySlot is read and dropped). The SBC
    /// screen (futSbcSquads.big) hands them to the placeholder card (FutPlayerItemSmall.setCustomBrickData), which shows the
    /// served card's own league and nation text only where an id is set and "N/A" elsewhere, so a placeholder with an empty
    /// elgReq showed N/A everywhere. Only a placeholder that serves a card (CUSTOM_BRICK) carries them, and they always
    /// match that card: it is drawn from the same league and nation.
    /// </summary>
    private JsonArray SlotEligibility(SbcBricks? bricks, int index)
    {
        if (bricks is null || !bricks.IsBrick(index) || bricks.CustomPlayers.ContainsKey(index)
            || !bricks.Dummies.TryGetValue(index, out var dummy) || BrickResource(bricks, index) is null)
        {
            return new JsonArray();
        }
        var slot = 0;
        JsonNode Entry(SbcEligibilityKey key, long value) => TemplateLibrary.Core.Object("sbc/eligibility",
            new TemplateArgs { ["key"] = (long)key, ["slot"] = ++slot, ["value"] = value });
        return new JsonArray(Entry(SbcEligibilityKey.LeagueId, dummy.League), Entry(SbcEligibilityKey.NationId, dummy.Nation));
    }

    /// <summary>
    /// The card a locked slot holds, exactly as the client is served it and as submit scores it. FIFA scores squad
    /// chemistry from the squad's items (CardsDLL 0x24b560 walks the squad and never reads the challenge's per-slot
    /// requirements at +0x4f0; the squad-complete check 0x2da537 wants an item in every CUSTOM_BRICK slot), so the served
    /// card is the placeholder. A retail placeholder's card plays in its slot's position (its preferredPosition is the
    /// slot's, as a position modifier would make it), so it links like FUTBIN's league/nation placeholder; its club is the
    /// card's own, and the client and the server both count it. Null for an empty locked slot.
    /// </summary>
    private JsonObject? BrickItem(SbcBricks bricks, string formation, int index)
    {
        if (BrickResource(bricks, index) is not { } resourceId)
        {
            return null;
        }
        var item = BrickPlayerItem(resourceId);
        if (!bricks.CustomPlayers.ContainsKey(index) && SlotPosition(formation, index) is { } position)
        {
            item["preferredPosition"] = position;
        }
        // rareflag 29 makes FIFA's item reader (CardsDLL 0x321910, 0x322eb2) flag the card as a custom brick (item+0xbc):
        // the requirement counters (NATION/LEAGUE/CLUB/SAME_*/PLAYER_COUNT, 0x2da610 and siblings) skip it, as retail
        // Brick Slots needs ("Leagues: Exactly 2" counts only the two placed players), while chemistry (0x24b560) still
        // links it. The reader then swaps the card's id and resourceId for its own negative ones, so it no longer looks
        // the card up and its league, nation and club must ride on the item itself.
        var card = Game.Players.Find(resourceId);
        item["rareflag"] = CustomBrickRareflag;
        item["leagueId"] = card?.Int("leagueId") ?? item.Int("leagueId");
        item["nation"] = card?.Int("nation") ?? item.Int("nation");
        item["teamid"] = card?.Int("teamid") ?? item.Int("teamid");
        return item;
    }

    /// <summary>
    /// The base position of a formation slot ("CB" for an RCB slot, "CM" for an LCM slot): the side variants read the fit
    /// table as their central position, so a card with this preferred position fits its slot fully.
    /// </summary>
    private string? SlotPosition(string formation, int index)
    {
        var chemistry = Game.Chemistry;
        if (!chemistry.Formations.TryGetValue(formation, out var slots) || index < 0 || index >= slots.Count) return null;
        var slot = slots[index];
        var central = chemistry.PositionGroups.GetValueOrDefault(slot.ToString(CultureInfo.InvariantCulture), slot);
        return central >= 0 && central < chemistry.Positions.Count ? chemistry.Positions[central] : null;
    }

    /// <summary>A locked slot's pre-placed card: a full native item shape on a synthetic id that nothing owns or saves.</summary>
    private JsonObject BrickPlayerItem(long resourceId)
    {
        var item = _fut.Native.PlayerItem(_fut.Items.BuildPlayer(resourceId, BrickItemId(resourceId), "club"));
        item["untradeable"] = true;
        item["owners"] = 1;
        item["loyaltyBonus"] = 0;
        return item;
    }

    private static JsonObject ZeroedSquadPlayer(int index) => new()
    {
        ["index"] = index,
        ["itemData"] = new JsonObject
        {
            ["id"] = 0, ["timestamp"] = 0, ["untradeable"] = false, ["assetId"] = 0, ["rating"] = 0,
            ["itemType"] = "player", ["resourceId"] = 0, ["owners"] = 0, ["discardValue"] = 0,
            ["itemState"] = "invalid", ["cardsubtypeid"] = 0, ["lastSalePrice"] = 0, ["morale"] = 0,
            ["fitness"] = 0, ["injuryGames"] = 0, ["preferredPosition"] = "any", ["statsList"] = new JsonArray(),
            ["lifetimeStats"] = new JsonArray(), ["training"] = 0, ["contract"] = 0, ["suspension"] = 0,
            ["attributeList"] = new JsonArray(), ["teamid"] = 0, ["rareflag"] = 0, ["loyaltyBonus"] = 1,
            ["pile"] = 0, ["nation"] = 0,
        },
    };

    /// <summary>
    /// The squad the client builds when entering a challenge (also GET .../squad). playerRequirements must hold every
    /// squad slot: the constructor dereferences every entry without a guard. Locked slots (bricks) say "BRICK" or
    /// "CUSTOM_BRICK" and ride in squad.players with a full item shape on a synthetic id (never owned, filtered from
    /// saves), so the client renders retail brick walls: only the free slots are fillable and only they are submitted.
    /// </summary>
    public JsonObject EmptySquad(long challengeId)
    {
        var challenge = challengeId >= 1 && challengeId <= int.MaxValue ? Game.Sbc.FindChallenge((int)challengeId) : null;
        var bricks = challenge is null ? null : Bricks(challenge);
        var formation = challenge is null ? Rules.Formation : Formation(challenge);
        var players = new JsonArray();
        for (var index = 0; index < SbcSquadBuffer; index++)
        {
            if (bricks is not null && BrickItem(bricks, formation, index) is { } brick)
            {
                players.Add(new JsonObject { ["index"] = index, ["itemData"] = brick });
            }
            else
            {
                players.Add(ZeroedSquadPlayer(index));
            }
        }
        return TemplateLibrary.Core.Object("sbc/empty-squad", new TemplateArgs
        {
            ["challengeId"] = challengeId,
            ["formation"] = formation,
            ["playerRequirements"] = new JsonArray(Enumerable.Range(0, (int)Rules.SquadSize)
                .Select(index => (JsonNode)TemplateLibrary.Core.Object("sbc/player-requirement", new TemplateArgs
                {
                    ["index"] = index,
                    ["playerType"] = PlayerType(bricks, index),
                    ["elgReq"] = SlotEligibility(bricks, index),
                }))
                .ToArray()),
            ["players"] = players,
        });
    }

    /// <summary>
    /// The squad FIFA opens a challenge with (POST /sbs/challenge/{id}, GET .../squad): the club's saved squad for it, so
    /// players placed before backing out are still there, as in retail. FIFA saves the squad when the player leaves the
    /// challenge; a player no longer eligible since (sold, listed, quick sold, used in another SBC) is left out and the
    /// slot stays empty. Locked slots always keep their brick, and a brick wall keeps its own formation.
    /// </summary>
    public Task<JsonObject> SquadAsync(long challengeId, CancellationToken cancellationToken = default) =>
        Game.ReadAsync(uow => SquadAsync(uow, challengeId), cancellationToken);

    private async ValueTask<JsonObject> SquadAsync(IUnitOfWork uow, long challengeId)
    {
        var json = EmptySquad(challengeId);
        if (challengeId is < 1 or > int.MaxValue || Game.Sbc.FindChallenge((int)challengeId) is not { } challenge || IsHidden(challenge)) return json;
        var saved = await uow.SbcSavedSquadAsync(challengeId);
        if (saved.Slots.Count == 0 || json.Obj("squad") is not { } squad || squad.Arr("players") is not { } players) return json;
        var bricks = Bricks(challenge);
        var footballers = new HashSet<long>();
        foreach (var slot in saved.Slots)
        {
            if (slot.Index < 0 || slot.Index >= Rules.SquadSize || slot.Index >= players.Count || bricks?.IsBrick(slot.Index) == true) continue;
            var (item, problem) = await SbcPlayerAsync(uow, slot.ItemId, footballers);
            if (problem is not null) continue;
            players[slot.Index] = new JsonObject { ["index"] = slot.Index, ["itemData"] = _fut.Native.PlayerItem(item!) };
        }
        squad["formation"] = SavedFormation(challenge, saved.Formation ?? string.Empty);
        if (await SbcManagerAsync(uow, saved.ManagerItemId) is { } manager) squad["manager"] = new JsonArray(manager);
        return json;
    }

    /// <summary>
    /// Saves the squad FIFA built. The save body (CardsDLL builder 0x2fbe40) is chemistry, rating, formation,
    /// manager[{id,dream}] and players[{index,itemData{id,dream}}] for the whole 23-slot buffer, empty slots as id 0. The
    /// client's chemistry and rating are never trusted: the server keeps each player's slot, the formation and the manager
    /// so submit can compute them itself. A player in a locked slot or outside the starting eleven is refused.
    /// </summary>
    public Task SaveSquadAsync(long challengeId, JsonNode? payload, CancellationToken cancellationToken = default)
    {
        if (challengeId is < 1 or > int.MaxValue || Game.Sbc.FindChallenge((int)challengeId) is not { } challenge || IsHidden(challenge)) throw new ClubRuleException("SBC_UNKNOWN_CHALLENGE");
        if (payload is not JsonObject document || document.Get("players") is not JsonArray slots || slots.Any(slot=>slot is not JsonObject))
            throw new ClubRuleException("SBC_INVALID_SQUAD");
        var bricks = Bricks(challenge);
        var filled = new List<SbcSquadSlot>();
        for (var position = 0; position < slots.Count; position++)
        {
            var slot = (JsonObject)slots[position]!;
            var id = SbcItemId(slot);
            // A custom brick comes back with the negative id FIFA gave it (CardsDLL 0x322f02: -1 - n), never an owned item.
            if (id <= 0 || IsBrickItemId(id)) continue;
            var index = slot.Get("index") is { } raw && JsonValues.TryInt(raw, out var given) ? given : position;
            // FIFA keeps substitutes and reserves in the SBC buffer (live 10-02: saves with 20 and 23 filled slots, and a
            // First Exchange save with its one player plus a sub). Only the starting slots belong to the challenge.
            if (index < 0 || index >= Rules.SquadSize) continue;
            if (bricks?.IsBrick((int)index) == true) throw new ClubRuleException("SBC_LOCKED_SLOT");
            filled.Add(new SbcSquadSlot((int)index, id));
        }
        var ids = filled.Select(slot => slot.ItemId).ToArray();
        if(ids.Any(id=>id<0)||ids.Distinct().Count()!=ids.Length) throw new ClubRuleException("SBC_DUPLICATE_OR_INVALID_ITEM");
        if (filled.Select(slot => slot.Index).Distinct().Count() != filled.Count) throw new ClubRuleException("SBC_INVALID_SQUAD");
        var formation = SavedFormation(challenge, JsonValues.Str(document.Get("formation"), string.Empty));
        var manager = SavedManagerId(document.Get("manager"));
        return Game.RunAsync("sbc-save-squad", async uow =>
        {
            await uow.LockClubAsync();
            await LoadSbcPlayersAsync(uow,ids);
            await uow.SaveSbcSquadAsync(challengeId,filled,formation,manager);
        }, cancellationToken);
    }

    /// <summary>
    /// Forgets a challenge's saved squad after a refused save, so the next submit is refused with the retail INVALID body
    /// instead of paying for an older squad.
    /// </summary>
    public Task ClearSquadAsync(long challengeId, CancellationToken cancellationToken = default) =>
        challengeId is < 1 or > int.MaxValue || Game.Sbc.FindChallenge((int)challengeId) is null
            ? Task.CompletedTask
            : Game.RunAsync("sbc-clear-squad", uow => uow.SaveSbcSquadItemsAsync(challengeId, Array.Empty<long>()), cancellationToken);

    /// <summary>
    /// The formation chemistry is computed in: a brick wall's locked slots belong to its own formation, so it always uses
    /// that; an open challenge uses the formation FIFA saved when FIFA has slots for it.
    /// </summary>
    private string SavedFormation(SbcChallenge challenge, string saved) =>
        Bricks(challenge) is null && saved.Length > 0 && Game.Chemistry.Formations.ContainsKey(saved) ? saved : Formation(challenge);

    /// <summary>The manager item id FIFA saved (manager is a one-entry array of {id, dream}; an object is accepted too).</summary>
    private static long? SavedManagerId(JsonNode? manager)
    {
        var entry = manager switch { JsonArray list => list.OfType<JsonObject>().FirstOrDefault(), JsonObject single => single, _ => null };
        var id = entry is null ? 0 : JsonValues.Int((entry.Obj("itemData") ?? entry).Get("id"));
        return id > 0 ? id : null;
    }

    /// <summary>
    /// Whether a set's rewards are granted untradeable whatever rules/tradeability.json says for the sbc source: a repeatable
    /// set's are (rules/sbc.json repeatableRewardsUntradeable), unless the set opts out with rewardsTradeable because retail
    /// paid it tradeable.
    /// </summary>
    private bool RewardsUntradeable(SbcSet? set) =>
        set is not null && Rules.RepeatableRewardsUntradeable && set.Source.Bool("repeatable") && !set.Source.Bool("rewardsTradeable");

    /// <summary>Grants every reward of a challenge or set. A failed reward throws, so the caller's transaction grants none.</summary>
    private async ValueTask<List<JsonObject>> GrantRewardsAsync(IUnitOfWork uow, JsonObject source, string setKey, bool untradeable)
    {
        var granted = new List<JsonObject>();
        foreach (var reward in Reward.In(source))
        {
            granted.AddRange(await GrantAsync(uow, reward, setKey, untradeable));
        }
        return granted;
    }

    /// <summary>Applies one reward and returns the granted items (players, unopened packs).</summary>
    private async ValueTask<List<JsonObject>> GrantAsync(IUnitOfWork uow, Reward reward, string setKey, bool untradeable)
    {
        switch (reward.Kind)
        {
            case "COINS" when reward.Value != 0:
                await uow.AddCreditsToAsync(uow.ClubId, reward.Value);
                break;
            case "XP" when reward.Value != 0:
                await uow.AddObjectivesXpAsync(Game.Rules.Objectives, reward.Value);
                break;
            case "PLAYER" when PlayerResourceId(reward.Entry) is var resourceId and not 0:
                var item = AuthoredPlayerItem(await _fut.Items.MakePlayerAsync(uow, resourceId, "unassigned"), reward.Attributes);
                // A player reward is always untradeable; as a reward it quick-sells for at most the reward-untradeable cap.
                _fut.Items.MarkReward(item, RewardSources.Sbc, untradeable: true);
                await uow.SaveItemAsync(item);
                return new List<JsonObject> { item };
            case "MANAGER_LEAGUE" when ManagerLeagueCardOf(reward) is { } leagueCard:
                // A manager league card (migration 071), tradeable unless rules/tradeability.json makes SBC grants untradeable.
                var card = _fut.Items.MarkReward(await _fut.Items.MakeConsumableAsync(uow, leagueCard.ResourceId, "unassigned"),
                    RewardSources.Sbc, untradeable || Game.Rules.Tradeability.Untradeable(RewardSources.Sbc));
                await uow.SaveItemAsync(card);
                return new List<JsonObject> { card };
            case "PACK":
                var packId = RewardPackId(reward.Attributes);
                // Player picks are delivered unopened too; opening yields the 1-of-N token.
                var packs = new List<JsonObject>();
                for (var copy = 0; copy < Math.Max(1, reward.Count); copy++)
                {
                    await uow.AddUnopenedPackAsync(packId, RewardSources.Sbc, null,
                        untradeable || Game.Rules.Tradeability.Untradeable(RewardSources.Sbc, packId));
                    packs.Add(new JsonObject { ["itemType"] = "pack", ["packId"] = packId });
                }
                return packs;
        }
        return new List<JsonObject>();
    }

    /// <summary>Exposed for tests: grants one challenge's rewards.</summary>
    public Task<List<JsonObject>> GrantChallengeRewardsAsync(SbcChallenge challenge, CancellationToken cancellationToken = default) =>
        Game.RunAsync("sbc-grant-challenge-rewards", uow => GrantRewardsAsync(uow, challenge.Source, challenge.SetKey,
            RewardsUntradeable(Game.Sbc.FindSet(challenge.SetId)!)), cancellationToken);

    /// <summary>Exposed for tests: grants one set's own (group) rewards.</summary>
    public Task<List<JsonObject>> GrantSetRewardsAsync(SbcSet set, CancellationToken cancellationToken = default) =>
        Game.RunAsync("sbc-grant-set-rewards", uow => GrantRewardsAsync(uow, set.Source, string.Empty, RewardsUntradeable(set)),
            cancellationToken);

    /// <summary>The text the client shows for a refused submit: whatever follows the reason-code prefix.</summary>
    private static string SubmitRefusalText(string reason)
    {
        var colon = reason.IndexOf(": ", StringComparison.Ordinal);
        return colon is >= 0 && colon + 2 < reason.Length ? reason[(colon + 2)..] : reason;
    }

    public async Task<FutResult> SubmitAsync(int challengeId, CancellationToken cancellationToken = default)
    {
        var challenge = Game.Sbc.FindChallenge(challengeId);
        if (challenge is null || IsHidden(challenge))
        {
            return new FutResult(404, TemplateLibrary.Core.Object("sbc/unknown-challenge", new TemplateArgs { ["message"] = Rules.Text.UnknownChallenge }));
        }
        long club = 0;
        try
        {
            return await Game.RunAsync("sbc-submit", uow =>
            {
                club = uow.ClubId;
                return SubmitLockedAsync(uow, challenge);
            }, cancellationToken);
        }
        catch (ClubRuleException error)
        {
            // FIFA 21 cannot show why a submit was refused. Its submit handler (CardsDLL 0x180102800) treats any error
            // status as squadSaveError, the fatal CARDS_CB_ERR_INVALID_ARGUMENT popup that throws the player out of the
            // SBC, and any 200 as success; the reply parser reads neither "status" nor "message". So a refusal answers
            // the 200 INVALID body and the player stays on the squad with nothing shown. Retail relied on the client
            // greying out Submit, so the server's rules must agree with the client's own requirement check, and this
            // line (with the club) is the only record of a refusal. The transaction above rolled back, so a retry works.
            _log.LogWarning("SBC SUBMIT REFUSED club={Club} challenge={Key} challengeId={Challenge} reason={Reason}",
                club, challenge.Key, challengeId, error.Message);
            return FutResult.Ok(TemplateLibrary.Core.Object("sbc/short-squad", new TemplateArgs
            {
                ["challengeId"] = challengeId,
                ["setId"] = challenge.SetId,
                ["message"] = SubmitRefusalText(error.Message),
            }));
        }
        catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
        {
            // Rolled back: no reward, completion marker or objective progress remains, so a retry can succeed.
            _log.LogError(error, "SBC_SUBMIT_FAILED challengeId={Challenge} rolledBack=true", challengeId);
            return FutResult.Coded(HttpStatusCode.InternalServerError, $"local SBC reward grant failed: {error.Message}");
        }
    }

    /// <summary>
    /// Completion counts, consumed players, rewards and objective progress share the club transaction.
    /// Non-repeatable challenges pay once; repeatable challenges pay once per completed set round. Only a challenge's
    /// first completion counts for objectives (the sbcs stat); SBC completions earn no Season XP.
    /// </summary>
    private async ValueTask<FutResult> SubmitLockedAsync(IUnitOfWork uow, SbcChallenge challenge)
    {
        await uow.LockClubAsync();
        var set = Game.Sbc.FindSet(challenge.SetId)!;
        var repeatable = set.Source.Bool("repeatable");
        var resetAt = DailyResetAt(DateTimeOffset.FromUnixTimeSeconds(uow.NowSeconds));
        var counts = await CurrentCountsAsync(uow, resetAt);
        var firstCompletion = !(await uow.SbcCompletionCountsAsync()).ContainsKey(challenge.Key);
        var daily = IsDaily(set);
        var roundsBefore = CompletedRounds(set, counts);
        var already = repeatable
            ? counts.GetValueOrDefault(challenge.Key) > roundsBefore || CompletionLimit(set) > 0 && counts.GetValueOrDefault(challenge.Key) >= CompletionLimit(set)
            : counts.ContainsKey(challenge.Key);
        var saved = already ? new SbcSavedSquad(Array.Empty<SbcSquadSlot>(), null, null) : await uow.SbcSavedSquadAsync(challenge.Id);
        var submittedIds = saved.Slots.Select(slot => slot.ItemId).ToArray();
        var players = already ? new List<JsonObject>() : await LoadSbcPlayersAsync(uow,submittedIds);
        if(!already) ValidateSbcRequirements(challenge,players,saved,await SbcManagerAsync(uow,saved.ManagerItemId));

        var granted = new List<JsonObject>();
        var completedNow = false;
        if (!already)
        {
            if (daily)
            {
                completedNow = await uow.ExecuteAsync("""
                    INSERT INTO ultimateteam.sbc_daily_completed(club_id,challenge_key,reset_at,completion_count)
                    VALUES(@club,@key,@reset,1)
                    ON CONFLICT(club_id,challenge_key,reset_at) DO UPDATE
                    SET completion_count=ultimateteam.sbc_daily_completed.completion_count+1
                    WHERE ultimateteam.sbc_daily_completed.completion_count<@limit
                    """, ("key", challenge.Key), ("reset", resetAt), ("limit", CompletionLimit(set))) == 1;
                if (completedNow) await uow.RecordRepeatableSbcCompletionAsync(challenge.Key);
            }
            else completedNow = repeatable ? await uow.RecordRepeatableSbcCompletionAsync(challenge.Key) == 1 : await uow.TryMarkSbcCompletedAsync(challenge.Key);
        }
        if (completedNow)
        {
            await ConsumeSbcPlayersAsync(uow,submittedIds);
            await uow.SaveSbcSquadItemsAsync(challenge.Id,Array.Empty<long>());
            granted.AddRange(await GrantRewardsAsync(uow, challenge.Source, challenge.SetKey, RewardsUntradeable(set)));
            if (firstCompletion)
            {
                await _fut.Objectives.RecordSbcCompletedAsync(uow);
            }
            else
            {
                // Logged like an objectives bump, so a "my SBC did not count" report can be checked against it.
                _log.LogInformation("SBC REPEAT club={Club} challenge={Challenge} completions={Completions} objectives=not counted",
                    uow.ClubId, challenge.Key, (await uow.SbcCompletionCountsAsync()).GetValueOrDefault(challenge.Key));
            }
        }
        counts = await CurrentCountsAsync(uow, resetAt);
        var setDone = repeatable
            ? CompletedRounds(set, counts) > roundsBefore
            : set.Challenges.All(c => counts.ContainsKey(c.Key));
        if (completedNow && setDone)
        {
            granted.AddRange(await GrantRewardsAsync(uow, set.Source, string.Empty, RewardsUntradeable(set)));
        }
        var challengeAwards = Awards(challenge.Source, set);
        var setAwards = setDone ? Awards(set.Source, set) : new List<JsonObject>();
        // A one-challenge set opens its squad straight from the Sets hub (CardsDLL picks GotoSbcSquadSingleChallenge
        // when the set's challengesCount is 1), so after the submit FIFA returns there. Only the challenge hub
        // (futSbcHub) asks for the completed popup, so challenge awards would wait unseen until the next challenge hub
        // opened. The submit handler (0x101f60) goes to the set rewards screen instead when grantedSetAwards has items
        // and grantedChallengeAwards has none, as retail one-challenge sets were paid, so their awards are shown as the set's.
        if (setDone && set.Challenges.Count == 1)
        {
            setAwards.InsertRange(0, challengeAwards);
            challengeAwards = new List<JsonObject>();
        }
        return FutResult.Ok(TemplateLibrary.Core.Object("sbc/submitted", new TemplateArgs
        {
            ["challengeId"] = challenge.Id,
            ["setId"] = challenge.SetId,
            ["awards"] = ClubPresentationService.ToArray(challengeAwards),
            ["setAwards"] = ClubPresentationService.ToArray(setAwards),
            ["credits"] = await uow.CreditsAsync(),
            ["grantedCount"] = granted.Count,
            ["unopenedPacks"] = (await uow.UnopenedPackIdsAsync()).Count,
        }));
    }


    [GeneratedRegex(@"[^A-Za-z0-9_-]+")]
    private static partial Regex ArtFileKey();

}
