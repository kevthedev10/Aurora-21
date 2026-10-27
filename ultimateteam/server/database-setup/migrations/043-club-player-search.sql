-- Default My Club player ordering is a read model of owned items and static card metadata.
-- Keep the source tables authoritative. Catalog definitions and item identity columns are
-- immutable in this schema; the projection follows item inserts, pile moves, and deletes.
CREATE TABLE ultimateteam.club_player_search (
    item_id bigint PRIMARY KEY REFERENCES ultimateteam.items(id) ON DELETE CASCADE,
    club_id bigint NOT NULL,
    native_item_id bigint NOT NULL,
    pile text,
    rating bigint NOT NULL,
    native_rareflag bigint NOT NULL,
    resource_id bigint NOT NULL
);

INSERT INTO ultimateteam.club_player_search
    (item_id, club_id, native_item_id, pile, rating, native_rareflag, resource_id)
SELECT i.id, i.club_id, i.native_item_id, i.pile,
    p.rating, r.native_rareflag, COALESCE(d.native_resource_id, d.native_asset_id, 0)
FROM ultimateteam.items i
JOIN ultimateteam.item_definitions d ON d.definition_id=i.definition_id
JOIN ultimateteam.player_card_definitions p ON p.definition_id=i.definition_id
JOIN ultimateteam.card_types ct ON ct.card_type_id=p.card_type_id
JOIN ultimateteam.native_rarities r ON r.native_rarity_id=ct.native_rarity_id
WHERE i.family_id=1;

DO $$ BEGIN
    IF (SELECT count(*) FROM ultimateteam.club_player_search) <>
       (SELECT count(*) FROM ultimateteam.items WHERE family_id=1) THEN
        RAISE EXCEPTION 'Club player search backfill omitted owned players';
    END IF;
END $$;

CREATE INDEX lfv4_ix_club_player_search_default
    ON ultimateteam.club_player_search
    (club_id, rating DESC, native_rareflag DESC, resource_id DESC, native_item_id ASC)
    WHERE pile='club';

CREATE FUNCTION ultimateteam.lfv4_refresh_club_player_search() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, ultimateteam AS $$
BEGIN
    IF TG_OP='UPDATE' THEN
        IF OLD.pile IS DISTINCT FROM NEW.pile THEN
            UPDATE ultimateteam.club_player_search SET pile=NEW.pile WHERE item_id=NEW.id;
        END IF;
        RETURN NEW;
    END IF;
    INSERT INTO ultimateteam.club_player_search
        (item_id, club_id, native_item_id, pile, rating, native_rareflag, resource_id)
    SELECT NEW.id, NEW.club_id, NEW.native_item_id, NEW.pile,
        p.rating, r.native_rareflag, COALESCE(d.native_resource_id, d.native_asset_id, 0)
    FROM ultimateteam.item_definitions d
    JOIN ultimateteam.player_card_definitions p ON p.definition_id=d.definition_id
    JOIN ultimateteam.card_types ct ON ct.card_type_id=p.card_type_id
    JOIN ultimateteam.native_rarities r ON r.native_rarity_id=ct.native_rarity_id
    WHERE d.definition_id=NEW.definition_id;
    -- A definition from another family is rejected by the deferred item-family check at commit,
    -- which rolls this transaction back; only a player definition without card metadata is new here.
    IF NOT FOUND AND EXISTS(SELECT 1 FROM ultimateteam.item_definitions WHERE definition_id=NEW.definition_id AND family_id=1) THEN
        RAISE EXCEPTION 'Player search definition missing for item %', NEW.id;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER lfv4_trg_club_player_search_items
    AFTER INSERT OR UPDATE OF pile ON ultimateteam.items
    FOR EACH ROW WHEN (NEW.family_id=1)
    EXECUTE FUNCTION ultimateteam.lfv4_refresh_club_player_search();

INSERT INTO ultimateteam.schema_migrations(version) VALUES(43);
