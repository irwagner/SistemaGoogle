CREATE TABLE research (
 id TEXT PRIMARY KEY, project TEXT NOT NULL REFERENCES projects, question TEXT NOT NULL, criteria TEXT NOT NULL,
 created TEXT NOT NULL, UNIQUE(project,id)
);
CREATE TABLE sources (
 id TEXT PRIMARY KEY, project TEXT NOT NULL, research TEXT NOT NULL, label TEXT NOT NULL, locator TEXT NOT NULL,
 author TEXT NOT NULL, rights TEXT NOT NULL, excerpt TEXT NOT NULL, consulted TEXT NOT NULL, UNIQUE(project,research,id),
 FOREIGN KEY(project,research) REFERENCES research(project,id)
);
CREATE TABLE claims (
 id TEXT PRIMARY KEY, project TEXT NOT NULL, research TEXT NOT NULL, source TEXT NOT NULL, claim TEXT NOT NULL,
 inference INTEGER NOT NULL, review TEXT, FOREIGN KEY(project,research,source) REFERENCES sources(project,research,id)
);
CREATE TABLE episodes (
 id TEXT PRIMARY KEY, project TEXT UNIQUE NOT NULL REFERENCES projects, title TEXT NOT NULL, created TEXT NOT NULL,
 UNIQUE(project,id)
);
CREATE TABLE bibles (
 episode TEXT NOT NULL REFERENCES episodes, version INTEGER NOT NULL, script TEXT NOT NULL, bible TEXT NOT NULL,
 created TEXT NOT NULL, PRIMARY KEY(episode,version)
);
CREATE TABLE scenes (
 id TEXT PRIMARY KEY, episode TEXT NOT NULL REFERENCES episodes, ordinal INTEGER NOT NULL, description TEXT NOT NULL,
 UNIQUE(episode,ordinal), UNIQUE(episode,id)
);
CREATE TABLE shots (
 id TEXT PRIMARY KEY, episode TEXT NOT NULL, scene TEXT NOT NULL, ordinal INTEGER NOT NULL, direction TEXT NOT NULL,
 frames INTEGER NOT NULL CHECK(frames>0), UNIQUE(episode,ordinal), UNIQUE(episode,id),
 FOREIGN KEY(episode,scene) REFERENCES scenes(episode,id)
);
CREATE TABLE takes (
 id TEXT PRIMARY KEY, project TEXT NOT NULL, episode TEXT NOT NULL, shot TEXT NOT NULL, artifact TEXT NOT NULL,
 bible_version INTEGER NOT NULL, parameters TEXT NOT NULL, disposition TEXT NOT NULL, review TEXT, created TEXT NOT NULL,
 FOREIGN KEY(episode,shot) REFERENCES shots(episode,id), FOREIGN KEY(project,episode) REFERENCES episodes(project,id),
 FOREIGN KEY(project,artifact) REFERENCES artifacts(project,id), FOREIGN KEY(episode,bible_version) REFERENCES bibles(episode,version)
);
CREATE UNIQUE INDEX one_take_per_shot ON takes(shot) WHERE disposition='selecionado';
CREATE TABLE tracks (
 id TEXT PRIMARY KEY, project TEXT NOT NULL, episode TEXT NOT NULL, artifact TEXT NOT NULL,
 role TEXT NOT NULL, voice TEXT NOT NULL, gain REAL NOT NULL, start_frame INTEGER NOT NULL,
 FOREIGN KEY(project,episode) REFERENCES episodes(project,id), FOREIGN KEY(project,artifact) REFERENCES artifacts(project,id)
);
CREATE TABLE edits (
 id TEXT PRIMARY KEY, project TEXT NOT NULL, episode TEXT NOT NULL, manifest TEXT NOT NULL,
 state TEXT NOT NULL, master TEXT, probe TEXT, created TEXT NOT NULL, review TEXT,
 FOREIGN KEY(project,episode) REFERENCES episodes(project,id), FOREIGN KEY(project,master) REFERENCES artifacts(project,id)
);
