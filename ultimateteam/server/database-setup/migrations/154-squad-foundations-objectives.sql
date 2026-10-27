-- Imported Squad Foundations objectives, adapted by the owner: Rivals or FUT Champions,
-- minimum 3 instead of 11 league starters, minimum 2 instead of 7; ceil(original target / 2).
-- All imported groups are explicitly OFF. Existing live ordering, edits, counters and claims survive.
-- The numeric ids in the source seed are project ids; upgrades allocate unused ids past catalogue AND claim history.
-- Native event flags/player_id semantics remain unverified: no guessed mapping is installed.
SET CONSTRAINTS ALL IMMEDIATE;
SELECT set_config('ultimateteam.content_publish', 'on', true);

-- Project reward pack: one standard rare gold player, 75+, grant only, never in the shop lineup. It is 564 on a fresh
-- database (the bundled objectives.json names 564). The panel can create packs past the catalogue, and live 10-05 already
-- had operator pack 564 (Guaranteed Icon Pick): then reuse an existing 75+ pack of this definition, or take the id after
-- every pack and Token Store offer. The imported awards below name the chosen id. Operator packs are never touched.
DO $$
DECLARE reward_pack bigint;
BEGIN
    SELECT pack_id INTO reward_pack FROM ultimateteam.store_packs
    WHERE size=1 AND min_rating=75 AND rare_slots=1 AND gold_weight=100 AND silver_weight=0 AND bronze_weight=0
        AND allowed_rareflags=ARRAY[1]::bigint[] AND admin_only AND category_chances='{}'::jsonb
        AND (pack_id=564 OR name='75+ Rated Rare Player Pack')
    ORDER BY pack_id<>564, pack_id LIMIT 1;
    IF reward_pack IS NULL THEN
        -- A Token Store offer numbered 564 does not block it: migration 158 moves offers off pack ids.
        IF NOT EXISTS(SELECT 1 FROM ultimateteam.store_packs WHERE pack_id=564)
            AND NOT EXISTS(SELECT 1 FROM ultimateteam.owned_legacy_pack_definitions WHERE pack_id=564) THEN
            reward_pack := 564;
        ELSE
            SELECT greatest(
                coalesce((SELECT max(pack_id) FROM ultimateteam.store_packs),0),
                coalesce((SELECT max(pack_id) FROM ultimateteam.owned_legacy_pack_definitions),0),
                coalesce((SELECT max(offer_id) FROM ultimateteam.token_store_listings),0))+1 INTO reward_pack;
        END IF;
        INSERT INTO ultimateteam.store_packs(pack_id,kind,enabled,name,bio,price,points_price,size,
            gold_weight,silver_weight,bronze_weight,rare_slots,min_rating,store_group,admin_only,
            include_general_pool,allowed_rareflags,category_chances)
        VALUES(reward_pack,'store',false,'75+ Rated Rare Player Pack','One rare gold player rated 75 or higher.',0,0,1,
            100,0,0,1,75,'gold',true,true,ARRAY[1]::bigint[],'{}'::jsonb);
    END IF;
    PERFORM set_config('ultimateteam.squad_foundations_pack', reward_pack::text, true);
END $$;

-- Append one JSON subtree as the scalar/empty-container rows RuleRows consumes, with stable array ordering.
CREATE FUNCTION pg_temp.append_objective_node(location jsonb, node jsonb, next_ordinal integer)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE child record; BEGIN
    IF jsonb_typeof(node)='object' AND node<>'{}'::jsonb THEN
        FOR child IN SELECT key,value FROM jsonb_each(node) ORDER BY key LOOP
            next_ordinal := pg_temp.append_objective_node(location || jsonb_build_array(child.key),child.value,next_ordinal);
        END LOOP;
    ELSIF jsonb_typeof(node)='array' AND node<>'[]'::jsonb THEN
        FOR child IN SELECT value,ord FROM jsonb_array_elements(node) WITH ORDINALITY a(value,ord) ORDER BY ord LOOP
            next_ordinal := pg_temp.append_objective_node(location || jsonb_build_array(child.ord-1),child.value,next_ordinal);
        END LOOP;
    ELSE
        INSERT INTO ultimateteam.content_rules(file,path,value,ordinal) VALUES('objectives',location,node,next_ordinal);
        next_ordinal := next_ordinal+1;
    END IF;
    RETURN next_ordinal;
END $$;

