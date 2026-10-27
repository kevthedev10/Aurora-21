-- My Stadium items join the New Items pile. A grant (the store's My Stadium pack, a Stadium Development reward) used to
-- own the item on the spot: the reveal showed it, but FIFA's pile flow then failed — PUT /item had no store move for it
-- (the equip path refused the locked-category items, and a failed move tears the client's flow down) and
-- GET /purchased/items, the pile FIFA lists, never held it. Now a granted item waits in the pile (stored false) until the
-- player keeps or discards it there; only a kept item joins the club's My Stadium items. Every row granted before this
-- migration is already in the club: the owner confirmed those clubs keep them (live 10-03).
SET CONSTRAINTS ALL IMMEDIATE;

-- Develop already shipped this as migration 140. Only backfill when adding the column:
-- rows already waiting in that branch's New Items pile must stay there.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'ultimateteam' AND table_name = 'club_vanity_items' AND column_name = 'stored') THEN
        ALTER TABLE ultimateteam.club_vanity_items ADD COLUMN stored boolean NOT NULL DEFAULT false;
        UPDATE ultimateteam.club_vanity_items SET stored = true;
    END IF;
END $$;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(140);
