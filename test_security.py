import os,asyncio,json,unittest,time
os.environ['MOOT_STATE_PATH']=':memory:'
import server
from security import Protection,BucketStore

class Security(unittest.IsolatedAsyncioTestCase):
 def setUp(self):
  os.environ.pop('PUBLIC_ORIGIN',None);os.environ['MOOT_PORT']='8765'
  server.SESSIONS[server.digest('fixture')]={'id':'security-user','csrf':'csrf','expires':time.time()+1000}
  self.called=0
  async def downstream(scope,receive,send):
   self.called+=1
   await send({'type':'http.response.start','status':200,'headers':[]})
   await send({'type':'http.response.body','body':b'{}'})
  self.guard=Protection(downstream)
 def tearDown(self):
  server.SESSIONS.pop(server.digest('fixture'),None);server.DEVICES.pop(server.digest('agent-fixture'),None)
 async def request(self,path='/api/chat',body=b'{}',extra=(),method='POST',ip='127.0.0.1'):
  headers=[(b'host',b'127.0.0.1:8765'),(b'cookie',b'moot=fixture'),(b'x-csrf-token',b'csrf'),(b'content-type',b'application/json')]
  headers=[(k,v) for k,v in headers if k not in dict(extra)]+list(extra);out=[];sent=False
  async def receive():
   nonlocal sent
   if not sent:sent=True;return {'type':'http.request','body':body,'more_body':False}
   return {'type':'http.disconnect'}
  async def send(msg):out.append(msg)
  await self.guard({'type':'http','path':path,'method':method,'headers':headers,'client':(ip,123),'query_string':b''},receive,send)
  return out[0],out
 async def test_chat_rate_limit(self):
  for _ in range(3):self.assertEqual((await self.request())[0]['status'],200)
  start,_=await self.request();self.assertEqual(start['status'],429);self.assertIn(b'retry-after',dict(start['headers']));self.assertEqual(self.called,3)
 async def test_csrf_before_dispatch(self):self.assertEqual((await self.request(extra=[(b'x-csrf-token',b'wrong')]))[0]['status'],403);self.assertEqual(self.called,0)
 async def test_oversized_chunked_body(self):self.assertEqual((await self.request(body=b'x'*48001))[0]['status'],413);self.assertEqual(self.called,0)
 async def test_non_finite_json(self):self.assertEqual((await self.request(body=b'{"v":NaN}'))[0]['status'],400)
 async def test_origin(self):self.assertEqual((await self.request(extra=[(b'origin',b'https://evil.invalid')]))[0]['status'],403)
 async def test_unauthenticated(self):self.assertEqual((await self.request(extra=[(b'cookie',b'')]))[0]['status'],401)
 async def test_busy_chat(self):
  self.guard.chats.add('security-user');self.assertEqual((await self.request())[0]['status'],429);self.assertEqual(self.called,0)
 async def test_live_limit(self):
  self.guard.streams[('/api/events','security-user')]=3
  self.assertEqual((await self.request('/api/events',method='GET'))[0]['status'],429)
 async def test_security_headers_and_slot_cleanup(self):
  first,_=await self.request();headers=dict(first['headers']);self.assertEqual(headers[b'x-frame-options'],b'DENY');self.assertFalse(self.guard.chats)
 async def test_telemetry_has_separate_budget(self):
  server.DEVICES[server.digest('agent-fixture')]={'id':'device-fixture','active':True,'expires':time.time()+1000}
  for _ in range(3):await self.request()
  response,_=await self.request('/api/agent/telemetry',extra=[(b'authorization',b'Bearer agent-fixture')]);self.assertEqual(response['status'],200)
 def test_bounded_buckets_and_refill(self):
  store=BucketStore(10);self.assertEqual(store.take('a',1,1,0),0);self.assertEqual(store.take('a',1,1,.1),1);self.assertEqual(store.take('a',1,1,1),0)
  for n in range(100):store.take(n,1,1,1)
  self.assertEqual(len(store.rows),10)
if __name__=='__main__':unittest.main()
