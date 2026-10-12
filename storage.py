"""SQLite persistence for one application process; credentials are never stored here."""
import json, os, secrets, sqlite3, threading
from pathlib import Path

PATH=os.getenv('MOOT_STATE_PATH',str(Path(__file__).parent/'data'/'state.sqlite'))
if PATH!=':memory:':Path(PATH).parent.mkdir(parents=True,exist_ok=True)
DB=sqlite3.connect(PATH,check_same_thread=False)
DB.execute('PRAGMA journal_mode=WAL')
DB.execute('CREATE TABLE IF NOT EXISTS runtime (name TEXT PRIMARY KEY, value TEXT NOT NULL)')
DB.execute('CREATE TABLE IF NOT EXISTS users (subject TEXT PRIMARY KEY, public_id TEXT UNIQUE NOT NULL, name TEXT NOT NULL)')
DB.commit(); DB_LOCK=threading.RLock()

def load(name):
    with DB_LOCK:
        row=DB.execute('SELECT value FROM runtime WHERE name=?',(name,)).fetchone()
        return json.loads(row[0]) if row else {}

def save(names):
    with DB_LOCK,DB:
        for name,value in names.items():
            DB.execute('INSERT INTO runtime VALUES (?,?) ON CONFLICT(name) DO UPDATE SET value=excluded.value',(name,json.dumps(value,ensure_ascii=False)))

def account(subject,name):
    with DB_LOCK,DB:
        row=DB.execute('SELECT public_id FROM users WHERE subject=?',(subject,)).fetchone()
        if row:
            public_id=row[0];DB.execute('UPDATE users SET name=? WHERE subject=?',(name,subject))
        else:
            for _ in range(100):
                public_id=str(1000000+secrets.randbelow(9000000))
                try:
                    DB.execute('INSERT INTO users VALUES (?,?,?)',(subject,public_id,name));break
                except sqlite3.IntegrityError:continue
            else:raise RuntimeError('Account allocation failed')
        return {'id':public_id,'name':name}

# Entitlements belong to account subjects, never expiring browser sessions.
with DB_LOCK,DB:
    DB.execute('CREATE TABLE IF NOT EXISTS entitlements (subject TEXT PRIMARY KEY, enabled INTEGER NOT NULL)')
def full_mode(subject):
    with DB_LOCK:
        row=DB.execute('SELECT enabled FROM entitlements WHERE subject=?',(subject,)).fetchone()
        return bool(row and row[0])
def activate_full_mode(subject):
    with DB_LOCK,DB:
        DB.execute('INSERT INTO entitlements VALUES (?,1) ON CONFLICT(subject) DO UPDATE SET enabled=1',(subject,))
