-- Source: fifa21_potm_1_sbc (1).json, SHA-256 4b58d1f4b5617f7a96003486c2c9fea2f0573c94d7ed16456cd9dcd6ab5a5a1f.
-- IDs are existing local wefut-custom-avatar catalogue identifiers; not recovered native SBC reward ids.
-- Owner-supplied POTM 1: exact existing local catalogue cards, three original challenges.
-- Import only: no live activation, no player/catalogue replacement, no club reset.
SELECT set_config('ultimateteam.content_publish', 'on', true);
INSERT INTO ultimateteam.sbc_sets(set_key,definition) VALUES('POTM_1',$definition$
{
  "id": "POTM_1",
  "tabId": 6,
  "name": "POTM 1",
  "description": "Complete challenges featuring Player of the Month winners from across Europe's top leagues.",
  "repeatable": false,
  "state": "available",
  "rewards": {
    "entries": [
      {
        "type": "PLAYER",
        "attributes": {
          "resourceId": 84139084
        }
      }
    ]
  },
  "challenges": [
    {
      "id": "POTM_1_PREMIER_LEAGUE",
      "number": 1,
      "order": 1,
      "name": "Premier League",
      "description": "Exchange a squad featuring players from the Premier League.",
      "difficulty": 4,
      "requirements": {
        "rules": [
          {
            "type": "PLAYERS",
            "attributes": {
              "min": 11,
              "max": 11
            }
          },
          {
            "type": "RATING",
            "attributes": {
              "min": 84
            }
          },
          {
            "type": "CHEMISTRY",
            "attributes": {
              "min": 70
            }
          },
          {
            "type": "PREMIER_LEAGUE_PLAYERS",
            "attributes": {
              "min": 1
            }
          },
          {
            "type": "TOTW",
            "attributes": {
              "min": 1
            }
          }
        ]
      },
      "rewards": {
        "entries": [
          {
            "type": "PLAYER",
            "attributes": {
              "resourceId": 50553127
            }
          }
        ]
      }
    },
    {
      "id": "POTM_1_BUNDESLIGA",
      "number": 2,
      "order": 2,
      "name": "Bundesliga",
      "description": "Exchange a squad featuring players from the Bundesliga.",
      "difficulty": 4,
      "requirements": {
        "rules": [
          {
            "type": "PLAYERS",
            "attributes": {
              "min": 11,
              "max": 11
            }
          },
          {
            "type": "RATING",
            "attributes": {
              "min": 84
            }
          },
          {
            "type": "CHEMISTRY",
            "attributes": {
              "min": 75
            }
          },
          {
            "type": "BUNDESLIGA_PLAYERS",
            "attributes": {
              "min": 1
            }
          },
          {
            "type": "TOTW",
            "attributes": {
              "min": 1
            }
          }
        ]
      },
      "rewards": {
        "entries": [
          {
            "type": "PLAYER",
            "attributes": {
              "resourceId": 50548002
            }
          }
        ]
      }
    },
    {
      "id": "POTM_1_LIGUE_1",
      "number": 3,
      "order": 3,
      "name": "Ligue 1",
      "description": "Exchange a squad featuring players from Ligue 1.",
      "difficulty": 4,
      "requirements": {
        "rules": [
          {
            "type": "PLAYERS",
            "attributes": {
              "min": 11,
              "max": 11
            }
          },
          {
            "type": "RATING",
            "attributes": {
              "min": 84
            }
          },
          {
            "type": "CHEMISTRY",
            "attributes": {
              "min": 60
            }
          },
          {
            "type": "LIGUE_1_PLAYERS",
            "attributes": {
              "min": 1
            }
          },
          {
            "type": "TOTW",
            "attributes": {
              "min": 1
            }
          }
        ]
      },
      "rewards": {
        "entries": [
          {
            "type": "PLAYER",
            "attributes": {
              "resourceId": 67347889
            }
          }
        ]
      }
    }
  ]
}
$definition$::jsonb) ON CONFLICT(set_key) DO NOTHING;
INSERT INTO ultimateteam.sbc_live(set_key,enabled) VALUES('POTM_1',false) ON CONFLICT(set_key) DO NOTHING;
-- Configuration controls the existing FIFA 21 LEAGUE_ID eligibility key and matching player count.
-- Preserve any existing operator rule at the same key; fill these only when SBC rules already exist.
DO $$ DECLARE rule record; entry record; ord integer; BEGIN
 IF NOT EXISTS(SELECT 1 FROM ultimateteam.content_rules WHERE file='sbc') THEN RETURN; END IF;
 SELECT coalesce(max(ordinal),-1)+1 INTO ord FROM ultimateteam.content_rules WHERE file='sbc';
 FOR rule IN SELECT * FROM (VALUES
 ('PREMIER_LEAGUE_PLAYERS','{"bound": "min", "eligibilityKey": "LeagueId", "eligibilityValue": 13, "scope": "AtLeast", "label": "Premier League players: Min {value}"}'::jsonb),
 ('BUNDESLIGA_PLAYERS','{"bound": "min", "eligibilityKey": "LeagueId", "eligibilityValue": 19, "scope": "AtLeast", "label": "Bundesliga players: Min {value}"}'::jsonb),
 ('LIGUE_1_PLAYERS','{"bound": "min", "eligibilityKey": "LeagueId", "eligibilityValue": 16, "scope": "AtLeast", "label": "Ligue 1 players: Min {value}"}'::jsonb)
 ) rules(key,definition) LOOP
  IF EXISTS(SELECT 1 FROM ultimateteam.content_rules WHERE file='sbc' AND path->>0='requirements' AND path->>1=rule.key) THEN CONTINUE; END IF;
  FOR entry IN SELECT key,value FROM jsonb_each(rule.definition) ORDER BY key LOOP
   INSERT INTO ultimateteam.content_rules(file,path,value,ordinal) VALUES('sbc',jsonb_build_array('requirements',rule.key,entry.key),entry.value,ord);
   ord := ord+1;
  END LOOP;
 END LOOP;
END $$;
SELECT set_config('ultimateteam.content_publish', '', true);
INSERT INTO ultimateteam.schema_migrations(version) VALUES(155);
