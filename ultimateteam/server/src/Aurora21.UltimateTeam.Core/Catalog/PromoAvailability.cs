using System.Collections.Frozen;
using Aurora21.UltimateTeam.Core.Data.Records;

namespace Aurora21.UltimateTeam.Core.Catalog;

/// <summary>General card availability from one content revision, evaluated at a requested UTC time.</summary>
public sealed class PromoAvailability
{
    private static readonly FrozenSet<long> OrdinaryCardTypes = new long[] { 1, 2, 3, 4, 5, 6 }.ToFrozenSet();
    public static bool IsIconTier(string code) => code is "base_icons" or "mid_icons" or "prime_icons" or "prime_icon_moments";
    private readonly FrozenSet<DefinitionId> _standardCards;
    private readonly FrozenDictionary<long, FrozenSet<DefinitionId>> _icons;
    private readonly long? _defaultIconPromo;
    private readonly IconRotationRecord[] _rotations;
    private readonly FrozenSet<DefinitionId> _promoCards;
    private readonly FrozenDictionary<DefinitionId, long[]> _eventsByCard;
    private readonly FrozenDictionary<long, PromoEventRecord> _events;
    private readonly FrozenDictionary<long, long[]> _eventIdsByPromo;
    private readonly FrozenSet<long> _alwaysPromoIds;

    public PromoAvailability(IReadOnlyDictionary<DefinitionId, long> cardTypes, IReadOnlyList<PromoRecord> promos,
        IReadOnlyList<PromoCardRecord> memberships, IReadOnlyList<PromoEventRecord> events,
        IReadOnlyList<PromoEventPromoRecord> eventPromos, IReadOnlyList<IconRotationRecord>? rotations = null)
    {
        var promosById = promos.ToDictionary(promo => promo.PromoId);
        _defaultIconPromo = promos.FirstOrDefault(p => p.Code == "base_icons" && p.Status == "active")?.PromoId;
        _rotations = (rotations ?? []).Where(r => r.Enabled).OrderBy(r => r.StartsAt).ToArray();
        foreach (var rotation in _rotations)
            if (!promosById.TryGetValue(rotation.PromoId, out var tier) || !IsIconTier(tier.Code) || tier.Status != "active"
                || rotation.StartsAt >= rotation.EndsAt)
                throw new InvalidDataException($"Invalid Icon rotation {rotation.Code}.");
        for (var i = 1; i < _rotations.Length; i++)
            if (_rotations[i].StartsAt < _rotations[i - 1].EndsAt)
                throw new InvalidDataException("Enabled Icon rotation windows overlap.");
        _events = events.ToFrozenDictionary(promoEvent => promoEvent.EventId);
        // Icon tiers rotate in the standard slot. Ordinary promo events cannot activate them.
        var eventsByPromo = eventPromos.Where(link => link.Enabled && !IsIconTier(promosById[link.PromoId].Code))
            .GroupBy(link => link.PromoId)
            .ToDictionary(group => group.Key, group => group.Select(link => link.EventId).Distinct().ToArray());
        _eventIdsByPromo = eventsByPromo.ToFrozenDictionary();
        _alwaysPromoIds = promos.Where(promo => promo.Status == "active" && promo.Code is
            ("gold_rare" or "gold_common" or "silver_common" or "silver_rare" or "bronze_common" or "bronze_rare"))
            .Select(promo => promo.PromoId).ToFrozenSet();
        var standard = cardTypes.Where(card => OrdinaryCardTypes.Contains(card.Value))
            .Select(card => card.Key).ToHashSet();
        var icons = new Dictionary<long, HashSet<DefinitionId>>();
        var eventsByCard = new Dictionary<DefinitionId, HashSet<long>>();
        foreach (var membership in memberships)
        {
            var promo = promosById[membership.PromoId];
            if (IsIconTier(promo.Code) && promo.Status == "active")
            {
                if (!icons.TryGetValue(promo.PromoId, out var cards)) icons[promo.PromoId] = cards = [];
                cards.Add(membership.PlayerCardDefinitionId);
                continue;
            }
            if (promo.Status != "active" || !eventsByPromo.TryGetValue(promo.PromoId, out var eventIds)) continue;
            if (!eventsByCard.TryGetValue(membership.PlayerCardDefinitionId, out var ids))
                eventsByCard[membership.PlayerCardDefinitionId] = ids = new HashSet<long>();
            foreach (var eventId in eventIds) ids.Add(eventId);
        }
        _standardCards = standard.ToFrozenSet();
        _icons = icons.ToFrozenDictionary(p => p.Key, p => p.Value.ToFrozenSet());
        _promoCards = memberships.Select(member => member.PlayerCardDefinitionId).ToFrozenSet();
        _eventsByCard = eventsByCard.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    public bool HasPromo(DefinitionId card) => _promoCards.Contains(card);

    public bool IsStandard(DefinitionId card) => _standardCards.Contains(card) || IsIcon(card);

    public bool IsIcon(DefinitionId card) => _defaultIconPromo is { } id && _icons.GetValueOrDefault(id)?.Contains(card) == true;

    public long? IconPromoId(DateTimeOffset now) =>
        _rotations.FirstOrDefault(r => r.StartsAt <= now.UtcDateTime && now.UtcDateTime < r.EndsAt)?.PromoId ?? _defaultIconPromo;

    public bool IsIcon(DefinitionId card, DateTimeOffset now) =>
        IconPromoId(now) is { } id && _icons.GetValueOrDefault(id)?.Contains(card) == true;

    /// <summary>Promo routes eligible at a draw time; a card in two promos may use only active routes.</summary>
    public IReadOnlySet<long> ActivePromoIds(DateTimeOffset now)
    {
        var active = _alwaysPromoIds.ToHashSet();
        if (IconPromoId(now) is { } icon) active.Add(icon);
        var utc = now.UtcDateTime;
        foreach (var (promoId, eventIds) in _eventIdsByPromo)
        {
            if (eventIds.Any(id => _events[id] is { Enabled: true } promoEvent
                && promoEvent.StartsAt <= utc && utc < promoEvent.EndsAt)) active.Add(promoId);
        }
        return active;
    }

    /// <summary>Event windows are inclusive at the start and exclusive at the end.</summary>
    public bool IsAvailable(DefinitionId card, DateTimeOffset now)
    {
        if (_standardCards.Contains(card) || IsIcon(card, now)) return true;
        if (!_eventsByCard.TryGetValue(card, out var eventIds)) return false;
        var utc = now.UtcDateTime;
        return eventIds.Any(id => _events[id] is { Enabled: true } promoEvent
            && promoEvent.StartsAt <= utc && utc < promoEvent.EndsAt);
    }
}