DO $import$
DECLARE
    groups jsonb := $groups$
[
  {
    "id": 30,
    "sourceKey": "mls_squad_foundation_ii",
    "title": "MLS Squad Foundation II",
    "subTitle": "MLS · Division Rivals / FUT Champions",
    "award": {
      "players": [
        {
          "resourceId": 50520590,
          "untradeable": true
        }
      ]
    },
    "xp": 0,
    "objectives": [
      {
        "id": 3001,
        "name": "Fine Finisher",
        "stat": "goals",
        "target": 6,
        "description": "Score {n} Finesse goals using MLS players in Division Rivals or FUT Champions, with at least 3 MLS players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 39,
            "minimumCount": 3
          },
          "player": {
            "leagueId": 39
          },
          "goalType": "finesse"
        },
        "award": {
          "players": [
            {
              "resourceId": 50554529,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3002,
        "name": "Assistance Required",
        "stat": "assists",
        "target": 3,
        "description": "Assist {n} goals with Crosses using MLS players in Division Rivals or FUT Champions, with at least 3 MLS players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 39,
            "minimumCount": 3
          },
          "player": {
            "leagueId": 39
          },
          "assistType": "cross"
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3003,
        "name": "Playing Time",
        "stat": "matches",
        "target": 9,
        "description": "Play {n} matches in Division Rivals or FUT Champions, with at least 3 MLS players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 39,
            "minimumCount": 3
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50539540,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3004,
        "name": "Winning Streak",
        "stat": "wins",
        "target": 4,
        "description": "Win {n} matches in Division Rivals or FUT Champions, with at least 3 MLS players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 39,
            "minimumCount": 3
          }
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      }
    ]
  },
  {
    "id": 31,
    "sourceKey": "st_patricks_squad_foundation",
    "title": "St Patrick's Squad Foundation",
    "subTitle": "St Patrick's · Division Rivals / FUT Champions",
    "award": {
      "players": [
        {
          "resourceId": 84123366,
          "untradeable": true
        }
      ]
    },
    "xp": 0,
    "objectives": [
      {
        "id": 3101,
        "name": "Shamrock Scorers",
        "stat": "goals",
        "target": 5,
        "description": "Score {n} goals using Republic of Ireland players in Division Rivals or FUT Champions, with at least 4 Republic of Ireland players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "nation": 25,
            "minimumCount": 4
          },
          "player": {
            "nation": 25
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50534311,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3102,
        "name": "Irish Instinct",
        "stat": "assists",
        "target": 3,
        "description": "Assist {n} goals with Through Balls using Republic of Ireland players in Division Rivals or FUT Champions, with at least 4 Republic of Ireland players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "nation": 25,
            "minimumCount": 4
          },
          "player": {
            "nation": 25
          },
          "assistType": "through_ball"
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3103,
        "name": "Paddy's Day Play",
        "stat": "matches",
        "target": 7,
        "description": "Play {n} matches in Division Rivals or FUT Champions, with at least 4 Republic of Ireland players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "nation": 25,
            "minimumCount": 4
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50532126,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3104,
        "name": "Green Glory",
        "stat": "wins",
        "target": 4,
        "description": "Win {n} matches in Division Rivals or FUT Champions, with at least 4 Republic of Ireland players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "nation": 25,
            "minimumCount": 4
          }
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      }
    ]
  },
  {
    "id": 32,
    "sourceKey": "efl_squad_foundation_ii",
    "title": "EFL Squad Foundation II",
    "subTitle": "EFL Championship · Division Rivals / FUT Champions",
    "award": {
      "players": [
        {
          "resourceId": 50557810,
          "untradeable": true
        }
      ]
    },
    "xp": 0,
    "objectives": [
      {
        "id": 3201,
        "name": "Fine Finisher",
        "stat": "goals",
        "target": 6,
        "description": "Score {n} Finesse goals using EFL Championship players in Division Rivals or FUT Champions, with at least 3 EFL Championship players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 14,
            "minimumCount": 3
          },
          "player": {
            "leagueId": 14
          },
          "goalType": "finesse"
        },
        "award": {
          "players": [
            {
              "resourceId": 50556142,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3202,
        "name": "Assistance Required",
        "stat": "assists",
        "target": 3,
        "description": "Assist {n} goals with Crosses using EFL Championship players in Division Rivals or FUT Champions, with at least 3 EFL Championship players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 14,
            "minimumCount": 3
          },
          "player": {
            "leagueId": 14
          },
          "assistType": "cross"
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3203,
        "name": "Playing Time",
        "stat": "matches",
        "target": 9,
        "description": "Play {n} matches in Division Rivals or FUT Champions, with at least 3 EFL Championship players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 14,
            "minimumCount": 3
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50545639,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3204,
        "name": "Winning Streak",
        "stat": "wins",
        "target": 4,
        "description": "Win {n} matches in Division Rivals or FUT Champions, with at least 3 EFL Championship players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 14,
            "minimumCount": 3
          }
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      }
    ]
  },
  {
    "id": 33,
    "sourceKey": "eredivisie_squad_foundation_ii",
    "title": "Eredivisie Squad Foundation II",
    "subTitle": "Eredivisie · Division Rivals / FUT Champions",
    "award": {
      "players": [
        {
          "resourceId": 67342042,
          "untradeable": true
        }
      ]
    },
    "xp": 0,
    "objectives": [
      {
        "id": 3301,
        "name": "Fine Finisher",
        "stat": "goals",
        "target": 6,
        "description": "Score {n} Finesse goals using Eredivisie players in Division Rivals or FUT Champions, with at least 3 Eredivisie players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 10,
            "minimumCount": 3
          },
          "player": {
            "leagueId": 10
          },
          "goalType": "finesse"
        },
        "award": {
          "players": [
            {
              "resourceId": 50543835,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3302,
        "name": "Assistance Required",
        "stat": "assists",
        "target": 3,
        "description": "Assist {n} goals with Crosses using Eredivisie players in Division Rivals or FUT Champions, with at least 3 Eredivisie players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 10,
            "minimumCount": 3
          },
          "player": {
            "leagueId": 10
          },
          "assistType": "cross"
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3303,
        "name": "Playing Time",
        "stat": "matches",
        "target": 9,
        "description": "Play {n} matches in Division Rivals or FUT Champions, with at least 3 Eredivisie players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 10,
            "minimumCount": 3
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50571529,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3304,
        "name": "Winning Streak",
        "stat": "wins",
        "target": 4,
        "description": "Win {n} matches in Division Rivals or FUT Champions, with at least 3 Eredivisie players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 10,
            "minimumCount": 3
          }
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      }
    ]
  },
  {
    "id": 34,
    "sourceKey": "eredivisie_squad_foundation",
    "title": "Eredivisie Squad Foundation",
    "subTitle": "Eredivisie · Division Rivals / FUT Champions",
    "award": {
      "players": [
        {
          "resourceId": 50563512,
          "untradeable": true
        }
      ]
    },
    "xp": 0,
    "objectives": [
      {
        "id": 3401,
        "name": "Scoring Streak",
        "stat": "goals",
        "target": 8,
        "description": "Score {n} goals using Eredivisie players in Division Rivals or FUT Champions, with at least 2 Eredivisie players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 10,
            "minimumCount": 2
          },
          "player": {
            "leagueId": 10
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50577752,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3402,
        "name": "Assistance Required",
        "stat": "assists",
        "target": 8,
        "description": "Assist {n} goals using Eredivisie players in Division Rivals or FUT Champions, with at least 2 Eredivisie players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 10,
            "minimumCount": 2
          },
          "player": {
            "leagueId": 10
          }
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3403,
        "name": "Playing Time",
        "stat": "matches",
        "target": 8,
        "description": "Play {n} matches in Division Rivals or FUT Champions, with at least 2 Eredivisie players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 10,
            "minimumCount": 2
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50555289,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3404,
        "name": "Winning Streak",
        "stat": "wins",
        "target": 5,
        "description": "Win {n} matches in Division Rivals or FUT Champions, with at least 2 Eredivisie players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 10,
            "minimumCount": 2
          }
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      }
    ]
  },
  {
    "id": 35,
    "sourceKey": "efl_squad_foundation",
    "title": "EFL Squad Foundation",
    "subTitle": "EFL Championship · Division Rivals / FUT Champions",
    "award": {
      "players": [
        {
          "resourceId": 50517070,
          "untradeable": true
        }
      ]
    },
    "xp": 300,
    "objectives": [
      {
        "id": 3501,
        "name": "Scoring Streak",
        "stat": "goals",
        "target": 8,
        "description": "Score {n} goals using EFL Championship players in Division Rivals or FUT Champions, with at least 2 EFL Championship players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 14,
            "minimumCount": 2
          },
          "player": {
            "leagueId": 14
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50542746,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3502,
        "name": "Assistance Required",
        "stat": "assists",
        "target": 8,
        "description": "Assist {n} goals using EFL Championship players in Division Rivals or FUT Champions, with at least 2 EFL Championship players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 14,
            "minimumCount": 2
          },
          "player": {
            "leagueId": 14
          }
        },
        "award": {
          "none": true
        },
        "xp": 0
      },
      {
        "id": 3503,
        "name": "Playing Time",
        "stat": "matches",
        "target": 8,
        "description": "Play {n} matches in Division Rivals or FUT Champions, with at least 2 EFL Championship players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 14,
            "minimumCount": 2
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50500862,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3504,
        "name": "Winning Streak",
        "stat": "wins",
        "target": 5,
        "description": "Win {n} matches in Division Rivals or FUT Champions, with at least 2 EFL Championship players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 14,
            "minimumCount": 2
          }
        },
        "award": {
          "none": true
        },
        "xp": 0
      }
    ]
  },
  {
    "id": 36,
    "sourceKey": "mls_squad_foundation",
    "title": "MLS Squad Foundation",
    "subTitle": "MLS · Division Rivals / FUT Champions",
    "award": {
      "players": [
        {
          "resourceId": 50508641,
          "untradeable": true
        }
      ]
    },
    "xp": 300,
    "objectives": [
      {
        "id": 3601,
        "name": "Scoring Streak",
        "stat": "goals",
        "target": 8,
        "description": "Score {n} goals using MLS players in Division Rivals or FUT Champions, with at least 2 MLS players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 39,
            "minimumCount": 2
          },
          "player": {
            "leagueId": 39
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50553771,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3602,
        "name": "Made by MLS",
        "stat": "assists",
        "target": 8,
        "description": "Assist {n} goals using MLS players in Division Rivals or FUT Champions, with at least 2 MLS players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 39,
            "minimumCount": 2
          },
          "player": {
            "leagueId": 39
          }
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3603,
        "name": "Major League Maestro",
        "stat": "matches",
        "target": 8,
        "description": "Play {n} matches in Division Rivals or FUT Champions, with at least 2 MLS players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 39,
            "minimumCount": 2
          }
        },
        "award": {
          "players": [
            {
              "resourceId": 50568641,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      },
      {
        "id": 3604,
        "name": "Winning Streak",
        "stat": "wins",
        "target": 5,
        "description": "Win {n} matches in Division Rivals or FUT Champions, with at least 2 MLS players in your starting squad.",
        "requirements": {
          "modes": [
            "rivals",
            "weekendLeague"
          ],
          "startingSquad": {
            "leagueId": 39,
            "minimumCount": 2
          }
        },
        "award": {
          "packs": [
            {
              "packId": 564,
              "count": 1,
              "untradeable": true
            }
          ]
        },
        "xp": 0
      }
    ]
  }
]
$groups$::jsonb;
    g jsonb; objective jsonb; live_value jsonb; old record; existing_key text;
    group_id bigint; objective_id bigint; group_pos integer; live_pos integer; ord integer; ix integer;
