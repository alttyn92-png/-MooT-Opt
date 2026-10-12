import unittest, threading, json, urllib.request, urllib.error
from http.server import ThreadingHTTPServer
import os
os.environ["MOOT_STATE_PATH"]=":memory:"
import server
from unittest.mock import patch

class Analysis(unittest.TestCase):
    def test_model_plan_rejects_unlisted_commands(self):
        with patch.dict(os.environ,{'ALEM_ENDPOINT':'https://example.invalid','ALEM_MODEL':'mock','ALEM_API_KEY':'mock'}),patch.object(server,'provider_complete',return_value='{"services":["WinDefend"],"closeApps":[],"analysis":"unsafe"}'):
            plan=server.optimization_plan('maximum',{'processes':[]},'maximum')
            self.assertEqual(plan['services'],[]);self.assertEqual(plan['closeApps'],[])

    def test_five_roles_use_provider_without_executing_actions(self):
        with patch.dict(server.os.environ,{'ALEM_ENDPOINT':'https://example.invalid/v1/chat/completions','ALEM_MODEL':'test','ALEM_API_KEY':'test'}), patch.object(server,'provider_complete',return_value='План без изменений') as provider:
            result=server.ai_reply('Оптимизируй CS2',[{'cpuPercent':20,'memoryPercent':50}])
            self.assertEqual(provider.call_count,5)
            self.assertEqual(result,'План без изменений')
    def test_missing_telemetry_does_not_fabricate_analysis(self):
        with patch.dict(server.os.environ,{'ALEM_ENDPOINT':'https://example.invalid','ALEM_MODEL':'test','ALEM_API_KEY':'test'}), patch.object(server,'provider_complete',return_value='Нужна диагностика') as provider:
            server.ai_reply('Оптимизируй CS2',[])
            self.assertEqual(provider.call_count,1)

