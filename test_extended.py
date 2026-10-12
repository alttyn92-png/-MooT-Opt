import unittest,time,json,os
os.environ['MOOT_STATE_PATH']=':memory:'
import test_server,server
from unittest.mock import patch
class Extended(test_server.Flow):
 def test_named_file_and_no_guessing(self):
  c,x=self.session();_,p,_=self.call('pair',{},c,x);_,d,_=self.call('agent/pair',{'code':p['code'],'name':'fixture'});t=d['token'];self.call('agent/grant',{'consent':True,'scope':'optimization.v1'},bearer=t)
  before=len(server.JOBS);self.call('chat',{'message':'удали файл'},c,x);self.assertEqual(len(server.JOBS),before)
  self.call('promo',{'code':'MOOT'},c,x)
  self.call('chat',{'message':'удали файл «fixture.txt»'},c,x)
  job=self.call('agent/jobs',bearer=t)[1]['jobs'][0];self.assertEqual(job['type'],'file.recycle');self.assertEqual(job['location'],'auto');self.assertEqual(job['fileName'],'fixture.txt')
  self.assertEqual(self.call('agent/result',{'id':job['id'],'result':{'status':'failed','summary':'fixture declined; no file changes','snapshotId':''}},bearer=t)[0],200)
 def test_apps_and_reviewed_scenario_routing(self):
  c,x=self.session();_,p,_=self.call('pair',{},c,x);_,d,_=self.call('agent/pair',{'code':p['code'],'name':'fixture'});t=d['token'];self.call('agent/grant',{'consent':True,'scope':'optimization.v1'},bearer=t)
  self.assertEqual(self.call('promo',{'code':'wrong'},c,x)[0],403)
  self.assertEqual(self.call('promo',{'code':'MooT'},c,x)[0],200)
  for action in ('apps.close','apps.install','apps.diagnose','apps.uninstall','command.review'):
   with patch.object(server,'choose_operation',return_value={'action':action,'appName':'WhatsApp','packageId':'9NKSQGP7F2NH','reason':'fixture'}),patch.object(server,'prepare_command',return_value={'script':"Write-Output 'fixture'",'description':'fixture only'}):
    self.assertEqual(self.call('chat',{'message':'выполни тестовый запрос'},c,x)[0],200)
   job=self.call('agent/jobs',bearer=t)[1]['jobs'][0];self.assertEqual(job['type'],action)
   if action=='command.review':
    self.assertIn('fixture',job['script']);self.assertEqual(len(job['lease']),32);self.assertGreater(job['leaseUntil'],time.time())
   result={'status':'failed','summary':'Not executed: isolated test','snapshotId':''}
   self.assertEqual(self.call('agent/result',{'id':job['id'],'result':result},bearer=t)[0],200)
 def test_permanent_activation_and_basic_gate(self):
  c,x=self.session();other,ox=self.session()
  self.assertFalse(self.call('state',cookie=c)[1]['fullMode'])
  for action in ('apps.close','apps.install','apps.diagnose','apps.uninstall','file.recycle','command.review'):
   with patch.object(server,'choose_operation',return_value={'action':action}):
    before=len(server.JOBS);reply=self.call('chat',{'message':'выполни тестовый запрос'},c,x)
    self.assertEqual(reply[0],200);self.assertEqual(len(server.JOBS),before)
  self.assertEqual(self.call('promo',{'code':'MOOT'},c,x)[0],200)
  session=next(v for v in server.SESSIONS.values() if v['csrf']==x)
  subject=session['id'];session['commandUntil']=0
  self.assertTrue(self.call('state',cookie=c)[1]['fullMode'])
  self.assertFalse(self.call('state',cookie=other)[1]['fullMode'])
  self.call('logout',{},c,x)
  self.assertTrue(server.storage.full_mode(subject))
  self.assertEqual(server.storage.DB.execute('SELECT enabled FROM entitlements WHERE subject=?',(subject,)).fetchone()[0],1)
 def test_install_and_launch_are_one_job(self):
  c,x=self.session();self.call('promo',{'code':'MOOT'},c,x)
  _,p,_=self.call('pair',{},c,x);_,d,_=self.call('agent/pair',{'code':p['code'],'name':'fixture'});t=d['token'];self.call('agent/grant',{'consent':True,'scope':'optimization.v1'},bearer=t)
  with patch.object(server,'choose_operation',return_value={'action':'apps.install','appName':'WhatsApp','launchAfter':True}):
   self.call('chat',{'message':'скачай WhatsApp и открой WhatsApp'},c,x)
  jobs=self.call('agent/jobs',bearer=t)[1]['jobs'];self.assertEqual(len(jobs),1);self.assertTrue(jobs[0]['launchAfter'])
# Avoid rerunning inherited cases; helper reuse is intentional.
for name in dir(test_server.Flow):
 if name.startswith('test_'):setattr(Extended,name,None)
if __name__=='__main__':unittest.main()
