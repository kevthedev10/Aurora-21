using System.Globalization;
using System.Text.Json.Serialization;

namespace Aurora21.UltimateTeam.Core.Rules;

/// <summary>
/// rules/staff.json: who counts as staff (the verified Discord accounts on the panel's maintenance bypass list, and
/// <see cref="DiscordIds"/>), and the weekly competitions that settle without staff clubs until <see cref="PrizesIneligibleUntil"/>
/// (pack-odds-2026-09 PLAN.md 5.2 and 7.2: staff are ineligible for test-week prizes; a prize passes to the next player).
/// </summary>
public sealed class StaffRules : IJsonOnDeserialized
{
    public const string Rivals = "rivals";
    public const string WeekendLeague = "weekendLeague";
    public const string SquadBattles = "squadBattles";
    private static readonly string[] Competitions = [Rivals, WeekendLeague, SquadBattles];

    public required long Version { get; set; }
    public required bool MaintenanceBypassIsStaff { get; set; }
    public required List<string> DiscordIds { get; set; }
    public required List<string> PrizesIneligibleIn { get; set; }
    public string? PrizesIneligibleUntil { get; set; }

    [JsonIgnore]
    public long? IneligibleUntilSeconds { get; private set; }

    /// <summary>Whether staff clubs are left out of <paramref name="competition"/> when a week or event ending at <paramref name="end"/> settles.</summary>
    public bool Excludes(string competition, long end) =>
        PrizesIneligibleIn.Contains(competition) && (IneligibleUntilSeconds is not { } until || end <= until);

    public void OnDeserialized()
    {
        if (PrizesIneligibleIn.FirstOrDefault(name => !Competitions.Contains(name)) is { } unknown)
        {
            throw new InvalidDataException($"prizesIneligibleIn: {unknown} is not one of {string.Join(", ", Competitions)}");
        }
        if (DiscordIds.FirstOrDefault(id => id.Length is < 5 or > 32 || !id.All(char.IsAsciiDigit)) is { } bad)
        {
            throw new InvalidDataException($"discordIds: {bad} is not a 5-32 digit Discord id");
        }
        if (PrizesIneligibleUntil is { } text)
        {
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until))
            {
                throw new InvalidDataException($"prizesIneligibleUntil: {text} is not an ISO 8601 time");
            }
            IneligibleUntilSeconds = until.ToUnixTimeSeconds();
        }
    }
}
