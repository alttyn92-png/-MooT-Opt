"""Bounded ASGI ingress. Limits apply before request bodies reach the WSGI pool."""
import asyncio,collections,json,math,time
from starlette.responses import JSONResponse
import server
from live import app as inner,handler_for

class BucketStore:
    def __init__(self,capacity=20000):self.rows=collections.OrderedDict();self.capacity=capacity
    def take(self,key,rate,burst,now=None):
        now=time.monotonic() if now is None else now
        tokens,last=self.rows.pop(key,(float(burst),now));tokens=min(burst,tokens+max(0,now-last)*rate)
        allowed=tokens>=1
        if allowed:tokens-=1
        self.rows[key]=(tokens,now)
        while len(self.rows)>self.capacity:self.rows.popitem(last=False)
        return 0 if allowed else max(1,math.ceil((1-tokens)/rate))

class Protection:
    def __init__(self,application):
        self.application=application;self.buckets=BucketStore();self.streams={};self.chats=set();self.downloads=0
    async def __call__(self,scope,receive,send):
        if scope['type']!='http':return await self.application(scope,receive,send)
        headers=dict(scope['headers']);path=scope.get('path','');method=scope['method'];ip=scope.get('client',('unknown',0))[0]
        async def secure_send(message):
            if message['type']=='http.response.start':
                extra=[(b'x-content-type-options',b'nosniff'),(b'x-frame-options',b'DENY'),(b'referrer-policy',b'same-origin'),(b'permissions-policy',b'camera=(), microphone=(), geolocation=()'),(b'cross-origin-resource-policy',b'same-origin')]
                existing={k.lower() for k,v in message.get('headers',[])}
                message['headers']=message.get('headers',[])+[(k,v) for k,v in extra if k not in existing]
            await send(message)
        async def reject(status,text,retry=0):
            await JSONResponse({'error':text},status,headers={'Cache-Control':'no-store',**({'Retry-After':str(retry)} if retry else {})})(scope,receive,secure_send)
        critical={b'host',b'authorization',b'cookie',b'content-length',b'origin',b'x-csrf-token'}
        if any(sum(1 for k,v in scope['headers'] if k==name)>1 for name in critical):return await reject(400,'Повторяющиеся заголовки')
        if len(scope.get('query_string',b''))>8192 or sum(len(k)+len(v) for k,v in scope['headers'])>16384:return await reject(431,'Слишком большой запрос')
        h=handler_for(scope,path)
        if not h.valid_host():return await reject(403,'Host rejected')
        if method not in ('GET','POST'):return await reject(405,'Метод не разрешён')
        wait=self.buckets.take(('ip',ip),10,100)
        if wait:return await reject(429,'Слишком много запросов. Подождите.',wait)
        actor=h.device() if path.startswith('/api/agent/') else h.owner()
        public=path in ('/api/config','/api/session','/api/agent/pair') or not path.startswith('/api/')
        if not actor and not public:return await reject(401,'Войдите в аккаунт или подключите приложение')
        if method=='POST':
            origin=headers.get(b'origin',b'').decode('latin1')
            if origin and origin!=h.origin():return await reject(403,'Origin rejected')
            if actor and not path.startswith('/api/agent/') and headers.get(b'x-csrf-token',b'').decode('latin1')!=actor['csrf']:return await reject(403,'CSRF rejected')
        identity=actor['id'] if actor else ip
        rules={'/api/promo':(5/60,2),'/api/chat':(6/60,3),'/api/pair':(5/60,2),'/api/session':(5/60,3),'/api/agent/pair':(5/60,3),'/api/agent/telemetry':(30/60,4),'/api/state':(60/60,10),'/api/agent/jobs':(60/60,10),'/auth/google/start':(10/60,3),'/auth/google/callback':(10/60,3),'/api/events':(10/60,4),'/api/agent/events':(10/60,4),'/download/MooT-Setup.exe':(3/60,2)}
        rate,burst=rules.get(path,(2,10) if method=='POST' else (4,30))
        route_key=path if path in rules else ('write' if method=='POST' else 'read')
        wait=self.buckets.take((route_key,identity),rate,burst)
        if wait:return await reject(429,'Лимит запросов. Повторите позже.',wait)
        stream=path in ('/api/events','/api/agent/events');chat=path=='/api/chat';download=path=='/download/MooT-Setup.exe'
        stream_key=(path,identity)
        if stream and (sum(self.streams.values())>=128 or self.streams.get(stream_key,0)>=(1 if path.startswith('/api/agent/') else 3)):
            return await reject(429,'Достигнут лимит живых подключений. Закройте лишние вкладки.',15)
        if chat and (identity in self.chats or len(self.chats)>=4):return await reject(429,'Ассистент занят. Дождитесь ответа.',10)
        if download and self.downloads>=4:return await reject(503,'Скачивание занято. Повторите позже.',15)
        if chat:
            wait=self.buckets.take(('daily-chat',identity),120/86400,120)
            if wait:return await reject(429,'Лимит сообщений исчерпан. Повторите позже.',wait)
        # Bound both known-length and chunked bodies, with an absolute read deadline.
        if method=='POST':
            try:
                length=int(headers.get(b'content-length',b'0'))
                if length<0 or length>48000:return await reject(413,'Максимальный размер запроса — 48 КБ')
                if headers.get(b'content-type',b'').split(b';')[0]!=b'application/json':return await reject(415,'Требуется application/json')
                body=bytearray()
                async with asyncio.timeout(10):
                    while True:
                        message=await receive()
                        if message['type']=='http.disconnect':return
                        body.extend(message.get('body',b''))
                        if len(body)>48000:return await reject(413,'Максимальный размер запроса — 48 КБ')
                        if not message.get('more_body'):break
                # Reject NaN, infinity and deeply nested documents before dispatch.
                json.loads(body,parse_constant=lambda _:(_ for _ in ()).throw(ValueError()))
            except TimeoutError:return await reject(408,'Время отправки запроса истекло')
            except (ValueError,RecursionError):return await reject(400,'Некорректный JSON')
            scope={**scope,'headers':[(k,v) for k,v in scope['headers'] if k!=b'content-length']+[(b'content-length',str(len(body)).encode())]}
            original_receive=receive;delivered=False
            async def buffered_receive():
                nonlocal delivered
                if not delivered:delivered=True;return {'type':'http.request','body':bytes(body),'more_body':False}
                return await original_receive()
            receive=buffered_receive
        # Recheck after awaiting the body: another request may have reserved a slot.
        if chat and (identity in self.chats or len(self.chats)>=4):return await reject(429,'Ассистент занят. Дождитесь ответа.',10)
        if stream:self.streams[stream_key]=self.streams.get(stream_key,0)+1
        if chat:self.chats.add(identity)
        if download:self.downloads+=1
        try:await self.application(scope,receive,secure_send)
        finally:
            if stream:
                self.streams[stream_key]-=1
                if not self.streams[stream_key]:del self.streams[stream_key]
            if chat:self.chats.discard(identity)
            if download:self.downloads-=1

app=Protection(inner)
