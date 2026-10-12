import os
from config_env import load
load()
from urllib.parse import urlparse
import uvicorn
import google_login
from security import app

if __name__=='__main__':
    production=os.getenv('APP_ENV')=='production'
    if production:
        if urlparse(os.getenv('PUBLIC_ORIGIN','')).scheme!='https':raise SystemExit('Production requires PUBLIC_ORIGIN with HTTPS')
        if not google_login.configured() or not google_login.required():raise SystemExit('Production requires Google OAuth and REQUIRE_GOOGLE=1')
        if not all(os.getenv(k) for k in ('ALEM_ENDPOINT','ALEM_MODEL','ALEM_API_KEY')):raise SystemExit('Production requires the model provider configuration')
    uvicorn.run(app,host='0.0.0.0' if production else '127.0.0.1',port=int(os.getenv('MOOT_PORT','8765')),proxy_headers=production,forwarded_allow_ips=os.getenv('TRUSTED_PROXY_IPS','127.0.0.1') if production else '',access_log=False,log_level='warning',timeout_keep_alive=10,limit_concurrency=180,backlog=128,server_header=False,timeout_graceful_shutdown=15)
