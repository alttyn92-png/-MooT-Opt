"""WSGI transport for Waitress behind the HTTPS reverse proxy."""
import io, os
from email.message import Message
from http import HTTPStatus
from types import SimpleNamespace
from server import Handler
from pathlib import Path
import google_login

class RequestHandler(Handler):
    def __init__(self,environ):
        self.command=environ['REQUEST_METHOD'];self.path=environ.get('PATH_INFO','/')
        try:self.path=self.path.encode('latin-1').decode('utf-8')
        except (UnicodeEncodeError,UnicodeDecodeError):pass
        if environ.get('QUERY_STRING'):self.path+='?'+environ['QUERY_STRING']
        self.headers=Message()
        for key,value in environ.items():
            if key.startswith('HTTP_'):self.headers[key[5:].replace('_','-')]=value
        for key in ('CONTENT_TYPE','CONTENT_LENGTH'):
            if environ.get(key):self.headers[key.replace('_','-')]=environ[key]
        self.client_address=(environ.get('REMOTE_ADDR','127.0.0.1'),0)
        self.server=SimpleNamespace(server_port=int(os.getenv('MOOT_PORT','8765')))
        self.rfile=environ['wsgi.input'];self.wfile=io.BytesIO();self.response_headers=[];self.status=200
    def send_response(self,code,message=None):self.status=code
    def send_header(self,name,value):self.response_headers.append((name,value))
    def end_headers(self):pass

def application(environ,start_response):
    handler=RequestHandler(environ)
    if handler.command=='GET' and handler.path=='/download/MooT-Setup.exe' and handler.valid_host() and (not google_login.required() or handler.owner()):
        default_name='MooT-Setup.exe' if os.getenv('PUBLIC_ORIGIN') else 'MooT-Local-Setup.exe'
        path=Path(os.getenv('MOOT_INSTALLER_PATH',str(Path(__file__).parent/'downloads'/default_name)))
        if path.is_file():
            start_response('200 OK',[('Content-Type','application/octet-stream'),('Content-Disposition','attachment; filename="MooT-Setup.exe"'),('Content-Length',str(path.stat().st_size)),('X-Content-Type-Options','nosniff')])
            def chunks():
                with path.open('rb') as f:
                    while chunk:=f.read(65536):yield chunk
            return chunks()
    if handler.command not in ('GET','POST'):
        handler.send(405,{'error':'Method not allowed'})
    else:
        try:getattr(handler,'do_'+handler.command)()
        except Exception:handler=RequestHandler(environ);handler.send(500,{'error':'Ошибка сервера. Повторите запрос.'})
    body=handler.wfile.getvalue()
    if not any(k.lower()=='content-length' for k,v in handler.response_headers):handler.response_headers.append(('Content-Length',str(len(body))))
    handler.response_headers.extend([('X-Content-Type-Options','nosniff'),('Referrer-Policy','same-origin')])
    start_response(f'{handler.status} {HTTPStatus(handler.status).phrase}',handler.response_headers)
    return [body]
