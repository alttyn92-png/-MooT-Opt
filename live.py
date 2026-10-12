"""Async event streams; existing authenticated API runs in the thread pool."""
import asyncio,io,json,os
from starlette.middleware.wsgi import WSGIMiddleware
from starlette.responses import StreamingResponse,JSONResponse
import server
from wsgi import application,RequestHandler

legacy=WSGIMiddleware(application)
listeners={}
loop=None

def notify(owner,telemetry=False):
    current=loop
    if current and not current.is_closed():
        def wake():
            for (identity,is_agent),queues in tuple(listeners.items()):
                if identity==owner and not (telemetry and is_agent):
                    for queue in tuple(queues):
                        if queue.empty():queue.put_nowait(True)
        current.call_soon_threadsafe(wake)
server.CHANGE_HOOK=notify

def handler_for(scope,path):
    env={'REQUEST_METHOD':'GET','PATH_INFO':path,'wsgi.input':io.BytesIO(),'REMOTE_ADDR':scope.get('client',('127.0.0.1',0))[0]}
    for k,v in scope['headers']:env['HTTP_'+k.decode().upper().replace('-','_')]=v.decode('latin1')
    return RequestHandler(env)

async def app(scope,receive,send):
    global loop
    if scope['type']=='lifespan':
        while True:
            message=await receive()
            if message['type']=='lifespan.startup':loop=asyncio.get_running_loop();await send({'type':'lifespan.startup.complete'})
            elif message['type']=='lifespan.shutdown':await send({'type':'lifespan.shutdown.complete'});return
    path=scope.get('path','')
    if scope['type']!='http' or path not in ('/api/events','/api/agent/events'):
        return await legacy(scope,receive,send)
    is_agent=path=='/api/agent/events'
    handler=handler_for(scope,'/api/agent/jobs' if is_agent else '/api/state')
    if scope['method']!='GET' or not handler.valid_host():return await JSONResponse({'error':'Forbidden'},403)(scope,receive,send)
    if not (handler.device() if is_agent else handler.owner()):return await JSONResponse({'error':'Unauthorized'},401)(scope,receive,send)
    if is_agent:handler.headers['X-MooT-Live']='1'
    async def events():
        actor=handler.device() if is_agent else handler.owner()
        key=(actor.get('owner',actor['id']),is_agent)
        queue=asyncio.Queue(maxsize=1);listeners.setdefault(key,set()).add(queue);previous=None;previous_object={}
        try:
            while True:
                if not (handler.device() if is_agent else handler.owner()):return
                def snapshot():
                    h=handler_for(scope,handler.path)
                    if is_agent:h.headers['X-MooT-Live']='1'
                    with server.LOCK:h.do_GET();return h.status,h.wfile.getvalue()
                status,body=await asyncio.to_thread(snapshot)
                if status!=200:return
                # Agents receive jobs as soon as they are queued. Comments keep idle streams alive.
                payload=body.decode()
                changed=payload!=previous
                if is_agent:
                    if json.loads(payload).get('jobs'):yield 'data: '+payload+'\n\n'
                    else:yield ': online\n\n'
                elif changed:
                    value=json.loads(payload)
                    patch={k:v for k,v in value.items() if previous_object.get(k)!=v}
                    previous_object=value
                    yield 'data: '+json.dumps(patch,ensure_ascii=False)+'\n\n'
                else:yield ': online\n\n'
                previous=payload
                try:await asyncio.wait_for(queue.get(),timeout=12)
                except asyncio.TimeoutError:pass
        finally:
            listeners[key].discard(queue)
            if not listeners[key]:listeners.pop(key,None)
    await StreamingResponse(events(),media_type='text/event-stream',headers={'Cache-Control':'no-cache, no-transform','X-Accel-Buffering':'no'})(scope,receive,send)
