using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>
/// Relational onboarding and client-data state (onboarding, onboarding_kit_choices,
/// onboarding_stale_loans, client_data). Loan, badge and stale-loan numbers are retained
/// club-local wire values; kit choices resolve to typed definitions and owned items.
/// </summary>
internal static class OnboardingRepository
{
    private const int WireYear = 2021;
    private const string HomeKitKey = "homeKitId";
    private const string AwayKitKey = "awayKitId";
    private static readonly (string Role, string Key, string State)[] Roles =
        { ("home", HomeKitKey, "activeHomeKit"), ("away", AwayKitKey, "activeAwayKit") };

    /// <summary>One statement, so the header and its ordered children are one consistent snapshot.</summary>
    internal static async ValueTask<OnboardingState> RelationalOnboardingAsync(this IUnitOfWork w)
    {
        var rows = await w.QueryAsync("""
            SELECT o.complete, o.badge_choice, o.badge_item_id, o.loan_offer_id, o.loan_item_id, o.loan_resource_id,
                   ARRAY(SELECT k.role FROM ultimateteam.onboarding_kit_choices k WHERE k.club_id = o.club_id ORDER BY k.ordinal),
                   ARRAY(SELECT k.ordinal FROM ultimateteam.onboarding_kit_choices k WHERE k.club_id = o.club_id ORDER BY k.ordinal),
                   ARRAY(SELECT d.native_resource_id FROM ultimateteam.onboarding_kit_choices k
                         JOIN ultimateteam.item_definitions d ON d.definition_id = k.definition_id
                         WHERE k.club_id = o.club_id ORDER BY k.ordinal),
                   ARRAY(SELECT i.native_item_id FROM ultimateteam.onboarding_kit_choices k
                         LEFT JOIN ultimateteam.items i ON i.id = k.item_id AND i.club_id = k.club_id
                         WHERE k.club_id = o.club_id ORDER BY k.ordinal),
                   ARRAY(SELECT s.wire_item_id FROM ultimateteam.onboarding_stale_loans s WHERE s.club_id = o.club_id ORDER BY s.ordinal),
                   o.nation_id, o.club_name, o.club_abbr
            FROM ultimateteam.onboarding o WHERE o.club_id = @club
            """,
            r => (Complete: r.GetBoolean(0), Badge: r.GetInt64(1), BadgeItem: r.GetInt64(2), Offer: r.GetInt64(3),
                LoanItem: r.GetInt64(4), LoanResource: r.GetInt64(5), Roles: r.GetFieldValue<string[]>(6),
                Ordinals: r.GetFieldValue<int[]>(7), Resources: r.GetFieldValue<long?[]>(8),
                Items: r.GetFieldValue<long?[]>(9), Stale: r.GetFieldValue<long[]>(10),
                Nation: r.IsDBNull(11) ? 0 : r.GetInt64(11), ClubName: r.IsDBNull(12) ? null : r.GetString(12),
                ClubAbbr: r.IsDBNull(13) ? null : r.GetString(13)));
        if (rows.Count == 0)
        {
            return new OnboardingState();
        }
        var row = rows[0];
        if (row.Roles.Length != 0 && (row.Roles.Length != Roles.Length
            || row.Roles.Where((role, index) => role != Roles[index].Role || row.Ordinals[index] != index).Any()
            || row.Resources.Any(resource => resource is null)))
        {
            throw new NotSupportedException("Persisted onboarding kit choices are not an ordered home/away pair; audit required.");
        }
        var kitItems = new JsonObject();
        for (var index = 0; index < row.Items.Length; index++)
        {
            // A NULL reference is a virtual default or a since-deleted owned kit; no owned number exists to report.
            if (row.Items[index] is { } native)
            {
                kitItems[Roles[index].Key] = native;
            }
        }
        return new OnboardingState
        {
            NationId = row.Nation,
            ClubName = row.ClubName,
            ClubAbbr = row.ClubAbbr,
            Complete = row.Complete,
            BadgeChoice = row.Badge,
            BadgeItemId = row.BadgeItem,
            LoanOfferId = row.Offer,
            LoanItemId = row.LoanItem,
            LoanResourceId = row.LoanResource,
            KitChoices = row.Resources.Select(resource => resource!.Value).ToArray(),
            KitItemIds = kitItems,
            StaleLoanItemIds = row.Stale,
        };
    }

