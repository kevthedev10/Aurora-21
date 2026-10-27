using System.Text.Json.Serialization;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>The client's SBCEligibilityKey values (shared with FIFA 17).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SbcEligibilityKey>))]
public enum SbcEligibilityKey
{
    Chemistry = 1,
    PlayerCount = 2,
    Quality = 3,
    SameNation = 4,
    SameLeague = 5,
    SameClub = 6,
    NationCount = 7,
    LeagueCount = 8,
    /// <summary>CLUB_COUNT: distinct clubs in the squad (retail "Clubs: Exactly 6").</summary>
    ClubCount = 9,
    /// <summary>
    /// NATION_ID, LEAGUE_ID and CLUB_ID: on a slot's own elgReq (CardsDLL 0x2ea780 keeps only these three) they say which of
    /// a retail placeholder's nation, league and club the SBC screen shows (FutPlayerItemSmall shows the served card's own
    /// value where an id is set, "N/A" elsewhere). Chemistry comes from the served card itself, not from these.
    /// </summary>
    NationId = 10,
    LeagueId = 11,
    ClubId = 12,
    Scope = 13,
    Rarity = 18,
    TeamRating = 19,

    /// <summary>
    /// PLAYER_RARITY_GROUP. FIFA 21 (CardsDLL) labels its value as the loc string Player_Group_&lt;n&gt; (group 4 reads
    /// "UEFA Champions League") and its PLAYER_COUNT check never matches it, so no requirement should use it; "Rare" is
    /// <see cref="Rarity"/> 1 (Player_Rarity_1), which the client matches against the card's rareflag exactly.
    /// </summary>
    RarityGroup = 25,
}

/// <summary>The client's SCOPE values for an eligibility slot.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SbcScope>))]
public enum SbcScope
{
    AtLeast = 0,
    AtMost = 1,
    Exact = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter<SbcBound>))]
public enum SbcBound
{
    Min,
    Max,
    Value,
}

public sealed class SbcExactRequirement
{
    public required SbcScope Scope { get; set; }
    public required string Label { get; set; }
}

/// <summary>How one authored requirement rule type maps to eligibility slots and a description.</summary>
public sealed class SbcRequirementRule
{
    public required SbcBound Bound { get; set; }
    public SbcEligibilityKey? EligibilityKey { get; set; }
    public long? EligibilityValue { get; set; }
    /// <summary>Authored attribute selecting a league, nation, club or player quality.</summary>
    public string? Selector { get; set; }
    public SbcScope Scope { get; set; }
    public long PlayerCount { get; set; }
    public required string Label { get; set; }
    public SbcExactRequirement? Exact { get; set; }
    public Dictionary<string, long> Qualities { get; set; } = new();
}

public sealed class SbcImageRules
{
    public required long ImageIdBase { get; set; }
    public required string ArtFolder { get; set; }
    public required long FallbackTabId { get; set; }
    public required long CacheSeconds { get; set; }
}

public sealed class SbcTextRules
{
    public required string RequirementSeparator { get; set; }
    public required string NoRequirements { get; set; }
    public required string SetDescription { get; set; }
    public required string UnnamedSetDescription { get; set; }
    public required string Challenge { get; set; }
    public required string Challenges { get; set; }
    public required string UnknownChallenge { get; set; }
    public required string ShortSquad { get; set; }
}

/// <summary>rules/sbc.json: how sbc.json requirements and rewards translate to the client contract. A reward pack is named by its id (packId).</summary>
public sealed class SbcRules
{
    public required string PlayerPickRewardName { get; set; }
    public required long SquadSize { get; set; }
    public required string Formation { get; set; }

    /// <summary>Synthetic brick item ids are brickItemIdBase + resource id: below cards.json's conceptItemIdBase, never owned, so a save echoing them back is filtered by range.</summary>
    public long BrickItemIdBase { get; set; }

    /// <summary>Whether a repeatable set's rewards are granted untradeable (a set opts out with sbc.json rewardsTradeable).</summary>
    public bool RepeatableRewardsUntradeable { get; set; }
    public required SbcImageRules Images { get; set; }
    public required Dictionary<string, SbcRequirementRule> Requirements { get; set; }

    /// <summary>Authored requirement rule types dropped from every challenge: neither sent to the client nor checked.</summary>
    public HashSet<string> IgnoredRequirements { get; set; } = new();
    public required SbcTextRules Text { get; set; }

    /// <summary>
    /// Refuses settings FIFA could not show (the panel's SBC Settings stage single values): an empty requirement label or game
    /// text, an ignored requirement type that does not exist, a negative image cache time. The formation and fallback tab are
    /// checked against chemistry.json and sbc.json when the content loads (ContentSources).
    /// </summary>
    public SbcRules Checked()
    {
        foreach (var (type, rule) in Requirements)
        {
            if (string.IsNullOrWhiteSpace(rule.Label) || (rule.Exact is { } exact && string.IsNullOrWhiteSpace(exact.Label)))
                throw new InvalidDataException($"The {type} requirement needs a label.");
        }
        if (IgnoredRequirements.FirstOrDefault(type => !Requirements.ContainsKey(type)) is { } unknown)
            throw new InvalidDataException($"ignoredRequirements names {unknown}, which is not a requirement type.");
        var texts = new[] { Text.RequirementSeparator, Text.NoRequirements, Text.SetDescription, Text.UnnamedSetDescription, Text.Challenge,
            Text.Challenges, Text.UnknownChallenge, Text.ShortSquad, PlayerPickRewardName };
        if (texts.Any(string.IsNullOrEmpty))
            throw new InvalidDataException("SBC game text cannot be empty.");
        if (Images.CacheSeconds < 0)
            throw new InvalidDataException("images.cacheSeconds is 0 or more.");
        return this;
    }
}
