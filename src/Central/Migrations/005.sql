CREATE TABLE work_items (
 id TEXT PRIMARY KEY, project TEXT NOT NULL REFERENCES projects(id), area TEXT NOT NULL, mode TEXT NOT NULL,
 title TEXT NOT NULL, request TEXT NOT NULL, plan TEXT NOT NULL DEFAULT '', plan_source TEXT NOT NULL DEFAULT '',
 executor TEXT NOT NULL DEFAULT 'assistido', model TEXT NOT NULL DEFAULT '', state TEXT NOT NULL DEFAULT 'rascunho',
 phase TEXT NOT NULL DEFAULT '', note TEXT NOT NULL DEFAULT '', revision INTEGER NOT NULL DEFAULT 1,
 priority INTEGER NOT NULL DEFAULT 5 CHECK(priority BETWEEN 0 AND 9), follows TEXT,
 created TEXT NOT NULL, updated TEXT NOT NULL, queued_at TEXT, started_at TEXT, finished_at TEXT,
 UNIQUE(project,id), FOREIGN KEY(project,follows) REFERENCES work_items(project,id)
);
CREATE INDEX work_queue ON work_items(state,priority,queued_at);
CREATE INDEX work_in_project ON work_items(project,updated);
CREATE TABLE work_messages (
 sequence INTEGER PRIMARY KEY AUTOINCREMENT, work TEXT NOT NULL REFERENCES work_items(id),
 author TEXT NOT NULL, body TEXT NOT NULL, created TEXT NOT NULL
);
CREATE TABLE work_files (
 project TEXT NOT NULL, work TEXT NOT NULL, asset TEXT NOT NULL, role TEXT NOT NULL,
 PRIMARY KEY(work,asset), FOREIGN KEY(project,work) REFERENCES work_items(project,id),
 FOREIGN KEY(project,asset) REFERENCES artifacts(project,id)
);
CREATE TABLE work_runs (
 id TEXT PRIMARY KEY, work TEXT NOT NULL REFERENCES work_items(id), phase TEXT NOT NULL, state TEXT NOT NULL,
 worker TEXT NOT NULL, queued_at TEXT NOT NULL, started_at TEXT NOT NULL, ended_at TEXT
);
CREATE TABLE work_drafts (
 work TEXT NOT NULL REFERENCES work_items(id), field TEXT NOT NULL, revision INTEGER NOT NULL, body TEXT NOT NULL,
 PRIMARY KEY(work,field)
);
CREATE TABLE video_stories (
 work TEXT PRIMARY KEY REFERENCES work_items(id), characters TEXT NOT NULL, storyboard TEXT NOT NULL,
 script TEXT NOT NULL, approved INTEGER NOT NULL DEFAULT 0, updated TEXT NOT NULL
);
CREATE TABLE deliveries (
 id TEXT PRIMARY KEY, project TEXT NOT NULL REFERENCES projects(id), work TEXT NOT NULL, media TEXT NOT NULL,
 cover TEXT, caption TEXT, title TEXT NOT NULL, description TEXT NOT NULL, channel TEXT NOT NULL,
 visibility TEXT NOT NULL, state TEXT NOT NULL, created TEXT NOT NULL, updated TEXT NOT NULL,
 remote_id TEXT, views INTEGER, cost_minor INTEGER, updated_by TEXT,
 FOREIGN KEY(project,work) REFERENCES work_items(project,id), FOREIGN KEY(project,media) REFERENCES artifacts(project,id),
 FOREIGN KEY(project,cover) REFERENCES artifacts(project,id), FOREIGN KEY(project,caption) REFERENCES artifacts(project,id)
);
