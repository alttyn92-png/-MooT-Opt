let csrf='',state={};const $=s=>document.querySelector(s);let toastTimer;
function toast(t){$('#toast').textContent=t;$('#toast').style.display='block';clearTimeout(toastTimer);toastTimer=setTimeout(()=>$('#toast').style.display='none',6000)}
async function api(path,data,retry=true){
  const r=await fetch('/api/'+path,{method:data?'POST':'GET',headers:{'Content-Type':'application/json','X-CSRF-Token':csrf},body:data?JSON.stringify(data):undefined,cache:'no-store',signal:AbortSignal.timeout(data?45000:12000)});
  const b=await r.json();
  if(retry&&r.status===403&&b.error==='CSRF rejected'){
    const fresh=await fetch('/api/state',{cache:'no-store'});if(fresh.ok){csrf=(await fresh.json()).csrf;return api(path,data,false)}
  }
  if(r.status===401){const config=await (await fetch('/api/config',{cache:'no-store'})).json();if(config.googleRequired){$('#loginGate').classList.remove('hidden');throw Error('Войдите через Google');}
    if(retry){await fetch('/api/session',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'});const fresh=await fetch('/api/state',{cache:'no-store'});if(fresh.ok){csrf=(await fresh.json()).csrf;return api(path,data,false)}}
  }
  if(!r.ok)throw Error(b.error||'Не удалось выполнить запрос');return b;
}

function tab(id){document.querySelectorAll('.workspace>section').forEach(e=>e.classList.toggle('hidden',e.id!==id));document.querySelectorAll('[data-tab]').forEach(e=>e.classList.toggle('active',e.dataset.tab===id));$('#title').textContent={chat:'AI Assistant',profiles:'Игровые профили',history:'История действий',connection:'Подключение ПК'}[id]}
document.querySelectorAll('[data-tab]').forEach(e=>e.onclick=()=>tab(e.dataset.tab));
async function refresh(incoming){try{state=incoming?{...state,...incoming}:await api('state');csrf=state.csrf;updateTaskStatus();if(state.fullMode!==undefined){$('#promoStatus').textContent=state.fullMode?'Полный режим · активирован навсегда':'Базовый режим · оптимизация ПК';}if(!sending&&state.chat){const signature=JSON.stringify(state.chat);if(signature!==window.chatSignature){window.chatSignature=signature;$('#messages').replaceChildren();state.chat.forEach(m=>message(m.text,m.role));}}$('#googleTop').textContent=state.google?'Выйти':'Войти через Google';if(state.user){$('#userName').textContent=state.user.name;$('#userId').textContent='ID '+state.user.id;}const d=state.devices[0],t=d?.telemetry;$('#status').textContent=d?(d.online?(d.access?'ПК подключён · доступ разрешён':'Ожидает разрешения в приложении'):'Агент не в сети'):'ПК не подключён';$('#deviceName').textContent=d?.name||'Ожидает подключения';$('#deviceInfo').textContent=t?`${t.cpuName} · ${t.cores} потоков · ${t.memoryGB} ГБ RAM`:'Подключите агент, чтобы увидеть реальные показатели компьютера.';$('#cpu').textContent=t?`${t.cpuPercent}%`:'—';$('#ram').textContent=t?`${t.memoryPercent}%`:'—';$('#cpuBar').value=t?.cpuPercent||0;$('#ramBar').value=t?.memoryPercent||0;$('#metricNote').textContent=t?(d.online&&Date.now()/1000-(d.telemetryAt||0)<25?'LIVE · '+new Date(d.telemetryAt*1000).toLocaleTimeString():'Данные устарели · ожидаю приложение'):'Показатели появятся после диагностики';$('#events').replaceChildren();if(!state.events.length)$('#events').textContent='Пока нет действий.';state.events.slice().reverse().forEach(e=>{const row=document.createElement('div');row.className='event';const time=document.createElement('small');time.textContent=e.time;row.append(time,document.createTextNode(e.message));$('#events').append(row)})}catch(e){toast(e.message)}}
async function pair(){tab('connection');try{let b=await api('pair',{});$('#pairCode').textContent=b.code;$('#copyCode').disabled=false;toast('Введите код в Windows-агенте. Срок действия: 3 минуты.')}catch(e){toast(e.message)}}
$('#pair').onclick=pair;$('#pairTop').onclick=pair;$('#disconnect').onclick=async()=>{try{await api('disconnect',{});await refresh();toast('Доступ отозван')}catch(e){toast(e.message)}};
if($('#diagnose'))$('#diagnose').onclick=async()=>{try{await api('diagnose',{});toast('Запрос отправлен агенту');refresh()}catch(e){toast(e.message)}};
function message(text,role){$('.welcome')?.remove();let el=document.createElement('div');el.className='message '+role;el.textContent=text;$('#messages').append(el);el.scrollIntoView({block:'nearest'})}
let sending=false,pendingChoices=[];
$('#prompt').addEventListener('keydown',e=>{
  if(e.key==='Enter'&&!e.shiftKey&&!e.isComposing){e.preventDefault();if(!sending)$('#composer').requestSubmit()}
});
$('#composer').onsubmit=async e=>{
  e.preventDefault();const text=$('#prompt').value.trim();if(!text||sending)return;
  document.querySelector('#requestError')?.remove();sending=true;message(text,'user');$('#prompt').value='';const btn=$('#composer button');btn.disabled=true;
  $('#assistantStatus').textContent='Готовлю ответ…';$('#messages').setAttribute('aria-busy','true');
  const pending=document.createElement('div');pending.className='thinking';pending.textContent='MooT готовит ответ…';pending.setAttribute('role','status');$('#messages').append(pending);pending.scrollIntoView({block:'nearest'});
  try{const b=await api('chat',{message:text});pending.remove();message(b.reply,'assistant');pendingChoices=b.choices||[];showChoices(pendingChoices)}
  catch(err){pending.remove();const error=document.createElement('div');error.id='requestError';error.setAttribute('role','alert');error.textContent=err.name==='TimeoutError'?'Ответ задерживается. Проверьте историю перед повтором: команда могла быть принята.':err.message;$('#composer').before(error);if(!$('#prompt').value)$('#prompt').value=text}
  finally{sending=false;await refresh();showChoices(pendingChoices);btn.disabled=false;updateTaskStatus();$('#messages').setAttribute('aria-busy','false');$('#prompt').focus()}
};
document.querySelectorAll('[data-prompt]').forEach(b=>b.onclick=()=>{$('#prompt').value=b.dataset.prompt;$('#prompt').focus()});$('#google').onclick=()=>{location.href='/auth/google/start'};(async()=>{try{const config=await api('config');const r=await fetch('/api/state');if(r.status===401){if(config.googleRequired){$('#loginGate').classList.remove('hidden');if(!config.googleAvailable){$('#loginGate h2').textContent='MooT готовится к запуску';$('#loginGate p').textContent='Владелец подключает вход через Google и ИИ. Портфолио и страницы проектов уже доступны.';$('#loginGate a.download').textContent='Вернуться в портфолио';$('#loginGate a.download').href='/';return;}if(new URLSearchParams(location.search).get('login')==='failed')$('#loginError').textContent='Вход не завершён. Проверьте аккаунт и повторите попытку.';return;}await api('session',{});}await refresh();startLive()}catch(e){toast('Сервер недоступен: '+e.message)}})();
function showChoices(choices){document.querySelectorAll('.chat-choices').forEach(el=>el.remove());if(!choices.length)return;const box=document.createElement('div');box.className='chat-choices';choices.forEach(text=>{const b=document.createElement('button');b.type='button';b.textContent=text;b.onclick=()=>{if(sending)return;$('#prompt').value=text;box.remove();$('#composer').requestSubmit();};box.append(b)});$('#messages').append(box);box.scrollIntoView({block:'nearest'});}
$('#copyCode').onclick=async()=>{try{await navigator.clipboard.writeText($('#pairCode').textContent);toast('Код скопирован')}catch(e){toast('Выделите и скопируйте код вручную')}};

$('#googleTop').onclick=async()=>{if(state.google){await api('logout',{});location.reload();}else location.href='/auth/google/start'};

const menu=$('#siteMenu'),menuButton=$('#menuOpen'),menuBackdrop=$('#menuBackdrop');
function setMenu(open){menu.classList.toggle('open',open);document.body.classList.toggle('sidebar-open',open);menu.inert=!open;menuBackdrop.hidden=true;menuButton.setAttribute('aria-expanded',String(open));menuButton.setAttribute('aria-label',open?'Свернуть боковую панель':'Развернуть боковую панель');if(!open&&menu.contains(document.activeElement))menuButton.focus();}
menuButton.onclick=()=>setMenu(!menu.classList.contains('open'));$('#menuClose').onclick=()=>setMenu(false);
document.addEventListener('keydown',e=>{if(e.key==='Escape'&&menu.classList.contains('open'))setMenu(false)});

let liveTimer,liveRunning=false,liveFailures=0;
function updateTaskStatus(){
 if(sending)return;
 const active=(state.actions||[]).filter(a=>a.status==='queued'||a.status==='dispatched');
 const running=active.find(a=>a.status==='dispatched');
 const text=running?(running.phase==='waiting_confirmation'?'Подтвердите действие в приложении Windows':running.phase==='running'?'Приложение выполняет команду':'Команда передана приложению'):active.length?'Команда ожидает приложения':'На связи';
 $('#assistantStatus').textContent=text+(active.length>1?' · в очереди '+(active.length-(running?1:0)):'');
}
async function pollLive(){
 if(liveRunning)return;liveRunning=true;
 try{const next=await api('state');liveFailures=0;await refresh(next);}
 catch(error){liveFailures++;if(liveFailures>=2){$('#metricNote').textContent='Нет обновлений с сервера · повторяем подключение';$('#assistantStatus').textContent=error.message;}}
 finally{liveRunning=false;liveTimer=setTimeout(pollLive,document.hidden?5000:2000);}
}
function startLive(){clearTimeout(liveTimer);if(!liveRunning)pollLive();}

$('#promoForm').onsubmit=async e=>{e.preventDefault();const button=e.currentTarget.querySelector('button');button.disabled=true;try{const result=await api('promo',{code:$('#promoCode').value});$('#promoCode').value='';$('#promoStatus').textContent=result.message;await refresh();}catch(error){$('#promoStatus').textContent=error.message;}finally{button.disabled=false;}};

window.addEventListener('online',()=>{startLive();refresh();});document.addEventListener('visibilitychange',()=>{if(!document.hidden)refresh();});
