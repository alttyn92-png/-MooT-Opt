"""OAuth authorization code + PKCE; verified OIDC identity tokens."""
import base64, hashlib, json, os, secrets, time, urllib.parse, urllib.request
from google.auth.transport.requests import Request
from google.oauth2 import id_token

FLOWS={}
def configured():return bool(os.getenv('GOOGLE_CLIENT_ID') and os.getenv('GOOGLE_CLIENT_SECRET'))
def required():return os.getenv('REQUIRE_GOOGLE', '1' if os.getenv('APP_ENV')=='production' else '0')=='1'
def start(origin):
    state=secrets.token_urlsafe(32);nonce=secrets.token_urlsafe(32);verifier=secrets.token_urlsafe(48);binding=secrets.token_urlsafe(32)
    for key in list(FLOWS):
        if FLOWS[key]['expires']<time.time():FLOWS.pop(key,None)
    if len(FLOWS)>=5000:raise ValueError('OAuth capacity reached')
    FLOWS[state]={'nonce':nonce,'verifier':verifier,'binding':hashlib.sha256(binding.encode()).hexdigest(),'origin':origin,'expires':time.time()+600}
    query=urllib.parse.urlencode({'client_id':os.environ['GOOGLE_CLIENT_ID'],'redirect_uri':origin+'/auth/google/callback','response_type':'code','scope':'openid email profile','state':state,'nonce':nonce,'code_challenge':base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).decode().rstrip('='),'code_challenge_method':'S256','prompt':'select_account'})
    return 'https://accounts.google.com/o/oauth2/v2/auth?'+query,binding
def finish(query,binding):
    state=query.get('state',[''])[0];flow=FLOWS.pop(state,None)
    if not flow or flow['expires']<time.time() or not secrets.compare_digest(flow['binding'],hashlib.sha256(binding.encode()).hexdigest()):raise ValueError('Invalid OAuth state')
    if query.get('error'):raise ValueError('Login cancelled')
    code=query.get('code',[''])[0]
    if not code or len(code)>4096:raise ValueError('Missing code')
    body=urllib.parse.urlencode({'code':code,'client_id':os.environ['GOOGLE_CLIENT_ID'],'client_secret':os.environ['GOOGLE_CLIENT_SECRET'],'redirect_uri':flow['origin']+'/auth/google/callback','grant_type':'authorization_code','code_verifier':flow['verifier']}).encode()
    req=urllib.request.Request('https://oauth2.googleapis.com/token',body,{'Content-Type':'application/x-www-form-urlencoded'})
    with urllib.request.urlopen(req,timeout=20) as r:data=json.load(r)
    claims=id_token.verify_oauth2_token(data['id_token'],Request(),os.environ['GOOGLE_CLIENT_ID'])
    if claims.get('nonce')!=flow['nonce'] or claims.get('email_verified') is not True:raise ValueError('Unverified identity')
    sub=claims.get('sub')
    if not isinstance(sub,str) or not sub:raise ValueError('Missing subject')
    return sub,str(claims.get('name') or 'Пользователь')[:100]
