CREATE TABLE IF NOT EXISTS telemetry_events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  install_id_hash TEXT NOT NULL,
  event_name TEXT NOT NULL,
  app_version TEXT NOT NULL,
  platform TEXT NOT NULL,
  event_utc TEXT NOT NULL,
  received_utc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_telem_install ON telemetry_events(install_id_hash);
CREATE INDEX IF NOT EXISTS idx_telem_event_utc ON telemetry_events(event_utc);
CREATE INDEX IF NOT EXISTS idx_telem_event_name ON telemetry_events(event_name);