    /// <summary>Replaces the club's onboarding state in the caller's transaction, under the club lock.</summary>
    internal static async ValueTask<int> SaveRelationalOnboardingAsync(this IUnitOfWork w, OnboardingState state, IReadOnlyList<JsonObject> virtualDefaults)
    {
        await w.LockRelationalClubAsync();
        var kits = await ResolveKitChoicesAsync(w, state, virtualDefaults);
        await w.ExecuteAsync("""
            INSERT INTO ultimateteam.onboarding(club_id, complete, badge_choice, badge_item_id, loan_offer_id, loan_item_id, loan_resource_id,
                nation_id, club_name, club_abbr)
            VALUES(@club, @complete, @badge, @badgeItem, @offer, @loanItem, @loanResource, @nation, @clubName, @clubAbbr)
            ON CONFLICT (club_id) DO UPDATE SET complete = excluded.complete, badge_choice = excluded.badge_choice,
                badge_item_id = excluded.badge_item_id, loan_offer_id = excluded.loan_offer_id,
                loan_item_id = excluded.loan_item_id, loan_resource_id = excluded.loan_resource_id,
                nation_id = excluded.nation_id, club_name = excluded.club_name, club_abbr = excluded.club_abbr
            """,
            ("complete", state.Complete), ("badge", state.BadgeChoice), ("badgeItem", state.BadgeItemId),
            ("offer", state.LoanOfferId), ("loanItem", state.LoanItemId), ("loanResource", state.LoanResourceId),
            ("nation", state.NationId == 0 ? null : state.NationId), ("clubName", state.ClubName), ("clubAbbr", state.ClubAbbr));
        await w.ExecuteAsync("DELETE FROM ultimateteam.onboarding_kit_choices WHERE club_id = @club");
        if (kits.Count > 0)
        {
            await w.ExecuteAsync("""
                INSERT INTO ultimateteam.onboarding_kit_choices(club_id, role, ordinal, definition_id, item_id)
                SELECT @club, role, ordinal, definition_id, item_id
                FROM json_to_recordset(@rows) AS x(role text, ordinal integer, definition_id bigint, item_id bigint)
                """, ("rows", RelationalItemRepository.Rows(kits)));
        }
        await w.ExecuteAsync("DELETE FROM ultimateteam.onboarding_stale_loans WHERE club_id = @club");
        if (state.StaleLoanItemIds.Count > 0)
        {
            await w.ExecuteAsync("""
                INSERT INTO ultimateteam.onboarding_stale_loans(club_id, ordinal, wire_item_id)
                SELECT @club, ordinality - 1, id FROM unnest(@ids) WITH ORDINALITY AS s(id, ordinality)
                """, ("ids", state.StaleLoanItemIds.ToArray()));
        }
        return 1;
    }

