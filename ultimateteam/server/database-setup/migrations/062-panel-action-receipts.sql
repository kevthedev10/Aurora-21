-- Durable idempotency for operator grants, balance deltas and custom pack creation.
-- The row is inserted and completed in the same transaction as the action. A retry
-- with the same request id returns the original result without applying it twice.
CREATE TABLE IF NOT EXISTS ultimateteam.panel_action_receipts (
    request_id text PRIMARY KEY,
    operation text NOT NULL,
    payload_hash text NOT NULL,
    result jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT panel_action_request_id_length CHECK (length(request_id) BETWEEN 22 AND 128)
);

INSERT INTO ultimateteam.schema_migrations(version) VALUES(62);