class Flow(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.http=ThreadingHTTPServer(('127.0.0.1',0),server.Handler)
        cls.url='http://127.0.0.1:'+str(cls.http.server_port)
        threading.Thread(target=cls.http.serve_forever,daemon=True).start()
    @classmethod
    def tearDownClass(cls):cls.http.shutdown();cls.http.server_close()
    def call(self,path,body=None,cookie='',csrf='',bearer='',origin=None):
        h={'Origin':origin or self.url,'Content-Type':'application/json','Cookie':cookie,'X-CSRF-Token':csrf,'Authorization':'Bearer '+bearer}
        r=urllib.request.Request(self.url+'/api/'+path,json.dumps(body).encode() if body is not None else None,h)
        try:
            with urllib.request.urlopen(r) as out:return out.status,json.load(out),out.headers
        except urllib.error.HTTPError as e:return e.code,json.load(e),e.headers
    def session(self):
        _,_,h=self.call('session',{});cookie=h['Set-Cookie'].split(';')[0]
        _,s,_=self.call('state',cookie=cookie);return cookie,s['csrf']
    def test_full_read_only_flow_and_boundaries(self):
        self.assertEqual(self.call('state')[0],401)
        c,x=self.session();other,ox=self.session()
        self.assertEqual(self.call('pair',{},c)[0],403)
        self.assertEqual(self.call('pair',{},c,x,origin='https://evil.invalid')[0],403)
        _,p,_=self.call('pair',{},c,x)
        _,d,_=self.call('agent/pair',{'code':p['code'],'consent':True,'name':'TEST DEVICE'})
        t=d['token']
        self.assertEqual(self.call('diagnose',{},c,x)[0],409)
        self.assertEqual(self.call('agent/jobs',bearer=t)[1]['jobs'],[])
        self.assertEqual(self.call('agent/grant',{'consent':True,'scope':'full'},bearer=t)[0],400)
        self.assertEqual(self.call('agent/grant',{'consent':True,'scope':'diagnostics.read'},bearer=t)[0],200)
        self.assertEqual(self.call('agent/pair',{'code':p['code'],'consent':True})[0],400)
        self.assertEqual(self.call('state',cookie=other)[1]['devices'],[])
        self.assertEqual(self.call('diagnose',{},other,ox)[0],409)
        status,answer,_=self.call('chat',{'message':'Проверь мой ПК'},c,x)
        self.assertEqual(status,200);self.assertIn('диагностику',answer['reply'])
        self.assertEqual(self.call('diagnose',{},c,x)[0],200)
        job=self.call('agent/jobs',bearer=t)[1]['jobs'][0]
        self.assertEqual(job['type'],'diagnostics.read')
        self.assertEqual(self.call('agent/result',{'id':job['id'],'result':{'command':'whoami'}},bearer=t)[0],400)
        r={'cpuPercent':12,'memoryPercent':45,'memoryGB':16,'cores':4,'cpuName':'Synthetic fixture','os':'Test'}
        self.assertEqual(self.call('agent/result',{'id':job['id'],'result':r},bearer=t)[0],200)
        self.assertEqual(self.call('agent/result',{'id':job['id'],'result':r},bearer=t)[0],200)
        self.assertEqual(self.call('state',cookie=c)[1]['devices'][0]['telemetry'],r)
        self.assertEqual(self.call('execute',{'command':'anything'},c,x)[0],404)
        self.assertEqual(self.call('disconnect',{},c,x)[0],200)
        self.assertEqual(self.call('agent/jobs',bearer=t)[0],401)
    def test_pair_expiry(self):
        c,x=self.session();_,p,_=self.call('pair',{},c,x)
        server.PAIRS[p['code']]['expires']=0
        self.assertEqual(self.call('agent/pair',{'code':p['code'],'consent':True})[0],400)
    def test_unconfigured_chat_is_honest(self):
        c,x=self.session();status,b,_=self.call('chat',{'message':'Привет'},c,x)
        self.assertEqual(status,200);self.assertIn('не настроено',b['reply'])
    def test_maximum_requires_separate_confirmation(self):
        c,x=self.session();_,p,_=self.call('pair',{},c,x)
        self.assertRegex(p['code'],r'^\d{7}$')
        _,d,_=self.call('agent/pair',{'code':p['code'],'name':'Synthetic'});t=d['token']
        self.call('agent/grant',{'consent':True,'scope':'optimization.v1'},bearer=t)
        answer=self.call('chat',{'message':'Оптимизируй CS2'},c,x)[1]
        self.assertIn('Базовый',answer['choices'])
        server.DEVICES[server.digest(t)]['telemetry']={'cpuPercent':20,'memoryPercent':30,'processes':[]}
        self.call('chat',{'message':'Максимальный'},c,x)
        self.assertTrue(all(j['type']=='diagnostics.read' for j in self.call('agent/jobs',bearer=t)[1]['jobs']))
        self.call('chat',{'message':'Разрешаю максимальный'},c,x)
        job=self.call('agent/jobs',bearer=t)[1]['jobs'][0]
        self.assertEqual(job['mode'],'maximum');self.assertEqual(job['type'],'optimization.apply')
        result={'status':'complete','summary':'Synthetic result','snapshotId':'a'*32}
        self.assertEqual(self.call('agent/result',{'id':job['id'],'result':result},bearer=t)[0],200)
        self.call('chat',{'message':'Вернуть настройки'},c,x)
        rollback=self.call('agent/jobs',bearer=t)[1]['jobs'][0]
        self.assertEqual(rollback['type'],'optimization.rollback')
        self.assertEqual(rollback['snapshotId'],'a'*32)
    def test_automatic_diagnostics_memory_rule_and_recycle(self):
        c,x=self.session();_,p,_=self.call('pair',{},c,x)
        _,d,_=self.call('agent/pair',{'code':p['code'],'name':'Synthetic'});t=d['token']
        self.call('agent/grant',{'consent':True,'scope':'optimization.v1'},bearer=t)
        job=self.call('agent/jobs',bearer=t)[1]['jobs'][0]
        self.assertTrue(job['automatic'])
        reply=self.call('chat',{'message':'Когда ОЗУ 60%, очищай автоматически'},c,x)[1]['reply']
        self.assertIn('Правило сохранено',reply)
        telemetry={'cpuPercent':20,'memoryPercent':70,'memoryGB':16,'cpuName':'Synthetic','os':'Test','cores':4}
        self.assertEqual(self.call('agent/result',{'id':job['id'],'result':telemetry},bearer=t)[0],200)
        clean=self.call('agent/jobs',bearer=t)[1]['jobs'][0]
        self.assertEqual(clean['type'],'memory.clean')
        result={'status':'complete','summary':'Synthetic memory result','snapshotId':''}
        self.assertEqual(self.call('agent/result',{'id':clean['id'],'result':result},bearer=t)[0],200)
        self.assertEqual(self.call('agent/result',{'id':clean['id'],'result':result},bearer=t)[0],200)
        self.assertEqual(self.call('agent/jobs',bearer=t)[1]['jobs'],[])
        self.call('chat',{'message':'Отключи автоочистку ОЗУ'},c,x)
        self.assertNotIn('memoryRule',server.DEVICES[server.digest(t)])
        self.call('chat',{'message':'Очисти корзину'},c,x)
        recycle=self.call('agent/jobs',bearer=t)[1]['jobs'][0]
        self.assertEqual(recycle['type'],'recycle.empty')
        denied={'status':'failed','summary':'Declined locally','snapshotId':''}
        self.assertEqual(self.call('agent/result',{'id':recycle['id'],'result':denied},bearer=t)[0],200)
    def test_google_gate_for_gpu_and_uac_continuation(self):
        c,x=self.session();_,p,_=self.call('pair',{},c,x)
        _,d,_=self.call('agent/pair',{'code':p['code'],'name':'Synthetic'});t=d['token']
        self.call('agent/grant',{'consent':True,'scope':'optimization.v1'},bearer=t)
        device=server.DEVICES[server.digest(t)];device['telemetry']={'gpuControl':{'supported':True}}
        answer=self.call('chat',{'message':'Разгони GPU'},c,x)[1]['reply']
        self.assertIn('Google',answer)
        self.assertFalse(any(j['device']==device['id'] and j['type']=='elevation.request' for j in server.JOBS.values()))
        server.SESSIONS[server.digest(c.split('=',1)[1])]['google']=True
        self.call('chat',{'message':'Разгони GPU'},c,x)
        job=self.call('agent/jobs',bearer=t)[1]['jobs'][0]
        self.assertEqual(job['type'],'elevation.request')
        self.assertEqual(self.call('agent/result',{'id':job['id'],'result':{'status':'complete','summary':'UAC mock','snapshotId':''}},bearer=t)[0],200)
        gpu=self.call('agent/jobs',bearer=t)[1]['jobs'][0]
        self.assertEqual(gpu['type'],'overclock.apply')
        self.assertEqual(self.call('agent/result',{'id':gpu['id'],'result':{'status':'failed','summary':'Declined on PC','snapshotId':''}},bearer=t)[0],200)
    def test_negated_command_does_not_queue(self):
        c,x=self.session()
        answer=self.call('chat',{'message':'Не надо очищать ОЗУ'},c,x)[1]['reply']
        self.assertIn('не запускаю',answer)
    def test_typo_memory_command_and_temp_cleanup(self):
        c,x=self.session();_,p,_=self.call('pair',{},c,x)
        _,d,_=self.call('agent/pair',{'code':p['code'],'name':'Synthetic'});t=d['token']
        self.call('agent/grant',{'consent':True,'scope':'optimization.v1'},bearer=t)
        reply=self.call('chat',{'message':'очису озу'},c,x)[1]['reply']
        self.assertIn('отправлено',reply)
        job=self.call('agent/jobs',bearer=t)[1]['jobs'][0];self.assertEqual(job['type'],'memory.clean')
        self.call('agent/result',{'id':job['id'],'result':{'status':'complete','summary':'Synthetic clean','snapshotId':''}},bearer=t)
        self.call('chat',{'message':'Удали ненужные файлы'},c,x)
        job=self.call('agent/jobs',bearer=t)[1]['jobs'][0];self.assertEqual(job['type'],'storage.clean')
    def test_google_required_blocks_local_bypass(self):
        with patch.dict(os.environ,{'REQUIRE_GOOGLE':'1'}):
            self.assertEqual(self.call('session',{})[0],403)
    def test_stable_account_id(self):
        a=server.storage.account('test-google-subject','First')
        b=server.storage.account('test-google-subject','Updated')
        self.assertEqual(a['id'],b['id']);self.assertRegex(a['id'],r'^\d{7}$')

if __name__=='__main__':unittest.main()
