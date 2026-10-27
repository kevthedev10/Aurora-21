using Aurora21.UltimateTeam.Core.Catalog;
using Aurora21.UltimateTeam.Core.Data.Records;
using Xunit;

namespace Aurora21.UltimateTeam.Tests.Core;

public sealed class PromoAvailabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = Start.AddHours(1);
    private static readonly DefinitionId BaseGold = new(1);
    private static readonly DefinitionId IconCard = new(2);
    private static readonly DefinitionId Totw = new(3);
    private static readonly DefinitionId AnotherTotw = new(4);
    private static readonly DefinitionId UngroupedSpecial = new(5);
    private static readonly DefinitionId CompetitionCard = new(6);

    [Fact]
    public void OnlyOrdinaryTypesAndCurrentIconsAreStandard()
    {
        var types = Enumerable.Range(1, 6).ToDictionary(id => new DefinitionId(id), id => (long)id);
        types[new DefinitionId(7)] = 7;
        types[IconCard] = 9;
        var availability = new PromoAvailability(types,
            [Promo(10, "base_icons"), Promo(11, "ucl")],
            [Member(1, 10, IconCard), Member(2, 11, new DefinitionId(7))], [], []);

        foreach (var card in Enumerable.Range(1, 6).Select(id => new DefinitionId(id)))
            if (card != IconCard) Assert.True(availability.IsStandard(card));
        Assert.True(availability.IsStandard(IconCard));
        Assert.False(availability.IsStandard(new DefinitionId(7)));
        Assert.True(availability.IsAvailable(IconCard, End));
        Assert.True(availability.IsIcon(IconCard));
        Assert.False(availability.IsAvailable(new DefinitionId(7), Start));
    }

    [Fact]
    public void EventsAddOnlyTheirPromosWithinUtcWindow()
    {
        var availability = Build(
            [Event(1, "first", true, Start.UtcDateTime, End.UtcDateTime),
             Event(2, "overlap", true, Start.AddMinutes(30).UtcDateTime, End.AddMinutes(30).UtcDateTime),
             Event(3, "disabled", false, Start.UtcDateTime, End.UtcDateTime)],
            [new PromoEventPromoRecord(1, 11, true), new PromoEventPromoRecord(2, 12, true),
             new PromoEventPromoRecord(3, 13, true)]);

        Assert.True(availability.IsAvailable(BaseGold, Start.AddDays(-1)));
        Assert.True(availability.IsAvailable(IconCard, End.AddDays(1)));
        Assert.False(availability.IsAvailable(Totw, Start.AddTicks(-1)));
        Assert.True(availability.IsAvailable(Totw, Start));
        Assert.True(availability.IsAvailable(Totw, End.AddTicks(-1)));
        Assert.True(availability.IsAvailable(Totw, End));
        Assert.False(availability.IsAvailable(Totw, End.AddMinutes(30)));
        Assert.True(availability.IsAvailable(AnotherTotw, End));
        Assert.False(availability.IsAvailable(AnotherTotw, End.AddMinutes(30)));
        Assert.False(availability.IsAvailable(CompetitionCard, Start.AddMinutes(15)));
        Assert.False(availability.IsAvailable(UngroupedSpecial, Start.AddMinutes(15)));
        Assert.True(availability.IsAvailable(Totw, Start.ToOffset(TimeSpan.FromHours(2))));
        Assert.Equal(new long[] { 10, 11 }, availability.ActivePromoIds(Start.AddMinutes(15)).Order().ToArray());
        Assert.Equal(new long[] { 10, 11, 12 }, availability.ActivePromoIds(Start.AddMinutes(45)).Order().ToArray());
        Assert.Equal(new long[] { 10, 12 }, availability.ActivePromoIds(End).Order().ToArray());
    }

    [Fact]
    public void OverlappingEventsAndPromoMembershipUseAnyActiveRoute()
    {
        var availability = Build(
            [Event(1, "first", false, Start.UtcDateTime, End.UtcDateTime),
             Event(2, "second", true, Start.UtcDateTime, End.UtcDateTime)],
            [new PromoEventPromoRecord(1, 11, true), new PromoEventPromoRecord(2, 12, true)]);

        Assert.True(availability.IsAvailable(Totw, Start));
        Assert.True(availability.HasPromo(Totw));
        Assert.False(availability.IsAvailable(Totw, End.ToOffset(TimeSpan.FromHours(-4))));
        Assert.True(availability.IsAvailable(AnotherTotw, Start));
    }

    [Fact]
    public void OneEventStopsAtItsExclusiveEndAndDisabledLinksDoNotAddCards()
    {
        var availability = Build(
            [Event(1, "only", true, Start.UtcDateTime, End.UtcDateTime)],
            [new PromoEventPromoRecord(1, 11, true), new PromoEventPromoRecord(1, 13, false)]);

        Assert.False(availability.IsAvailable(Totw, Start.AddTicks(-1)));
        Assert.True(availability.IsAvailable(Totw, Start));
        Assert.True(availability.IsAvailable(Totw, End.AddTicks(-1)));
        Assert.False(availability.IsAvailable(Totw, End));
        Assert.False(availability.IsAvailable(CompetitionCard, Start));
    }

    [Fact]
    public void OldPromoTimingFieldsDoNotOverrideAnEvent()
    {
        var oldPromo = Promo(11, "totw_1") with {
            Enabled = false, StartsAt = Start.AddDays(10).UtcDateTime, EndsAt = Start.AddDays(11).UtcDateTime,
        };
        var availability = new PromoAvailability(new Dictionary<DefinitionId, long> { [Totw] = 16 },
            [oldPromo], [Member(1, 11, Totw)],
            [Event(1, "active", true, Start.UtcDateTime, End.UtcDateTime)],
            [new PromoEventPromoRecord(1, 11, true)]);

        Assert.True(availability.IsAvailable(Totw, Start));
    }

    [Fact]
    public void OrdinaryPromoEventDoesNotRotateTheStandardIconTier()
    {
        var midIcon = new DefinitionId(20);
        var availability = new PromoAvailability(
            new Dictionary<DefinitionId, long> { [IconCard] = 9, [midIcon] = 10 },
            [Promo(10, "base_icons"), Promo(14, "mid_icons") with { OddsCategory = "icon" }],
            [Member(1, 10, IconCard), Member(2, 14, midIcon)],
            [Event(1, "ordinary", true, Start.UtcDateTime, End.UtcDateTime)],
            [new PromoEventPromoRecord(1, 14, true)]);

        Assert.True(availability.IsIcon(IconCard));
        Assert.False(availability.IsIcon(midIcon));
        Assert.False(availability.IsAvailable(midIcon, Start));
        Assert.DoesNotContain(14L, availability.ActivePromoIds(Start));
    }

    private static PromoAvailability Build(IReadOnlyList<PromoEventRecord> events,
        IReadOnlyList<PromoEventPromoRecord> eventPromos) => new(
        new Dictionary<DefinitionId, long> {
            [BaseGold] = 5, [IconCard] = 9, [Totw] = 16, [AnotherTotw] = 16,
            [UngroupedSpecial] = 35, [CompetitionCard] = 48,
        },
        [Promo(10, "base_icons"), Promo(11, "totw_1"), Promo(12, "totw_2"), Promo(13, "ucl")],
        [Member(1, 10, IconCard), Member(2, 11, Totw), Member(3, 12, Totw),
         Member(4, 12, AnotherTotw), Member(5, 13, CompetitionCard)],
        events, eventPromos);

    private static PromoRecord Promo(long id, string code) =>
        new(id, code, code, null, null, "active", true, code == "base_icons" ? "icon" : "other_promo", 1);

    private static PromoCardRecord Member(long id, long promoId, DefinitionId card) => new(id, promoId, card, promoId);

    private static PromoEventRecord Event(long id, string code, bool enabled, DateTime startsAt, DateTime endsAt) =>
        new(id, code, code, enabled, startsAt, endsAt);
}
