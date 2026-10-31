-- Mảnh của câu dài đã được gộp vào mảnh cuối để dịch chung (status = 'Merged').
ALTER TABLE segments ADD COLUMN merged_into TEXT;

CREATE INDEX ix_sessions_started ON sessions(started_at DESC);
