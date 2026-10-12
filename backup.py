import os,sqlite3,sys
from pathlib import Path
source=Path(os.environ.get('MOOT_STATE_PATH','data/state.sqlite')).resolve()
target=Path(sys.argv[1]).resolve()
if source==target:raise SystemExit('Backup must use a separate path')
if target.exists():raise SystemExit('Choose a new backup filename')
target.parent.mkdir(parents=True,exist_ok=True)
with sqlite3.connect(source) as src,sqlite3.connect(target) as dst:src.backup(dst)
os.chmod(target,0o600)
print('Backup completed')