    /// <summary>
    /// Kit choices are either empty or the ordered home/away pair. Equipped references resolve
    /// by club and must be that role's chosen kit; the role's verified virtual default has no owned row.
    /// </summary>
    private static async ValueTask<List<object>> ResolveKitChoicesAsync(IUnitOfWork w, OnboardingState state, IReadOnlyList<JsonObject> virtualDefaults)
    {
        var references = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (key, value) in state.KitItemIds)
        {
            if (Array.FindIndex(Roles, role => role.Key == key) < 0 || !JsonValues.IsStrictInt(value, out var native) || native <= 0)
            {
                throw new InvalidOperationException("Unsupported onboarding kit reference field; audit required.");
            }
            references[key] = native;
        }
        if (state.KitChoices.Count == 0)
        {
            return references.Count == 0 ? new List<object>()
                : throw new InvalidOperationException("Onboarding kit references require kit choices.");
        }
        if (state.KitChoices.Count != Roles.Length)
        {
            throw new InvalidOperationException("Onboarding kit choices must be one home and one away kit.");
        }
        var definitions = (await w.QueryAsync("""
            SELECT native_resource_id, definition_id FROM ultimateteam.item_definitions
            WHERE game_year = @year AND family_id = 4 AND native_item_type = 'kit' AND native_resource_id = ANY(@resources)
            """, r => (Resource: r.GetInt64(0), Definition: r.GetInt64(1)),
            ("year", WireYear), ("resources", state.KitChoices.Distinct().ToArray())))
            .ToDictionary(d => d.Resource, d => d.Definition);
        var owned = (await w.QueryAsync("""
            SELECT i.native_item_id, i.id, i.definition_id FROM ultimateteam.items i
            JOIN ultimateteam.customisation_items k ON k.item_id = i.id
            WHERE i.club_id = @club AND i.native_item_id = ANY(@ids)
            """, r => (Native: r.GetInt64(0), Id: r.GetInt64(1), Definition: r.GetInt64(2)),
            ("ids", references.Values.Distinct().ToArray())))
            .ToDictionary(i => i.Native);
        var rows = new List<object>();
        foreach (var (role, index) in Roles.Select((role, index) => (role, index)))
        {
            var resource = state.KitChoices[index];
            if (!definitions.TryGetValue(resource, out var definition))
            {
                throw new InvalidOperationException("Onboarding kit choice has no FIFA 21 kit definition.");
            }
            long? item = null;
            if (references.TryGetValue(role.Key, out var native))
            {
                if (owned.TryGetValue(native, out var ownedKit))
                {
                    if (ownedKit.Definition != definition)
                    {
                        throw new InvalidOperationException("Onboarding equipped kit does not match its chosen definition.");
                    }
                    item = ownedKit.Id;
                }
                else if (!virtualDefaults.Any(template => template.Text("itemState") == role.State
                    && template.Int("id") == native && template.Int("resourceId") == resource))
                {
                    throw new InvalidOperationException("Onboarding equipped kit is neither owned by this club nor its verified default.");
                }
            }
            rows.Add(new { role = role.Role, ordinal = index, definition_id = definition, item_id = item });
        }
        return rows;
    }

    /// <summary>
    /// Clears onboarding equipped-kit references to items about to be deleted. The onboarding choice
    /// keeps its definition; the legacy document kept a dangling number that no response reads.
    /// </summary>
    internal static ValueTask<int> DetachOnboardingKitsAsync(this IUnitOfWork w, string itemPredicate, params (string Name, object? Value)[] parameters) =>
        w.ExecuteAsync($"""
            UPDATE ultimateteam.onboarding_kit_choices SET item_id = NULL
            WHERE club_id = @club AND item_id IN (SELECT i.id FROM ultimateteam.items i WHERE i.club_id = @club AND {itemPredicate})
            """, parameters);

    /// <summary>The club name and abbreviation its player typed when FIFA created the club, or null.</summary>
    internal static async ValueTask<(string Name, string Abbreviation)?> ChosenClubIdentityAsync(this IUnitOfWork w)
    {
        var rows = await w.QueryAsync("""
            -- query: onboarding.chosen-club
            SELECT club_name, club_abbr FROM ultimateteam.onboarding
            WHERE club_id = @club AND club_name IS NOT NULL AND club_abbr IS NOT NULL
            """, r => (Name: r.GetString(0), Abbreviation: r.GetString(1)));
        return rows.Count == 0 ? null : rows[0];
    }

    // ---- client data --------------------------------------------------------

    internal static async ValueTask<SortedDictionary<long, long>> RelationalClientDataAsync(this IUnitOfWork w, string kind) =>
        new((await w.QueryAsync("SELECT entry_key, value FROM ultimateteam.client_data WHERE club_id = @club AND kind = @kind",
                r => KeyValuePair.Create(r.GetInt64(0), r.GetInt64(1)), ("kind", kind)))
            .ToDictionary(entry => entry.Key, entry => entry.Value));

    internal static async ValueTask SaveRelationalClientDataAsync(this IUnitOfWork w, string kind, IReadOnlyDictionary<long, long> values)
    {
        await w.LockRelationalClubAsync();
        await w.ExecuteAsync("DELETE FROM ultimateteam.client_data WHERE club_id = @club AND kind = @kind", ("kind", kind));
        if (values.Count > 0)
        {
            await w.ExecuteAsync("""
                INSERT INTO ultimateteam.client_data(club_id, kind, entry_key, value)
                SELECT @club, @kind, unnest(@keys), unnest(@values)
                """, ("kind", kind), ("keys", values.Keys.ToArray()), ("values", values.Values.ToArray()));
        }
    }
}
