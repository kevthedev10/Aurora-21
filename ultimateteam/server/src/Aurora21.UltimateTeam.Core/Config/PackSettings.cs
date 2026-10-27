using System.Globalization;
using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Rules;

namespace Aurora21.UltimateTeam.Core.Config;

/// <summary>Pack settings loaded from the relational catalog at startup: every pack, sold or not.</summary>
public sealed class PackSettings
{
    public sealed record PackView(PackRules Rules, JsonObject Config, bool ForSale);
    private sealed record Snapshot(PackRules Rules, JsonObject StorePacks);
    private Snapshot _snapshot;

    public PackSettings(UserConfigStore userConfig, PackRules rules)
    {
        // Existing personal files are retained but cannot override the database catalog.
        UserSettingsPath = userConfig.PathFor("pack_settings.json");
        _snapshot = BuildSnapshot(rules);
    }

    public PackRules Rules => Volatile.Read(ref _snapshot).Rules;
    public string UserSettingsPath { get; }

    /// <summary>The store packs loaded from PostgreSQL, returned as a mutable copy.</summary>
    public JsonObject Defaults() => Volatile.Read(ref _snapshot).StorePacks.Copy();

    private static Snapshot BuildSnapshot(PackRules rules)
    {
        var packs = new JsonObject();
        foreach (var (packId, node) in rules.StorePacks)
        {
            var pack = node!.AsObject().Copy();
            packs[packId] = pack;
        }
        return new Snapshot(rules, new JsonObject { ["next_pack_id"] = rules.NextPackId, ["packs"] = packs });
    }

    public JsonObject Load() => Volatile.Read(ref _snapshot).StorePacks.Copy();
    public JsonObject TokenOffers() => Volatile.Read(ref _snapshot).Rules.TokenOffers.Copy();

    /// <summary>Publish a fully validated PostgreSQL catalog as one immutable runtime snapshot.</summary>
    public void Publish(PackRules rules) => Volatile.Write(ref _snapshot, BuildSnapshot(rules));

    /// <summary>Returns one pack; an unknown ID returns an empty object.</summary>
    public JsonObject GetPackConfig(long packId)
        => Capture(packId).Config;

    /// <summary>One consistent rules/configuration view for a store purchase.</summary>
    public PackView Capture(long packId)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var key = packId.ToString(CultureInfo.InvariantCulture);
        var store = snapshot.StorePacks.Obj("packs")?.Obj(key) ?? snapshot.Rules.TokenOffers.Obj(key);
        var forSale = store is not null && store.Bool("enabled", true) && !store.Bool("admin_only");
        var config = store?.Copy() ?? new JsonObject();
        // An unknown pack stays empty, so callers can refuse it.
        if (config.Count == 0) return new PackView(snapshot.Rules, config, false);
        return new PackView(snapshot.Rules, config, forSale);
    }

    /// <summary>
    /// Whether the store sells this pack: an enabled pack in the lineup that is not grant-only (admin_only). Every other
    /// pack only opens from My Packs, after a reward or a panel grant.
    /// </summary>
    public bool IsForSale(long packId) => Capture(packId).ForSale;

}
