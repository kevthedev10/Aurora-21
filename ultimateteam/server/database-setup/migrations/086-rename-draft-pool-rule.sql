-- Keep the operator's current Draft setting while naming the scheduled card pool accurately.
UPDATE ultimateteam.content_rules
SET path = '["picks","scheduledPool"]'::jsonb
WHERE file = 'draft' AND path = '["picks","phasePool"]'::jsonb
  AND NOT EXISTS (
      SELECT 1 FROM ultimateteam.content_rules existing
      WHERE existing.file = 'draft' AND existing.path = '["picks","scheduledPool"]'::jsonb
  );
DELETE FROM ultimateteam.content_rules
WHERE file = 'draft' AND path = '["picks","phasePool"]'::jsonb;

INSERT INTO ultimateteam.schema_migrations(version) VALUES(86);
