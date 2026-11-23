CREATE TABLE accounts (id TEXT PRIMARY KEY, owner_label TEXT NOT NULL);
CREATE TABLE connections (
 id TEXT PRIMARY KEY, account TEXT NOT NULL REFERENCES accounts, product TEXT NOT NULL,
 observed_identity TEXT, status TEXT NOT NULL, profile TEXT NOT NULL, concurrency INTEGER NOT NULL CHECK(concurrency>0),
 isolated INTEGER NOT NULL, overage_off INTEGER, version TEXT NOT NULL, verified_until INTEGER
);
CREATE TABLE entitlements (
 connection TEXT NOT NULL REFERENCES connections, model TEXT NOT NULL, version TEXT NOT NULL,
 retired INTEGER NOT NULL, PRIMARY KEY(connection,model)
);
CREATE TABLE quota_subjects (id TEXT PRIMARY KEY, kind TEXT NOT NULL);
CREATE TABLE quota_keys (
 id TEXT PRIMARY KEY, subject TEXT NOT NULL REFERENCES quota_subjects, product TEXT NOT NULL,
 bucket TEXT NOT NULL, unit TEXT NOT NULL, UNIQUE(subject,product,bucket,unit)
);
CREATE TABLE quota_windows (
 quota TEXT NOT NULL REFERENCES quota_keys, window TEXT NOT NULL, PRIMARY KEY(quota,window)
);
CREATE TABLE quota_observations (
 seq INTEGER PRIMARY KEY AUTOINCREMENT, quota TEXT NOT NULL, window TEXT NOT NULL, remaining INTEGER,
 status TEXT NOT NULL, observed INTEGER NOT NULL, expires INTEGER NOT NULL, reset INTEGER, source TEXT NOT NULL,
 FOREIGN KEY(quota,window) REFERENCES quota_windows(quota,window)
);
CREATE TABLE constraints_map (
 connection TEXT NOT NULL, model TEXT NOT NULL, quota TEXT NOT NULL, window TEXT NOT NULL,
 PRIMARY KEY(connection,model,quota,window),
 FOREIGN KEY(connection,model) REFERENCES entitlements(connection,model),
 FOREIGN KEY(quota,window) REFERENCES quota_windows(quota,window)
);
CREATE TABLE wallets (
 id TEXT PRIMARY KEY, unit TEXT NOT NULL, ceiling INTEGER CHECK(ceiling >= 0), spent INTEGER NOT NULL CHECK(spent>=0)
);
CREATE TABLE effects (
 id TEXT PRIMARY KEY, project TEXT NOT NULL, task TEXT NOT NULL, logical_key TEXT NOT NULL,
 payload_hash TEXT NOT NULL, input_version INTEGER NOT NULL, connection TEXT REFERENCES connections,
 identity TEXT, model TEXT, catalog_version TEXT, state TEXT NOT NULL, remote_scope TEXT, remote_id TEXT,
 created TEXT NOT NULL, UNIQUE(project,logical_key), UNIQUE(remote_scope,remote_id),
 FOREIGN KEY(project,task) REFERENCES tasks(project,id)
);
CREATE TABLE leases (
 effect TEXT PRIMARY KEY REFERENCES effects, owner TEXT NOT NULL, fence INTEGER NOT NULL,
 expires INTEGER NOT NULL, heartbeat INTEGER NOT NULL, profile TEXT NOT NULL
);
CREATE TABLE attempts (
 effect TEXT NOT NULL REFERENCES effects, fence INTEGER NOT NULL, worker TEXT NOT NULL, state TEXT NOT NULL,
 started INTEGER NOT NULL, PRIMARY KEY(effect,fence)
);
CREATE TABLE reservations (
 effect TEXT NOT NULL REFERENCES effects, resource TEXT NOT NULL, window TEXT NOT NULL, unit TEXT NOT NULL,
 amount INTEGER NOT NULL CHECK(amount>=0), expires INTEGER, PRIMARY KEY(effect,resource,window)
);
CREATE TABLE ledger (
 seq INTEGER PRIMARY KEY AUTOINCREMENT, effect TEXT NOT NULL REFERENCES effects, wallet TEXT NOT NULL REFERENCES wallets,
 kind TEXT NOT NULL, amount INTEGER, unit TEXT NOT NULL, certainty TEXT NOT NULL, created TEXT NOT NULL
);
CREATE TABLE receipts (
 seq INTEGER PRIMARY KEY AUTOINCREMENT, effect TEXT NOT NULL REFERENCES effects, identity TEXT,
 kind TEXT NOT NULL, remote_id TEXT, created TEXT NOT NULL
);
CREATE TABLE service_flags (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE INDEX effects_project ON effects(project,state);
