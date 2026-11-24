CREATE TABLE work_scopes (
 work TEXT PRIMARY KEY, project TEXT NOT NULL, scope TEXT NOT NULL UNIQUE REFERENCES projects(id),
 FOREIGN KEY(project,work) REFERENCES work_items(project,id)
);
CREATE TABLE scope_imports (
 scope TEXT NOT NULL REFERENCES work_scopes(scope), source TEXT NOT NULL REFERENCES artifacts(id),
 asset TEXT NOT NULL REFERENCES artifacts(id), PRIMARY KEY(scope,source)
);
