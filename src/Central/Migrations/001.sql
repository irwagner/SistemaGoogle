CREATE TABLE projects (
 id TEXT PRIMARY KEY, name TEXT NOT NULL, kind TEXT NOT NULL CHECK(kind IN ('software','estudio','pesquisa')),
 classification TEXT NOT NULL DEFAULT 'interno', created TEXT NOT NULL
);
CREATE TABLE tasks (
 id TEXT PRIMARY KEY, project TEXT NOT NULL REFERENCES projects, title TEXT NOT NULL, state TEXT NOT NULL,
 version INTEGER NOT NULL DEFAULT 1, policy TEXT NOT NULL DEFAULT 'local', reason TEXT, created TEXT NOT NULL,
 UNIQUE(project,id)
);
CREATE TABLE dependencies (
 project TEXT NOT NULL, task TEXT NOT NULL, predecessor TEXT NOT NULL, PRIMARY KEY(project,task,predecessor),
 FOREIGN KEY(project,task) REFERENCES tasks(project,id), FOREIGN KEY(project,predecessor) REFERENCES tasks(project,id),
 CHECK(task <> predecessor)
);
CREATE TABLE blobs (hash TEXT PRIMARY KEY, size INTEGER NOT NULL CHECK(size > 0));
CREATE TABLE artifacts (
 id TEXT PRIMARY KEY, project TEXT NOT NULL, task TEXT NOT NULL, name TEXT NOT NULL, type TEXT NOT NULL,
 hash TEXT NOT NULL REFERENCES blobs, origin TEXT NOT NULL, rights TEXT NOT NULL, created TEXT NOT NULL,
 UNIQUE(project,id), FOREIGN KEY(project,task) REFERENCES tasks(project,id)
);
CREATE TABLE events (
 seq INTEGER PRIMARY KEY AUTOINCREMENT, project TEXT NOT NULL REFERENCES projects, kind TEXT NOT NULL,
 detail TEXT NOT NULL, created TEXT NOT NULL
);
CREATE TABLE checkpoints (
 seq INTEGER PRIMARY KEY AUTOINCREMENT, project TEXT NOT NULL, task TEXT NOT NULL,
 version INTEGER NOT NULL, next_action TEXT NOT NULL, details TEXT NOT NULL, created TEXT NOT NULL,
 FOREIGN KEY(project,task) REFERENCES tasks(project,id)
);
CREATE TABLE requests (
 project TEXT NOT NULL, key TEXT NOT NULL, fingerprint TEXT NOT NULL, result TEXT NOT NULL, PRIMARY KEY(project,key)
);
CREATE INDEX artifacts_project ON artifacts(project,created);
CREATE INDEX events_project ON events(project,seq);
