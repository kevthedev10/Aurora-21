using Aurora21.UltimateTeam.Core.Data.Repositories;
using Aurora21.UltimateTeam.Core.Sessions;
using Microsoft.Extensions.Logging;

namespace Aurora21.UltimateTeam.Server.Panel;

public sealed partial class OperatorPanel
{
    private async Task<(int, object)> ResetPlayerClubAsync(ApiRequest request, ClubSummary club, string? discordId)
    {
        if (FlagField(request.Body, "confirm") != true || WholeField(request.Body, "confirmPersonaId") != club.PersonaId)
            throw new PanelError(400, "Confirm deletion of this player's club contents using their persona id.");
        void RequireOffline()
        {
            if (discordId is not null && (issuer.Presence?.IsOnline(discordId) == true
                || matchmaking?.IsInMatch(discordId) == true || matchmaking?.IsSearching(discordId) == true))
                throw new PanelError(409, "The player must leave FIFA 21 before resetting their club.");
        }
        RequireOffline();
        PlayerRegistry.ClubResetLease reset;
        try { reset = players.BeginClubReset(club.PersonaId); }
        catch (InvalidOperationException error) { throw new PanelError(409, error.Message); }
        using (reset)
        {
            RequireOffline();
            var account = await game.UnitOfWork.RunAsync(club.ClubId, "panel-reset-club", async work =>
            {
                // Withdraw first: the market lock order refunds other bidders before clearing the target's balance.
                await work.ClearClubAsync(0);
                await work.SetFlagAsync(ClubRepository.NativeCreationRequiredFlag);
                await work.ExecuteAsync("""
                    INSERT INTO ultimateteam.panel_edits(persona_id,patch,requested_by,result,applied_at)
                    VALUES(@persona,'{"op":"resetClub"}'::jsonb,@by,'ok',now())
                    """, ("persona", club.PersonaId), ("by", "panel " + request.Remote));
                return await work.AccountAsync();
            }, request.Aborted);
            reset.RefreshAccount(account);
            game.Competitions.InvalidateRows();
        }
        _log.LogWarning("PANEL reset club contents and onboarding for persona {Persona}", club.PersonaId);
        return (200, await PlayerDetailAsync(club.PersonaId, request.Aborted));
    }
}
