using Npgsql;
using Aurora21.UltimateTeam.Core.Config;
using Aurora21.UltimateTeam.Core.Data.Records;

namespace Aurora21.UltimateTeam.Core.Data.Repositories;

/// <summary>Explicit relational account operations; never accesses the legacy public tables.</summary>
public static class RelationalClubRepository
{
    private const string AccountColumns = "id, persona_id, nucleus_id, display_name, credits, fifa_points, next_item_id, sid, token, established, selected_stadium_id, last_purchase_transaction_id, daily_drop_day, last_match_create_is_draft, last_draft_slot, created_at";

    /// <summary>Identity resolution before an owning club is known. Existing balances and session values are retained.</summary>
    public static async ValueTask<RelationalClubOpenResult> EnsureRelationalClubAsync(this IUnitOfWork work,
        ClubIdentity identity, long initialCredits, string? displayName = null)
    {
        RequireTransaction(work);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCredits);
        var inserted = await work.QueryAsync($"""
            -- query: club.create
            -- The id is drawn only when the persona has no club yet, so the new club can start in its own item id band.
            INSERT INTO ultimateteam.clubs(id, persona_id, nucleus_id, display_name, credits, fifa_points, next_item_id,
                sid, token, established, last_purchase_transaction_id, last_match_create_is_draft, last_draft_slot)
            SELECT n.id, @persona, @nucleus, @name, @credits, 0, @next + (n.id - 1) * @band, @sid, @token, @established, 0, false, 1
            FROM (SELECT nextval(pg_get_serial_sequence('ultimateteam.clubs', 'id')) AS id
                WHERE NOT EXISTS (SELECT 1 FROM ultimateteam.clubs WHERE persona_id = @persona)) n
            ON CONFLICT (persona_id) DO NOTHING RETURNING {AccountColumns}
            """, ReadAccount, ("persona", identity.PersonaId), ("nucleus", identity.NucleusId),
            ("name", displayName), ("credits", initialCredits), ("next", ClubRepository.FirstItemId), ("band", ClubRepository.ItemIdBandSize),
            ("sid", ClubSql.NewSid()), ("token", ClubSql.NewToken()), ("established", work.NowSeconds));
        var account = inserted.Count == 1 ? inserted[0] : (await work.QueryAsync(
            $"SELECT {AccountColumns} FROM ultimateteam.clubs WHERE persona_id = @persona", ReadAccount,
            ("persona", identity.PersonaId))).Single();
        if (account.NucleusId != identity.NucleusId)
            throw new RuntimeSetupException("Existing relational club identity does not match the requested account.");
        return new RelationalClubOpenResult(account, inserted.Count == 1);
    }

    public static async ValueTask<RelationalAccount> RelationalAccountAsync(this IUnitOfWork work) =>
        (await work.QueryAsync($"SELECT {AccountColumns} FROM ultimateteam.clubs WHERE id = @club", ReadAccount)).Single();

    public static ValueTask<long?> RelationalClubIdOfPersonaAsync(this IUnitOfWork work, long personaId) =>
        work.ScalarAsync<long?>("SELECT id FROM ultimateteam.clubs WHERE persona_id = @persona", ("persona", personaId));

    /// <summary>
    /// Takes this club's row lock, once per transaction per club. The lock is held to the end of the
    /// transaction, so a repeat request is answered from <see cref="IUnitOfWork.HeldClubLocks"/> instead
    /// of another round trip. A held shared lock never answers an exclusive request: upgrading is a real
    /// lock change and must reach the database. Returns false when the club row does not exist.
    /// </summary>
    public static async ValueTask<bool> EnsureRelationalClubLockAsync(this IUnitOfWork work, bool exclusive)
    {
        RequireTransaction(work);
        if (work.HeldClubLocks.TryGetValue(work.ClubId, out var heldExclusive) && (heldExclusive || !exclusive))
            return true;
        var exists = await work.ScalarAsync<long?>(exclusive
            ? "-- query: club.lock.exclusive\nSELECT id FROM ultimateteam.clubs WHERE id = @club FOR UPDATE"
            : "-- query: club.lock.shared\nSELECT id FROM ultimateteam.clubs WHERE id = @club FOR SHARE") is not null;
        if (exists) work.HeldClubLocks[work.ClubId] = heldExclusive || exclusive;
        return exists;
    }

    public static async ValueTask LockRelationalClubAsync(this IUnitOfWork work)
    {
        if (!await work.EnsureRelationalClubLockAsync(exclusive: true))
            throw new InvalidOperationException("Relational club does not exist.");
    }

    public static async ValueTask<NativeItemIdRange> ReserveNativeItemIdsAsync(this IUnitOfWork work, int count)
    {
        RequireTransaction(work);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var first = await work.ScalarAsync<long?>("""
            UPDATE ultimateteam.clubs SET next_item_id = next_item_id + @count
            WHERE id = @club AND next_item_id + @count <= @first + id * @band - @reserve RETURNING next_item_id - @count + 1
            """, ("count", count), ("first", ClubRepository.FirstItemId), ("band", ClubRepository.ItemIdBandSize),
            ("reserve", ClubRepository.VirtualItemIdReserve))
            ?? throw new InvalidOperationException("Relational club does not exist, or its item id band is exhausted.");
        return new NativeItemIdRange(new NativeItemId(first), count);
    }

    public static async ValueTask<NativeItemId> NextNativeItemIdAsync(this IUnitOfWork work) =>
        (await work.ReserveNativeItemIdsAsync(1)).First;

    public static async ValueTask<ClubFinancialSummary> RelationalFinancialSummaryAsync(this IUnitOfWork work) =>
        (await work.QueryAsync("""
            SELECT c.credits, c.fifa_points,
                (SELECT count(*) FROM ultimateteam.unopened_packs p WHERE p.club_id = c.id)
            FROM ultimateteam.clubs c WHERE c.id = @club
            """, r => new ClubFinancialSummary(r.GetInt64(0), Math.Max(0, r.GetInt64(1)), checked((int)r.GetInt64(2))))).Single();

    public static async ValueTask<long> AddRelationalCreditsAsync(this IUnitOfWork work, long delta)
    {
        RequireTransaction(work);
        return await work.ScalarAsync<long?>("""
            UPDATE ultimateteam.clubs SET credits = GREATEST(0, credits + @delta)
            WHERE id = @club RETURNING credits
            """, ("delta", delta)) ?? throw new InvalidOperationException("Relational club does not exist.");
    }

    public static async ValueTask<long> AddRelationalFifaPointsAsync(this IUnitOfWork work, long delta)
    {
        RequireTransaction(work);
        return await work.ScalarAsync<long?>("""
            UPDATE ultimateteam.clubs SET fifa_points = GREATEST(0, fifa_points + @delta)
            WHERE id = @club RETURNING fifa_points
            """, ("delta", delta)) ?? throw new InvalidOperationException("Relational club does not exist.");
    }

    public static ValueTask<bool> TrySpendRelationalCreditsAsync(this IUnitOfWork work, long amount) =>
        TrySpendAsync(work, amount, "credits");

    public static ValueTask<bool> TrySpendRelationalFifaPointsAsync(this IUnitOfWork work, long amount) =>
        TrySpendAsync(work, amount, "fifa_points");

    private static async ValueTask<bool> TrySpendAsync(IUnitOfWork work, long amount, string column)
    {
        RequireTransaction(work);
        ArgumentOutOfRangeException.ThrowIfNegative(amount);
        return await work.ExecuteAsync($"UPDATE ultimateteam.clubs SET {column} = {column} - @amount WHERE id = @club AND {column} >= @amount",
            ("amount", amount)) == 1;
    }

    public static ValueTask<int> SetRelationalDisplayNameAsync(this IUnitOfWork work, string name) =>
        work.ExecuteAsync("UPDATE ultimateteam.clubs SET display_name = @name WHERE id = @club AND display_name IS DISTINCT FROM @name", ("name", name));

    public static ValueTask<int> SetRelationalDisplayNameIfMissingAsync(this IUnitOfWork work, string name) =>
        work.ExecuteAsync("UPDATE ultimateteam.clubs SET display_name = @name WHERE id = @club AND (display_name IS NULL OR btrim(display_name) = '')", ("name", name));

    public static ValueTask<int> SetRelationalSelectedStadiumAsync(this IUnitOfWork work, long? nativeStadiumId) =>
        work.ExecuteAsync("UPDATE ultimateteam.clubs SET selected_stadium_id = @stadium WHERE id = @club", ("stadium", nativeStadiumId));

    public static ValueTask<int> SetRelationalDailyDropDayAsync(this IUnitOfWork work, string? day) =>
        work.ExecuteAsync("UPDATE ultimateteam.clubs SET daily_drop_day = @day WHERE id = @club", ("day", day));

    public static ValueTask<int> SetRelationalLastPurchaseTransactionAsync(this IUnitOfWork work, long transactionId) =>
        work.ExecuteAsync("UPDATE ultimateteam.clubs SET last_purchase_transaction_id = @txn WHERE id = @club", ("txn", transactionId));

    public static ValueTask<int> SetRelationalLastMatchCreateIsDraftAsync(this IUnitOfWork work, bool isDraft) =>
        work.ExecuteAsync("UPDATE ultimateteam.clubs SET last_match_create_is_draft = @draft WHERE id = @club", ("draft", isDraft));

    public static ValueTask<int> SetRelationalLastDraftSlotAsync(this IUnitOfWork work, DraftSlot slot)
    {
        if (slot is not (DraftSlot.SinglePlayer or DraftSlot.Online)) throw new ArgumentOutOfRangeException(nameof(slot));
        return work.ExecuteAsync("UPDATE ultimateteam.clubs SET last_draft_slot = @slot WHERE id = @club", ("slot", (short)slot));
    }

    public static ValueTask<bool> RelationalFlagAsync(this IUnitOfWork work, string flag) =>
        work.ScalarAsync<bool>("SELECT EXISTS(SELECT 1 FROM ultimateteam.club_flags WHERE club_id = @club AND flag = @flag)", ("flag", flag));

    public static ValueTask<int> SetRelationalFlagAsync(this IUnitOfWork work, string flag) =>
        work.ExecuteAsync("INSERT INTO ultimateteam.club_flags(club_id, flag) VALUES(@club, @flag) ON CONFLICT (club_id, flag) DO NOTHING", ("flag", flag));

    /// <summary>Which of the named flags this club has, in one statement.</summary>
    public static async ValueTask<HashSet<string>> RelationalFlagsAsync(this IUnitOfWork work, IEnumerable<string> flags) =>
        new(await work.QueryAsync("-- query: club.flags\nSELECT flag FROM ultimateteam.club_flags WHERE club_id = @club AND flag = ANY(@flags)",
            r => r.GetString(0), ("flags", flags.Distinct(StringComparer.Ordinal).ToArray())), StringComparer.Ordinal);

    /// <summary>Sets every named flag in one statement; flags already set are left as they are.</summary>
    /// <summary>This club's flags that start with <paramref name="prefix"/>, in order.</summary>
    public static ValueTask<List<string>> RelationalFlagsWithPrefixAsync(this IUnitOfWork work, string prefix) =>
        work.QueryAsync("-- query: club.flags.prefix\nSELECT flag FROM ultimateteam.club_flags WHERE club_id = @club AND starts_with(flag, @prefix) ORDER BY flag",
            r => r.GetString(0), ("prefix", prefix));

    public static ValueTask<int> ClearRelationalFlagsAsync(this IUnitOfWork work, IEnumerable<string> flags) =>
        work.ExecuteAsync("-- query: club.flags.clear\nDELETE FROM ultimateteam.club_flags WHERE club_id = @club AND flag = ANY(@flags)",
            ("flags", flags.Distinct(StringComparer.Ordinal).ToArray()));

    public static ValueTask<int> SetRelationalFlagsAsync(this IUnitOfWork work, IEnumerable<string> flags) =>
        work.ExecuteAsync("-- query: club.flags.set\nINSERT INTO ultimateteam.club_flags(club_id, flag) SELECT @club, f FROM unnest(@flags) AS f ON CONFLICT (club_id, flag) DO NOTHING",
            ("flags", flags.Distinct(StringComparer.Ordinal).ToArray()));

    private static RelationalAccount ReadAccount(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2),
        r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetInt64(5), new NativeItemId(r.GetInt64(6)),
        r.GetString(7), r.GetString(8), r.GetInt64(9), r.IsDBNull(10) ? null : r.GetInt64(10), r.GetInt64(11),
        r.IsDBNull(12) ? null : r.GetString(12), r.GetBoolean(13), (DraftSlot)r.GetInt16(14), r.GetDateTime(15));

    private static void RequireTransaction(IUnitOfWork work)
    {
        if (!work.InTransaction) throw new InvalidOperationException("Relational writes require a root transaction.");
    }
}
