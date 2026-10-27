-- The read-only role postgres-exporter logs in with (compose service "monitor-role", monitoring profile).
-- pg_monitor reads statistics views only; it cannot read club tables. Idempotent: it also resets the password
-- to MONITOR_DB_PASSWORD, read with \getenv so the value never appears on a command line.
\set ON_ERROR_STOP on
\getenv monitor_password MONITOR_DB_PASSWORD
SELECT format('CREATE ROLE aurora_monitor LOGIN PASSWORD %L', :'monitor_password')
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'aurora_monitor') \gexec
SELECT format('ALTER ROLE aurora_monitor WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', :'monitor_password') \gexec
GRANT pg_monitor TO aurora_monitor;
GRANT CONNECT ON DATABASE ultimateteam TO aurora_monitor;
