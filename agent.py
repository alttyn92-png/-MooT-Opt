"""Compact Windows read-only agent. No shell execution, persistence or elevation."""
import ctypes, json, os, platform, queue, threading, time, tkinter as tk
import urllib.request, urllib.error
SERVER='http://127.0.0.1:'+os.getenv('MOOT_PORT','8765')

def diagnostics():
    if os.name!='nt': raise RuntimeError('Windows required')
    class Memory(ctypes.Structure):
        _fields_=[('length',ctypes.c_ulong),('load',ctypes.c_ulong)]+[(n,ctypes.c_ulonglong) for n in ('total','available','page','freepage','virtual','freevirtual','extended')]
    def ticks():
        a,b,c=ctypes.c_ulonglong(),ctypes.c_ulonglong(),ctypes.c_ulonglong()
        if not ctypes.windll.kernel32.GetSystemTimes(ctypes.byref(a),ctypes.byref(b),ctypes.byref(c)):raise RuntimeError('CPU read failed')
        return a.value,b.value+c.value
    idle,total=ticks();time.sleep(.5);idle2,total2=ticks()
    m=Memory();m.length=ctypes.sizeof(m)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(m)):raise RuntimeError('Memory read failed')
    return {'cpuPercent':round(max(0,min(100,100*(1-(idle2-idle)/max(1,total2-total)))),1),'memoryPercent':m.load,'memoryGB':round(m.total/1024**3,1),'cpuName':platform.processor()[:200] or 'CPU','os':platform.platform()[:200],'cores':os.cpu_count() or 1}

class Agent:
    def __init__(self):
        self.root=tk.Tk();self.root.title('[MooT]');self.root.geometry('380x400');self.root.resizable(False,False);self.root.configure(bg='#0d0c11')
        self.token=None;self.granted=False;self.generation=0;self.messages=queue.Queue()
        c=tk.Canvas(self.root,width=62,height=45,bg='#0d0c11',highlightthickness=0);c.pack(pady=(20,5));c.create_polygon(4,40,4,5,15,5,31,22,47,5,58,5,58,40,47,40,47,23,31,40,15,23,15,40,fill='#ff334b')
        self.label('[MooT]',22,'#f3edf5');self.label('Optimization Boonderking',9,'#918595');self.user=self.label('Локальный пользователь',11,'#c0b3c5')
        self.status=self.label('Не подключён',11,'#ff637c')
        self.code=tk.Entry(self.root,font=('Segoe UI',14),justify='center',bg='#211821',fg='white',insertbackground='white',relief='flat');self.code.pack(pady=8,ipady=5)
        self.consent=tk.BooleanVar(value=False)
        self.consent_box=tk.Checkbutton(self.root,text='Разрешаю ассистенту чтение CPU, RAM и ОС',variable=self.consent,bg='#0d0c11',fg='#b9aabd',selectcolor='#211821',activebackground='#0d0c11',activeforeground='white',font=('Segoe UI',9))
        self.button=tk.Button(self.root,text='Подключить',command=self.connect,bg='#e52c49',fg='white',relief='flat',font=('Segoe UI',11),width=26);self.button.pack(pady=10)
        self.cancel=tk.Button(self.root,text='Отключить',command=self.disconnect,bg='#211821',fg='white',relief='flat')
        self.label('Только диагностика · без изменения настроек',8,'#776c80')
        self.root.protocol('WM_DELETE_WINDOW',self.close);self.root.after(150,self.drain)
    def label(self,text,size,color):
        w=tk.Label(self.root,text=text,font=('Segoe UI',size),bg='#0d0c11',fg=color);w.pack(pady=3);return w
    def request(self,path,data=None,credential=None):
        headers={'Content-Type':'application/json'}
        if credential:headers['Authorization']='Bearer '+credential
        req=urllib.request.Request(SERVER+'/api/agent/'+path,json.dumps(data).encode() if data is not None else None,headers)
        with urllib.request.urlopen(req,timeout=5) as res:return json.load(res)
    def connect(self):
        if self.token:
            if self.granted:self.disconnect();return
            if not self.consent.get():self.status.config(text='Отметьте согласие на диагностику');return
            credential=self.token;generation=self.generation;self.button.config(state='disabled')
            def grant():
                try:
                    self.request('grant',{'consent':True,'scope':'diagnostics.read'},credential)
                    self.messages.put(('granted',generation))
                except Exception:self.messages.put(('error','Не удалось предоставить доступ'))
            threading.Thread(target=grant,daemon=True).start();return
        code=self.code.get().strip().upper();self.button.config(state='disabled');generation=self.generation
        def work():
            try:
                b=self.request('pair',{'code':code,'name':platform.node()[:60]})
                self.messages.put(('paired',(b['token'],generation)))
            except Exception:self.messages.put(('error','Не удалось подключиться. Проверьте код.'))
        threading.Thread(target=work,daemon=True).start()
    def poll(self,credential,generation):
        while self.token==credential and self.generation==generation:
            try:
                b=self.request('jobs',credential=credential)
                for job in b['jobs']:
                    if self.token!=credential or self.generation!=generation:return
                    if job.get('type')!='diagnostics.read':continue
                    r=diagnostics()
                    if self.token==credential:self.request('result',{'id':job['id'],'result':r},credential)
                self.messages.put(('status',(generation,'Подключён · доступ только к чтению')))
            except urllib.error.HTTPError as e:
                if e.code==401:self.messages.put(('revoked',generation));return
                self.messages.put(('status',(generation,'Ошибка сервера · повтор подключения')))
            except Exception:self.messages.put(('status',(generation,'Нет связи · повтор подключения')))
            time.sleep(3)
    def disconnect(self):
        credential=self.token;self.token=None;self.generation+=1
        self.granted=False;self.consent.set(False);self.consent_box.pack_forget();self.cancel.pack_forget()
        self.status.config(text='Доступ отключён');self.button.config(text='Подключить',state='normal');self.code.config(state='normal')
        if credential:
            def revoke():
                try:self.request('disconnect',{},credential)
                except Exception:pass
            threading.Thread(target=revoke,daemon=True).start()
    def close(self):self.disconnect();self.root.destroy()
    def drain(self):
        while not self.messages.empty():
            kind,value=self.messages.get()
            if kind=='paired':
                credential,generation=value
                if generation!=self.generation:continue
                self.token=credential;self.button.config(text='Предоставить доступ',state='normal');self.code.config(state='disabled');self.status.config(text='ПК подключён · доступ не предоставлен')
                self.consent_box.pack(before=self.button);self.cancel.pack(after=self.button)
                threading.Thread(target=self.poll,args=(credential,generation),daemon=True).start()
            elif kind=='granted' and value==self.generation:
                self.granted=True;self.button.config(text='Отключить',state='normal');self.cancel.pack_forget();self.consent_box.pack_forget();self.status.config(text='Подключён · доступ только к чтению')
            elif kind=='error':self.status.config(text=value);self.button.config(state='normal')
            elif kind=='status' and value[0]==self.generation:self.status.config(text=value[1] if self.granted else 'ПК подключён · доступ не предоставлен')
            elif kind=='revoked' and value==self.generation:self.disconnect()
        self.root.after(150,self.drain)

if __name__=='__main__':Agent().root.mainloop()
