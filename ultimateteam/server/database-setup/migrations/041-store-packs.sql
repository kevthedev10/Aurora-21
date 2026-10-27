-- Store pack configuration moves from rules/packs.json, store_packs.json and pack_settings.json
-- into the database. Nullable scalars mean "not set for this pack", exactly as an absent JSON key
-- did. Nothing about pack behaviour changes: the seeded rows are the merged document the server
-- produced from those three files.
SET CONSTRAINTS ALL IMMEDIATE;

CREATE TABLE ultimateteam.store_packs (
    pack_id                   bigint  NOT NULL,
    -- store: purchasable. reward: granted by SBCs/objectives. template: defaults for a new custom
    -- pack. display: a name the client shows for something that is not an openable pack.
    kind                      text    NOT NULL,
    enabled                   boolean,
    name                      text,
    bio                       text,
    price                     bigint,
    size                      integer,
    gold_weight               integer,
    silver_weight             integer,
    bronze_weight             integer,
    rare_slots                integer,
    special_chance            numeric,
    manager_chance            numeric,
    chem_style_chance         numeric,
    min_rating                integer,
    -- Read by the pack builder but unused by every bundled pack; a column so such a pack stays
    -- expressible now that the database, not the settings file, is the configuration.
    max_rating                integer,
    kit_stadium_pack          boolean,
    store_group               text,
    player_pick_count         integer,
    -- Set on the four player-pick reward packs; the minimum rating that pick delivers.
    player_pick_min_rating    integer,
    guaranteed_specials       integer,
    allow_unweighted_specials boolean,
    all_consumables           boolean,
    all_kits                  boolean,
    set_stadium_id            bigint,
    -- Built-in feature packs that must keep existing.
    always_present            boolean NOT NULL DEFAULT false,
    display_only              boolean NOT NULL DEFAULT false,
    -- Restricts which special rarities the pack may draw; only a few packs set it.
    allowed_rareflags         bigint[],
    -- Per-pack overrides of store_pack_rarity_weights. NULL means inherit every default, which is
    -- what every bundled pack does today.
    special_weights           jsonb,
    CONSTRAINT lfv4_pk_store_packs PRIMARY KEY (pack_id),
    CONSTRAINT lfv4_ck_store_packs_kind CHECK (kind IN ('store', 'reward', 'template', 'display')),
    CONSTRAINT lfv4_ck_store_packs_size CHECK (size IS NULL OR size >= 0),
    CONSTRAINT lfv4_ck_store_packs_price CHECK (price IS NULL OR price >= 0),
    CONSTRAINT lfv4_ck_store_packs_weights CHECK (
        (gold_weight IS NULL OR gold_weight >= 0)
        AND (silver_weight IS NULL OR silver_weight >= 0)
        AND (bronze_weight IS NULL OR bronze_weight >= 0))
);

-- The global default weight of each special rarity when a pack rolls a special card.
CREATE TABLE ultimateteam.store_pack_rarity_weights (
    -- Keyed by the native rareflag, which is what pack configuration has always used; the
    -- native_rarity_id surrogate is an internal catalogue key and is deliberately not used here.
    native_rareflag bigint  NOT NULL,
    weight          integer NOT NULL,
    CONSTRAINT lfv4_pk_store_pack_rarity_weights PRIMARY KEY (native_rareflag),
    CONSTRAINT lfv4_fk_store_pack_rarity_weights_rarity FOREIGN KEY (native_rareflag)
        REFERENCES ultimateteam.native_rarities(native_rareflag),
    CONSTRAINT lfv4_ck_store_pack_rarity_weights_weight CHECK (weight >= 0)
);

-- Store tabs a pack may be listed under. An alias row carries alias_of and no ids of its own.
CREATE TABLE ultimateteam.store_pack_groups (
    name        text   NOT NULL,
    category_id bigint,
    art_id      bigint,
    alias_of    text,
    CONSTRAINT lfv4_pk_store_pack_groups PRIMARY KEY (name),
    CONSTRAINT lfv4_ck_store_pack_groups_alias CHECK (
        (alias_of IS NULL AND category_id IS NOT NULL)
        OR (alias_of IS NOT NULL AND category_id IS NULL AND art_id IS NULL))
);

-- Reward tile artwork chosen by pack id range: the first band whose below_pack_id exceeds the id.
CREATE TABLE ultimateteam.store_pack_art_bands (
    below_pack_id bigint NOT NULL,
    art_id        bigint NOT NULL,
    CONSTRAINT lfv4_pk_store_pack_art_bands PRIMARY KEY (below_pack_id)
);

