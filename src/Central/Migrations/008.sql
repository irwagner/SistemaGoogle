CREATE TABLE audio_reviews (
 work TEXT NOT NULL, project TEXT NOT NULL, asset TEXT NOT NULL, role TEXT NOT NULL,
 seconds REAL NOT NULL, approved INTEGER NOT NULL, note TEXT NOT NULL, updated TEXT NOT NULL,
 PRIMARY KEY(work,asset), FOREIGN KEY(project,work) REFERENCES work_items(project,id),
 FOREIGN KEY(project,asset) REFERENCES artifacts(project,id)
);
CREATE TABLE google_profiles (
 id TEXT PRIMARY KEY, product TEXT NOT NULL, label TEXT NOT NULL, cloud TEXT NOT NULL, expected TEXT NOT NULL,
 identity TEXT, subject TEXT, verified TEXT
);
CREATE TABLE notebook_jobs (
 id TEXT PRIMARY KEY, work TEXT NOT NULL, project TEXT NOT NULL, account TEXT NOT NULL REFERENCES google_profiles(id),
 destination TEXT NOT NULL, label TEXT NOT NULL, asset TEXT, body TEXT, state TEXT NOT NULL, remote TEXT,
 authorization TEXT NOT NULL, updated TEXT NOT NULL, error TEXT, UNIQUE(work,account,destination,label),
 FOREIGN KEY(project,work) REFERENCES work_items(project,id), FOREIGN KEY(project,asset) REFERENCES artifacts(project,id)
);
ALTER TABLE deliveries ADD COLUMN remote_account TEXT REFERENCES google_profiles(id);
ALTER TABLE deliveries ADD COLUMN remote_policy TEXT;
CREATE TABLE work_questions (
 work TEXT PRIMARY KEY, project TEXT NOT NULL, question TEXT NOT NULL,
 FOREIGN KEY(project,work) REFERENCES work_items(project,id),
 FOREIGN KEY(project,question) REFERENCES research(project,id)
);
