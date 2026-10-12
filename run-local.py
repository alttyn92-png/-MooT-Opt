"""Local launcher: isolated Python dependencies and hidden API-key prompt."""
import getpass,os,subprocess,sys
from pathlib import Path
from config_env import load
load()
root=Path(__file__).resolve().parent
os.chdir(root)
venv=root/'.venv'
python=venv/('Scripts/python.exe' if os.name=='nt' else 'bin/python')
if not python.exists():subprocess.run([sys.executable,'-m','venv',str(venv)],check=True)
requirements=root/'requirements.lock.txt'
marker=venv/'moot-requirements.txt'
if not marker.exists() or marker.read_bytes()!=requirements.read_bytes():
    subprocess.run([str(python),'-m','pip','install','--disable-pip-version-check','-r',str(requirements)],check=True)
    marker.write_bytes(requirements.read_bytes())
os.environ['APP_ENV']='local'
os.environ['REQUIRE_GOOGLE']='0'
os.environ.pop('PUBLIC_ORIGIN',None)
os.environ.setdefault('ALEM_ENDPOINT','https://llm.alem.ai/v1/chat/completions')
os.environ.setdefault('ALEM_MODEL','qwen3-8')
if not os.getenv('ALEM_API_KEY'):os.environ['ALEM_API_KEY']=getpass.getpass('API key (input hidden): ')
print('MooT 1.2.0: http://127.0.0.1:8765/ — keep this window open.')
subprocess.run([str(python),'serve.py'],check=True)
