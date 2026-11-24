ALTER TABLE effects ADD COLUMN demand_signature TEXT;
ALTER TABLE work_items ADD COLUMN connection_id TEXT REFERENCES connections(id);
