using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>What the weekly competitions' settlements read besides their own tables: staff clubs and previous fields.</summary>
public static class CompetitionPrizeRepository
{
    /// <summary>
    /// The clubs of staff (rules/staff.json): the verified Discord accounts on the panel's maintenance bypass list (migration 063
    /// <c>player_access</c>) when maintenanceBypassIsStaff, plus the listed ids; a club is theirs when its persona is derived from one.
    /// </summary>
    public static async ValueTask<HashSet<long>> StaffClubIdsAsync(this IUnitOfWork work, StaffRules rules)
    {
        var discordIds = rules.DiscordIds.ToList();
        if (rules.MaintenanceBypassIsStaff)
        {
            discordIds.AddRange(await work.QueryAsync(
                "-- query: staff.bypass\nSELECT discord_id FROM ultimateteam.player_access WHERE maintenance_bypass", reader => reader.GetString(0)));
        }
        var personas = discordIds.Distinct().Select(id => PlayerIdentity.Of(id).PersonaId).ToArray();
        if (personas.Length == 0)
        {
            return [];
        }
        return (await work.QueryAsync("-- query: staff.clubs\nSELECT id FROM ultimateteam.clubs WHERE persona_id = ANY(@personas)",
            reader => reader.GetInt64(0), ("personas", personas))).ToHashSet();
    }

}
