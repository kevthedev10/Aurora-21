using System.Text.Json.Nodes;
using Aurora21.UltimateTeam.Core.Data;
using Aurora21.UltimateTeam.Core.Helpers.Json;
using Aurora21.UltimateTeam.Core.Items;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Api;

/// <summary>
/// Kits drawn in mixed packs before PR #87 were saved with category 9, which the item extension kept as an override, so
/// FIFA showed them as blank "KIT" cards (live 10-05). Migration 161 drops the override.
/// </summary>
public sealed class KitCategoryOverrideTests
{
    [Fact]
    public void MigrationRestoresTheCategoryOfKitsSavedWithCategoryNine()
    {
        using var rt = TestRuntime.Create(marketTarget: 0);
        var store = rt.Game.Store();
        var definition = rt.Game.Kits.Kits.First();
        var kit = ItemFactory.KitItem(store.NextItemId(), rt.Game.NowSeconds, definition.IntKeep("resourceId"), "free", "club",
            definition.Int("teamid"), ItemFactory.KitType(definition), 9, definition.Text("name"));
        store.SaveItem(kit);
        Assert.Equal(9, store.GetItem(kit.Int("id"))!.Int("category"));

        rt.Game.InUnit(async work =>
        {
            await work.ExecuteAsync("DELETE FROM ultimateteam.schema_migrations WHERE version=161");
            using var stream = typeof(RelationalSchemaBootstrap).Assembly.GetManifestResourceStream("DatabaseSetup/migrations/161-kit-category-overrides.sql")!;
            using var reader = new StreamReader(stream);
            await work.ExecuteAsync(await reader.ReadToEndAsync());
            return 0;
        });

        Assert.Equal(2 + definition.Int("kitType"), store.GetItem(kit.Int("id"))!.Int("category"));
    }
}
