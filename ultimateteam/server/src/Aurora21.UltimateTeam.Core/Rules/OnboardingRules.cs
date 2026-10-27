namespace Aurora21.UltimateTeam.Core.Rules;

public sealed class LoanRules
{
    public required long Games { get; set; }
    public required long FirstOfferId { get; set; }
    public required int PlayersPerPosition { get; set; }

    /// <summary>The highest rarity a loan card may have (base cards only: 0 common, 1 rare).</summary>
    public required long MaxRareflag { get; set; }

    /// <summary>How many of a position's cards may be offered (skipping footballers the club owns); also the offer-id stride.</summary>
    public required int OfferIdsPerPosition { get; set; }
    public required List<string> Positions { get; set; }
}

/// <summary>The starter squad club creation grants (local policy; see rules/onboarding.json).</summary>
public sealed class StarterRules
{
    public required string Formation { get; set; }
    public required List<string> Positions { get; set; }
    public required long MinRating { get; set; }
    public required long MaxRating { get; set; }
    public required long TargetRating { get; set; }
    public required int Candidates { get; set; }
    public required long MaxRareflag { get; set; }
    public required long ManagerMaxRating { get; set; }
}

public sealed class KitChoiceRules
{
    public required int HomeChoices { get; set; }
    public required int AwayChoices { get; set; }
}

public sealed class BadgeChoiceRules
{
    public required int Choices { get; set; }
}

/// <summary>rules/onboarding.json: FIFA 21's first-run flow (country, club creation, loan, kits and badge).</summary>
public sealed class OnboardingRules
{
    /// <summary>The country screen's nine nations, in order; its action names a 1-based position in this list.</summary>
    public required List<long> CountryNations { get; set; }

    public required StarterRules Starter { get; set; }
    public required LoanRules Loans { get; set; }
    public required KitChoiceRules Kits { get; set; }
    public required BadgeChoiceRules Badges { get; set; }

    /// <summary>The nation of a country-screen position (1-based), or null when there is no such position.</summary>
    public long? CountryNation(long position) =>
        position >= 1 && position <= CountryNations.Count ? CountryNations[(int)position - 1] : null;
}