BEGIN
    IF NOT EXISTS(SELECT 1 FROM ultimateteam.content_rules WHERE file='objectives') THEN
        RETURN; -- A fresh database's subsequent rules fill uses the bundled, already-disabled definitions.
    END IF;
    -- The seed's awards name 564; point them at the reward pack chosen above.
    groups := regexp_replace(groups::text, '"packId": 564([,}])',
        '"packId": ' || current_setting('ultimateteam.squad_foundations_pack') || '\1', 'g')::jsonb;
    SELECT coalesce(max(ordinal),-1)+1 INTO ord FROM ultimateteam.content_rules WHERE file='objectives';
    -- A null/absent live list used to mean every existing group was on. Make that exact order explicit before appending.
    IF NOT EXISTS(SELECT 1 FROM ultimateteam.content_rules WHERE file='objectives' AND path->>0='live'
        AND value<>'null'::jsonb) THEN
        DELETE FROM ultimateteam.content_rules WHERE file='objectives' AND path='["live"]'::jsonb;
        live_value := '[]'::jsonb;
        FOR old IN SELECT (CASE WHEN jsonb_typeof(value)='number' THEN value #>> '{}' END)::bigint id FROM ultimateteam.content_rules
            WHERE file='objectives' AND (path='["welcomeGroup","id"]'::jsonb
                OR jsonb_array_length(path)=3 AND path->>0 IN ('grindGroups','gifts') AND path->>2='id')
            ORDER BY CASE WHEN path->>0='welcomeGroup' THEN 0 WHEN path->>0='grindGroups' THEN 1 ELSE 2 END,ordinal LOOP
            live_value := live_value || jsonb_build_array(jsonb_build_object('group',old.id,'enabled',true));
        END LOOP;
        ord := pg_temp.append_objective_node('["live"]'::jsonb,live_value,ord);
    END IF;
    FOR g IN SELECT value FROM jsonb_array_elements(groups) LOOP
        existing_key := g->>'sourceKey';
        IF EXISTS(SELECT 1 FROM ultimateteam.content_rules WHERE file='objectives' AND path->>0='grindGroups'
            AND jsonb_array_length(path)=3 AND path->>2='sourceKey' AND value=to_jsonb(existing_key)) THEN CONTINUE; END IF;
        SELECT coalesce(max(id),0)+1 INTO group_id FROM (
            SELECT (CASE WHEN jsonb_typeof(value)='number' THEN value #>> '{}' END)::bigint id FROM ultimateteam.content_rules
            WHERE file='objectives' AND (path='["welcomeGroup","id"]'::jsonb
                OR jsonb_array_length(path)=3 AND path->>0 IN ('grindGroups','gifts') AND path->>2='id')
            UNION ALL SELECT started.group_id FROM ultimateteam.objective_group_starts started
            UNION ALL SELECT claim_id FROM ultimateteam.objective_claims WHERE scope='group') ids;
        SELECT coalesce(max(id),0)+1 INTO objective_id FROM (
            SELECT (CASE WHEN jsonb_typeof(value)='number' THEN value #>> '{}' END)::bigint id FROM ultimateteam.content_rules WHERE file='objectives' AND (
                path='["welcomeGroup","objective","id"]'::jsonb
                OR jsonb_array_length(path)=5 AND path->>0='grindGroups' AND path->>2='objectives' AND path->>4='id'
                OR jsonb_array_length(path)=4 AND path->>0='gifts' AND path->>2='objective' AND path->>3='id')
            UNION ALL SELECT claim_id FROM ultimateteam.objective_claims WHERE scope='objective') ids;
        g := jsonb_set(g,'{id}',to_jsonb(group_id));
        FOR ix IN 0..jsonb_array_length(g->'objectives')-1 LOOP
            g := jsonb_set(g,ARRAY['objectives',ix::text,'id'],to_jsonb(objective_id)); objective_id := objective_id+1;
        END LOOP;
        SELECT coalesce(max((CASE WHEN jsonb_typeof(path->1)='number' THEN path->>1 END)::integer),-1)+1 INTO group_pos FROM ultimateteam.content_rules WHERE file='objectives'
            AND path->>0='grindGroups' AND jsonb_array_length(path)>1;
        DELETE FROM ultimateteam.content_rules WHERE file='objectives' AND path='["grindGroups"]'::jsonb AND value='[]'::jsonb;
        ord := pg_temp.append_objective_node(jsonb_build_array('grindGroups',group_pos),g,ord);
        SELECT coalesce(max((CASE WHEN jsonb_typeof(path->1)='number' THEN path->>1 END)::integer),-1)+1 INTO live_pos FROM ultimateteam.content_rules WHERE file='objectives'
            AND path->>0='live' AND jsonb_array_length(path)>1;
        DELETE FROM ultimateteam.content_rules WHERE file='objectives' AND path='["live"]'::jsonb AND value='[]'::jsonb;
        ord := pg_temp.append_objective_node(jsonb_build_array('live',live_pos),jsonb_build_object('group',group_id,'enabled',false),ord);
    END LOOP;
    IF NOT EXISTS(SELECT 1 FROM ultimateteam.content_rules WHERE file='objectives' AND path='["statLabels","assists"]'::jsonb) THEN
        ord := pg_temp.append_objective_node('["statLabels","assists"]'::jsonb,'"Assist {n} goals in FUT matches."'::jsonb,ord);
    END IF;
END $import$;
DROP FUNCTION pg_temp.append_objective_node(jsonb,jsonb,integer);
SELECT set_config('ultimateteam.content_publish', '', true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(154);
