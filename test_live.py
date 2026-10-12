import os,sys,json,threading,unittest,urllib.request,time,socket
os.environ['MOOT_STATE_PATH']=':memory:'
import server,uvicorn
from security import app
import test_server
class Live(unittest.TestCase):
 call=test_server.Flow.call
 session=test_server.Flow.session
 @classmethod
 def setUpClass(cls):
  sock=socket.socket();sock.bind(('127.0.0.1',0));port=sock.getsockname()[1];sock.close()
  cls.url='http://127.0.0.1:'+str(port);os.environ['MOOT_PORT']=str(port);server.LIMITS.clear()
  cls.runner=uvicorn.Server(uvicorn.Config(app,host='127.0.0.1',port=port,log_level='error'))
  cls.thread=threading.Thread(target=cls.runner.run,daemon=True);cls.thread.start()
  for _ in range(100):
   if cls.runner.started:break
   time.sleep(.05)
 @classmethod
 def tearDownClass(cls):cls.runner.should_exit=True;cls.thread.join(5)
 def test_live_command_result_chat_and_telemetry(self):
  c,x=self.session();other,ox=self.session()
  _,p,_=self.call('pair',{},c,x);_,d,_=self.call('agent/pair',{'code':p['code'],'name':'Mock PC'});token=d['token']
  self.call('agent/grant',{'consent':True,'scope':'optimization.v1'},bearer=token)
  stream=urllib.request.urlopen(urllib.request.Request(self.url+'/api/agent/events',headers={'Authorization':'Bearer '+token}),timeout=10)
  def nextjob():
   while True:
    line=stream.readline().decode()
    if line.startswith('data: '):return json.loads(line[6:])['jobs'][0]
  with stream:
   self.assertTrue(stream.readline().startswith(b': online'))
   r={'cpuPercent':12,'memoryPercent':55,'memoryGB':16,'cores':4,'cpuName':'Fixture','os':'Test'}
   self.assertEqual(self.call('agent/telemetry',{'result':r},bearer=token)[0],200)
   self.assertEqual(self.call('chat',{'message':'очисти OZU'},c,x)[0],200)
   job=nextjob();self.assertEqual(job['type'],'memory.clean')
   count=len(server.JOBS)
   reply=self.call('chat',{'message':'очистило озу?'},c,x)[1]['reply'];self.assertIn('приняло',reply);self.assertEqual(len(server.JOBS),count)
   result={'status':'complete','summary':'Mock operation completed; no system changes','snapshotId':''}
   self.assertEqual(self.call('agent/result',{'id':job['id'],'result':result},bearer=token)[0],200)
   state=self.call('state',cookie=c)[1];self.assertIn('Mock operation',state['chat'][-1]['text'])
   self.assertEqual(self.call('state',cookie=other)[1]['chat'],[])
   r['cpuPercent']=37;self.call('agent/telemetry',{'result':r},bearer=token)
   self.assertEqual(self.call('state',cookie=c)[1]['devices'][0]['telemetry']['cpuPercent'],37)
   server.persist();self.assertEqual(server.storage.load('chats')[server.SESSIONS[server.digest(c.split('=',1)[1])]['id']][-1]['text'],state['chat'][-1]['text'])
if __name__=='__main__':unittest.main()
