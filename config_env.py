"""Load private deployment settings without exposing values to the frontend."""
import os
from pathlib import Path

def load():
    path=Path(os.getenv('MOOT_ENV_FILE',str(Path(__file__).parent/'.env')))
    if not path.is_file():return
    allowed={'ALEM_ENDPOINT','ALEM_MODEL','ALEM_API_KEY','GOOGLE_CLIENT_ID',
        'GOOGLE_CLIENT_SECRET','PUBLIC_ORIGIN','REQUIRE_GOOGLE','APP_ENV',
        'MOOT_PORT','MOOT_PROMO_CODE','TRUSTED_PROXY_IPS'}
    for raw in path.read_text(encoding='utf-8-sig').splitlines():
        line=raw.strip()
        if not line or line.startswith('#') or '=' not in line:continue
        key,value=line.split('=',1);key=key.strip();value=value.strip()
        if key not in allowed:continue
        if len(value)>1 and value[0]==value[-1] and value[0] in ('"',"'"):value=value[1:-1]
        if value:os.environ.setdefault(key,value)
