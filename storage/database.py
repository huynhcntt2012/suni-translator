import json
import sqlite3
import threading
from engine.config import DATA, Settings

class Database:
    def __init__(self, path=None):
        path = path or DATA / "suni.sqlite3"
        path.parent.mkdir(parents=True, exist_ok=True)
        self.lock = threading.RLock()
        self.db = sqlite3.connect(path, check_same_thread=False)
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.executescript('''
            CREATE TABLE IF NOT EXISTS settings (id INTEGER PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions (id INTEGER PRIMARY KEY, started TEXT DEFAULT CURRENT_TIMESTAMP, target TEXT, mode TEXT);
            CREATE TABLE IF NOT EXISTS segments (id INTEGER PRIMARY KEY, session INTEGER, start REAL, end REAL, language TEXT, original TEXT, translated TEXT, latency REAL);
        ''')
        self.db.commit()

    def settings(self):
        with self.lock:
            row = self.db.execute("SELECT value FROM settings WHERE id=1").fetchone()
        return Settings.from_dict(json.loads(row[0])) if row else Settings()

    def save_settings(self, settings):
        with self.lock, self.db:
            self.db.execute("INSERT OR REPLACE INTO settings VALUES (1,?)", (json.dumps(settings.dict()),))

    def session(self, target, mode):
        with self.lock, self.db:
            return self.db.execute("INSERT INTO sessions(target,mode) VALUES (?,?)", (target, mode)).lastrowid

    def append(self, session, segment):
        with self.lock, self.db:
            self.db.execute("INSERT INTO segments(session,start,end,language,original,translated,latency) VALUES (?,?,?,?,?,?,?)", (session, segment.start, segment.end, segment.language, segment.original, segment.translated, segment.latency))

    def history(self, session=None, limit=500):
        with self.lock:
            if session is not None:
                return self.db.execute("SELECT start,end,language,original,translated,latency FROM segments WHERE session=? ORDER BY start", (session,)).fetchall()
            return self.db.execute("SELECT start,end,language,original,translated,latency FROM segments ORDER BY id DESC LIMIT ?", (limit,)).fetchall()

    def sessions(self):
        with self.lock:
            return self.db.execute("SELECT id,started,target,mode FROM sessions ORDER BY id DESC LIMIT 100").fetchall()

    def close(self):
        with self.lock:
            self.db.close()
