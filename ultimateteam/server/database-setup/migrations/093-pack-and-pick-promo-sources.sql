-- Pack sources and the draw rules captured by newly created pick tokens.
ALTER TABLE ultimateteam.store_packs
    ADD COLUMN include_general_pool boolean,
    ADD COLUMN promo_codes text[];
ALTER TABLE ultimateteam.player_pick_items
    ADD COLUMN draw_config jsonb,
    ADD CONSTRAINT player_pick_draw_config_object CHECK (draw_config IS NULL OR jsonb_typeof(draw_config)='object');
INSERT INTO ultimateteam.schema_migrations(version) VALUES(93);
