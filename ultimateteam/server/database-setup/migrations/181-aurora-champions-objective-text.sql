-- Rename the visible competition brand in existing objective copy. Change only
-- the original phrase, so separately edited goals, rewards and surrounding
-- operator-written text remain intact. Fresh databases use rules/objectives.json.
UPDATE ultimateteam.content_rules
SET value = to_jsonb(replace(value #>> '{}', 'FUT Champions', 'Aurora Champions')),
    updated_at = now()
WHERE file = 'objectives'
  AND jsonb_typeof(value) = 'string'
  AND path ->> (jsonb_array_length(path) - 1) IN ('title', 'subTitle', 'name', 'description')
  AND strpos(value #>> '{}', 'FUT Champions') > 0;
