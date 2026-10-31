CREATE TABLE sessions(
  id TEXT PRIMARY KEY,
  started_at INTEGER NOT NULL,
  ended_at INTEGER,
  source_app TEXT,
  source_pid INTEGER,
  capture_mode TEXT NOT NULL,
  source_lang TEXT,
  target_lang TEXT NOT NULL,
  asr_engine TEXT,
  asr_model TEXT,
  ollama_url_kind TEXT,
  ollama_model TEXT,
  status TEXT NOT NULL,
  title TEXT
);

CREATE TABLE segments(
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
  seq INTEGER NOT NULL,
  t_start_ms INTEGER NOT NULL,
  t_end_ms INTEGER NOT NULL,
  text_original TEXT,
  text_translated TEXT,
  text_translated_edited TEXT,
  lang_detected TEXT,
  lang_prob REAL,
  is_final INTEGER NOT NULL,
  status TEXT NOT NULL,
  asr_ms INTEGER,
  llm_ms INTEGER,
  error_code TEXT,
  UNIQUE(session_id, seq)
);

CREATE INDEX ix_segments_session ON segments(session_id, seq);
CREATE INDEX ix_segments_pending ON segments(status) WHERE status IN ('TranslatePending', 'TranslateFailed');

CREATE TABLE jobs(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  segment_id TEXT NOT NULL REFERENCES segments(id) ON DELETE CASCADE,
  stage TEXT NOT NULL,
  retry_count INTEGER NOT NULL DEFAULT 0,
  started_at INTEGER,
  finished_at INTEGER,
  last_error TEXT
);

CREATE TABLE glossary(
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  source_lang TEXT,
  target_lang TEXT,
  source_term TEXT NOT NULL,
  target_term TEXT NOT NULL,
  case_sensitive INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE settings(
  key TEXT PRIMARY KEY,
  value_json TEXT NOT NULL,
  updated_at INTEGER NOT NULL
);

-- Tìm kiếm toàn văn cho Việt/CJK (FR-18).
CREATE VIRTUAL TABLE segments_fts USING fts5(
  text_original, text_translated, text_translated_edited,
  content='segments', content_rowid='rowid', tokenize='trigram'
);

CREATE TRIGGER segments_ai AFTER INSERT ON segments BEGIN
  INSERT INTO segments_fts(rowid, text_original, text_translated, text_translated_edited)
  VALUES (new.rowid, new.text_original, new.text_translated, new.text_translated_edited);
END;

CREATE TRIGGER segments_ad AFTER DELETE ON segments BEGIN
  INSERT INTO segments_fts(segments_fts, rowid, text_original, text_translated, text_translated_edited)
  VALUES ('delete', old.rowid, old.text_original, old.text_translated, old.text_translated_edited);
END;

CREATE TRIGGER segments_au AFTER UPDATE ON segments BEGIN
  INSERT INTO segments_fts(segments_fts, rowid, text_original, text_translated, text_translated_edited)
  VALUES ('delete', old.rowid, old.text_original, old.text_translated, old.text_translated_edited);
  INSERT INTO segments_fts(rowid, text_original, text_translated, text_translated_edited)
  VALUES (new.rowid, new.text_original, new.text_translated, new.text_translated_edited);
END;