-- Card tier by rating when the catalogue names none, and the rating at which a base card of that
-- tier counts as rare. Bronze has no minimum: it is the fallback tier.
CREATE TABLE ultimateteam.store_pack_tiers (
    tier                     text    NOT NULL,
    min_rating               integer,
    rare_fallback_min_rating integer NOT NULL,
    CONSTRAINT lfv4_pk_store_pack_tiers PRIMARY KEY (tier),
    CONSTRAINT lfv4_ck_store_pack_tiers_name CHECK (tier IN ('gold', 'silver', 'bronze'))
);

-- Single-row pack policy. The check keeps it single-row.
CREATE TABLE ultimateteam.store_pack_details (
    only_row                   boolean NOT NULL DEFAULT true,
    max_pack_size              integer NOT NULL,
    max_kit_pack_size          integer NOT NULL,
    coins_per_point            bigint  NOT NULL,
    reward_pack_special_chance numeric NOT NULL,
    special_pack_art_id        bigint  NOT NULL,
    store_art_max_pack_id      bigint  NOT NULL,
    elite_from_rating          bigint  NOT NULL,
    elite_per_rating           numeric NOT NULL,
    elite_maximum              numeric NOT NULL,
    elite_minimum              numeric NOT NULL,
    CONSTRAINT lfv4_pk_store_pack_details PRIMARY KEY (only_row),
    CONSTRAINT lfv4_ck_store_pack_details_single CHECK (only_row),
    CONSTRAINT lfv4_ck_store_pack_details_coins CHECK (coins_per_point > 0)
);

CREATE INDEX lfv4_ix_store_packs_kind ON ultimateteam.store_packs(kind);

SET CONSTRAINTS ALL IMMEDIATE;



INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,bio,price,size,gold_weight,silver_weight,bronze_weight,rare_slots,special_chance,manager_chance,chem_style_chance,min_rating,store_group,player_pick_count,player_pick_min_rating,guaranteed_specials,allow_unweighted_specials,all_consumables,all_kits,set_stadium_id,always_present,display_only,allowed_rareflags,special_weights) VALUES
(1,'store',true,'Bronze Pack','A bronze pack containing 12 items with 1 guaranteed rare and a chance of a Manager or Chemistry Style.',2000,12,0,0,100,1,0.1,0.2,0.25,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(2,'store',true,'Silver Pack','A silver pack containing 12 items with 1 guaranteed rare and a chance of a Manager or Chemistry Style.',2500,12,0,100,0,1,0.1,0.2,0.25,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(3,'store',true,'Gold Pack','A gold pack containing 12 items with 3 guaranteed rares and a chance of a Manager or Chemistry Style.',7500,12,100,0,0,3,0.1,0.2,0.35,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(4,'store',true,'Elite Ten 82+ Pack','Contains 10 Gold players rated 82 or higher, including at least 1 Special player.',50000,10,100,0,0,10,0.15,0,NULL,82,'special',NULL,NULL,1,NULL,NULL,NULL,NULL,true,false,NULL,NULL),
(5,'store',true,'Guaranteed 99 pack','Ouuuu shiiii, fym ouuuu shi?',1000,1,100,0,0,1,0,0,NULL,99,'special',NULL,NULL,1,true,NULL,NULL,NULL,true,false,NULL,NULL),
(6,'store',true,'Guaranteed 92+ x10 Player Pack','Contains 10 unique players rated 92 or higher.',25000,10,100,0,0,10,0,0,NULL,92,'special',NULL,NULL,10,true,NULL,NULL,NULL,true,false,NULL,NULL),
(7,'store',true,'Guaranteed Prime Moments Icon Pack','Contains one high-rated Icon player.',50000,1,100,0,0,1,0,0,NULL,90,'special',NULL,NULL,1,true,NULL,NULL,NULL,true,false,ARRAY[12]::bigint[],NULL),
(8,'store',true,'All Consumables Pack','Contains one of every native FIFA 21 position modifier.',1,20,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,true,NULL,NULL,false,false,NULL,NULL),
(9,'store',true,'All Kits Pack','Contains 30 unique FIFA 21 kits. Reopen it to collect the complete 1,382-kit catalog.',1,30,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,true,NULL,false,false,NULL,NULL),
(10,'store',true,'Full Icon XI Pack','Build a whole starting XI of legends. Contains 11 unique Icon and Icon Moments players rated 87 or higher — every slot a walkout.',125000,11,100,0,0,11,0.0,0.0,NULL,87,'special',NULL,NULL,11,true,NULL,NULL,NULL,false,false,ARRAY[12,84]::bigint[],NULL),
(11,'store',true,'Stadium: Sanderson Park (reset)','Sets your home venue to the default Sanderson Park.',1,1,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,NULL,261,false,false,NULL,NULL),
(12,'store',true,'Stadium: Santiago Bernabeu','Sets your home venue to Santiago Bernabeu.',1,1,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,NULL,2,false,false,NULL,NULL),
(13,'store',true,'Stadium: Old Trafford','Sets your home venue to Old Trafford.',1,1,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,NULL,5,false,false,NULL,NULL),
(14,'store',true,'Stadium: Anfield','Sets your home venue to Anfield.',1,1,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,NULL,12,false,false,NULL,NULL),
(15,'store',true,'Stadium: Emirates Stadium','Sets your home venue to the Emirates Stadium.',1,1,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,NULL,14,false,false,NULL,NULL),
(16,'store',true,'Stadium: Etihad Stadium','Sets your home venue to the Etihad Stadium.',1,1,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,NULL,15,false,false,NULL,NULL),
(17,'store',true,'Stadium: Signal Iduna Park','Sets your home venue to Signal Iduna Park.',1,1,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,NULL,9,false,false,NULL,NULL),
(18,'store',true,'Stadium: San Siro','Sets your home venue to San Siro.',1,1,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,NULL,100,false,false,NULL,NULL),
(19,'store',true,'Stadium: Parc des Princes','Sets your home venue to Parc des Princes.',1,1,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,NULL,104,false,false,NULL,NULL),
(20,'store',true,'Stadium: Camp Nou','Sets your home venue to Camp Nou.',1,1,100,0,0,3,0.1,0,NULL,NULL,'special',NULL,NULL,NULL,NULL,NULL,NULL,112,false,false,NULL,NULL),
(21,'store',true,'82+ Player Pick','Guaranteed 82-rated or better player, straight to your club.',15000,1,100,0,0,3,0.1,0,NULL,82,'special',4,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(22,'store',true,'84+ Player Pick','Guaranteed 84-rated or better player, straight to your club.',30000,1,100,0,0,3,0.1,0,NULL,84,'special',4,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(23,'store',true,'86+ Player Pick','Guaranteed 86-rated or better player, straight to your club.',60000,1,100,0,0,3,0.1,0,NULL,86,'special',4,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(24,'store',true,'88+ Player Pick','Guaranteed 88-rated or better player, straight to your club.',120000,1,100,0,0,3,0.1,0,NULL,88,'special',4,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(42,'store',true,'JayFreezy Pack','Ice cold. A full starting XI of the best in the game: 11 unique players, every one rated 98 or higher.',3000000,11,100,0,0,11,0,0,NULL,98,'special',NULL,NULL,11,true,NULL,NULL,NULL,true,false,NULL,NULL),
(43,'store',true,'Ballon d''Or Pack','Only the untouchables. Contains 5 unique players rated 99.',2500000,5,100,0,0,5,0,0,NULL,99,'special',NULL,NULL,5,true,NULL,NULL,NULL,true,false,NULL,NULL),
(44,'store',true,'Galácticos Pack','Build a superteam in one go. A full 23-man squad of unique players rated 95 or higher.',2000000,23,100,0,0,23,0,0,NULL,95,'special',NULL,NULL,23,true,NULL,NULL,NULL,true,false,NULL,NULL),
(45,'store',true,'Icon Vault','The legends room is open. Contains 11 unique Icons rated 93 or higher.',1500000,11,100,0,0,11,0,0,NULL,93,'special',NULL,NULL,11,true,NULL,NULL,NULL,true,false,ARRAY[12,84]::bigint[],NULL),
(46,'store',true,'Deep Freeze Mega Pack','The biggest pack in the store. 30 unique players, every one rated 93 or higher.',1750000,30,100,0,0,30,0,0,NULL,93,'special',NULL,NULL,30,true,NULL,NULL,NULL,true,false,NULL,NULL),
(47,'store',true,'84+ x50 Pack','Fifty walkouts in one pack. Contains 50 unique players, every one rated 84 or higher.',1250000,50,100,0,0,50,0.25,0,NULL,84,'special',NULL,NULL,0,true,NULL,NULL,NULL,true,false,NULL,NULL),
(100,'reward',NULL,'Bronze Pack',NULL,NULL,12,NULL,NULL,100,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(200,'reward',NULL,'Silver Pack',NULL,NULL,12,NULL,100,NULL,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(201,'reward',NULL,'Premium Silver Players Pack',NULL,NULL,12,NULL,100,NULL,3,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(300,'reward',NULL,'Gold Pack',NULL,NULL,12,100,NULL,NULL,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(301,'reward',NULL,'Premium Gold Pack',NULL,NULL,12,100,NULL,NULL,3,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(304,'reward',NULL,'Gold Players Pack',NULL,NULL,12,100,NULL,NULL,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(305,'reward',NULL,'Premium Gold Players Pack',NULL,NULL,12,100,NULL,NULL,3,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(306,'reward',NULL,'Electrum Players Pack',NULL,NULL,12,50,50,NULL,1,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(307,'reward',NULL,'Prime Electrum Players Pack',NULL,NULL,12,50,50,NULL,6,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(308,'reward',NULL,'Prime Gold Players Pack',NULL,NULL,12,100,NULL,NULL,6,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(309,'reward',NULL,'Prime Mixed Players Pack',NULL,NULL,12,34,33,33,6,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(400,'reward',NULL,'Rare Gold Pack',NULL,NULL,12,100,NULL,NULL,12,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(401,'reward',NULL,'Rare Players Pack',NULL,NULL,12,100,NULL,NULL,12,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(402,'reward',NULL,'Jumbo Rare Players Pack',NULL,NULL,24,100,NULL,NULL,24,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(403,'reward',NULL,'Mega Pack',NULL,NULL,30,100,NULL,NULL,18,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(404,'reward',NULL,'Rare Mega Pack',NULL,NULL,30,100,NULL,NULL,30,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(405,'reward',NULL,'Ultimate Pack',NULL,NULL,30,100,NULL,NULL,30,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(406,'reward',NULL,'Rare Mixed Players Pack',NULL,NULL,12,34,33,33,12,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(513,'reward',NULL,'82+ Player Pick',NULL,NULL,1,100,NULL,NULL,1,NULL,NULL,NULL,82,NULL,4,82,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(514,'reward',NULL,'84+ Player Pick',NULL,NULL,1,100,NULL,NULL,1,NULL,NULL,NULL,84,NULL,4,84,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(515,'reward',NULL,'86+ Player Pick',NULL,NULL,1,100,NULL,NULL,1,NULL,NULL,NULL,86,NULL,4,86,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(516,'reward',NULL,'88+ Player Pick',NULL,NULL,1,100,NULL,NULL,1,NULL,NULL,NULL,88,NULL,4,88,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(0,'template',true,'Custom Pack {id}','A custom FIFA 21 Ultimate Team pack.',7500,12,100,0,0,3,0.1,0,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,false,NULL,NULL),
(500,'display',NULL,'Draft Token Pack',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,false,true,NULL,NULL);

INSERT INTO ultimateteam.store_pack_rarity_weights(native_rareflag,weight) VALUES
(3,55),
(4,4),
(5,2),
(6,3),
(8,12),
(9,5),
(10,1),
(11,8),
(12,1),
(16,1),
(21,1),
(22,1),
(25,1),
(30,1),
(32,1),
(33,1),
(34,1),
(37,1),
(42,1),
(43,1),
(45,1),
(46,1),
(47,1),
(48,1),
(49,1),
(50,1),
(51,1),
(52,1),
(53,1),
(54,1),
(55,1),
(57,1),
(58,1),
(59,1),
(60,1),
(61,1),
(62,1),
(64,1),
(65,1),
(68,1),
(70,1),
(71,1),
(75,1),
(76,1),
(77,1),
(79,1),
(81,1),
(82,1),
(84,1),
(86,1),
(87,1),
(89,1),
(90,1),
(91,1),
(92,1),
(93,1),
(94,1),
(95,1),
(96,1),
(98,1);

INSERT INTO ultimateteam.store_pack_groups(name,category_id,art_id,alias_of) VALUES
('bronze',1,1,NULL),
('silver',2,2,NULL),
('gold',3,3,NULL),
('special',4,4,NULL),
('promo',NULL,NULL,'special');

INSERT INTO ultimateteam.store_pack_art_bands(below_pack_id,art_id) VALUES
(200,1),
(300,2),
(400,3);

INSERT INTO ultimateteam.store_pack_tiers(tier,min_rating,rare_fallback_min_rating) VALUES
('gold',75,82),
('silver',65,72),
('bronze',NULL,64);

INSERT INTO ultimateteam.store_pack_details(max_pack_size,max_kit_pack_size,coins_per_point,reward_pack_special_chance,special_pack_art_id,store_art_max_pack_id,elite_from_rating,elite_per_rating,elite_maximum,elite_minimum) VALUES
(50,30,50,0.1,4,99,90,0.35,3.5,0.0001);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(41);
