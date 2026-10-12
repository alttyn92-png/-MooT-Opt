<?php
declare(strict_types=1);
if(parse_url($_SERVER['REQUEST_URI'],PHP_URL_PATH)==='/api/project-opens'){require __DIR__.'/project-stats.php';exit;}
// Shared-hosting backend: state survives short PHP requests; no resident daemon.
ini_set('display_errors','0');
$cfg=require __DIR__.'/config.php';
foreach(['ALEM_API_KEY'=>'alem_key','GOOGLE_CLIENT_ID'=>'google_id','GOOGLE_CLIENT_SECRET'=>'google_secret'] as $env=>$key)if(getenv($env))$cfg[$key]=getenv($env);
$local=getenv('MOOT_PHP_LOCAL')==='1' && in_array($_SERVER['REMOTE_ADDR']??'', ['127.0.0.1','::1'],true);
$origin=$local?'http://'.($_SERVER['HTTP_HOST']??'127.0.0.1:8780'):$cfg['origin'];
$path=parse_url($_SERVER['REQUEST_URI'],PHP_URL_PATH);$method=$_SERVER['REQUEST_METHOD'];
header('X-Content-Type-Options: nosniff');header('X-Frame-Options: DENY');header('Cache-Control: no-store');header('Referrer-Policy: same-origin');
header("Content-Security-Policy: default-src 'self'; frame-ancestors 'none'; base-uri 'none'");
if(!$local && ($_SERVER['HTTP_HOST']??'')!==parse_url($origin,PHP_URL_HOST)){http_response_code(403);exit;}
if(!$local && empty($_SERVER['HTTPS'])){http_response_code(400);exit('HTTPS required');}
if(!in_array($method,['GET','POST'],true)){http_response_code(405);exit;}
$db=new PDO('sqlite:'.__DIR__.'/state.sqlite');$db->setAttribute(PDO::ATTR_ERRMODE,PDO::ERRMODE_EXCEPTION);
$db->exec('PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; CREATE TABLE IF NOT EXISTS state (id INTEGER PRIMARY KEY, data TEXT NOT NULL)');
$db->exec("INSERT OR IGNORE INTO state VALUES(1,'{}')");
$db->exec('BEGIN IMMEDIATE');$locked=true;$S=json_decode($db->query('SELECT data FROM state WHERE id=1')->fetchColumn(),true)?:[];
foreach(['sessions','devices','jobs','events','chats','pairs','failures','users','limits','oauth','busy'] as $k)$S[$k]??=[];
function unlock():void{global $locked,$db,$S;if($locked){$q=$db->prepare('UPDATE state SET data=? WHERE id=1');$q->execute([json_encode($S,JSON_UNESCAPED_UNICODE|JSON_THROW_ON_ERROR)]);$db->exec('COMMIT');$locked=false;}}
function relock():void{global $locked,$db,$S;$db->exec('BEGIN IMMEDIATE');$S=json_decode($db->query('SELECT data FROM state WHERE id=1')->fetchColumn(),true);$locked=true;}
function output(array $data,int $status=200):never{unlock();http_response_code($status);header('Content-Type: application/json; charset=utf-8');echo json_encode($data,JSON_UNESCAPED_UNICODE);exit;}
function fail(string $text,int $status=400):never{output(['error'=>$text],$status);}
function token():string{return bin2hex(random_bytes(24));}
function hashed(string $v):string{return hash('sha256',$v);}
function cookie(string $name,string $value,int $ttl=86400):void{global $local;setcookie($name,$value,['expires'=>time()+$ttl,'path'=>'/','secure'=>!$local,'httponly'=>true,'samesite'=>'Lax']);}
function redirect(string $url):never{unlock();header('Location: '.$url, true,302);exit;}
function limit(string $id,int $burst,float $rate):void{global $S;$now=microtime(true);$b=$S['limits'][$id]??['t'=>$now,'n'=>$burst];$n=min($burst,$b['n']+($now-$b['t'])*$rate);$S['limits'][$id]=['t'=>$now,'n'=>max(0,$n-1)];if($n<1){header('Retry-After: '.max(1,(int)ceil((1-$n)/$rate)));fail('Лимит запросов. Повторите позже.',429);}}
function event(string $owner,string $message):void{global $S;$S['events'][$owner][]= ['time'=>date('H:i:s'),'message'=>$message];$S['events'][$owner]=array_slice($S['events'][$owner],-100);}
function chat_add(string $owner,string $role,string $text):void{global $S;$S['chats'][$owner][]=['role'=>$role,'text'=>$text];$S['chats'][$owner]=array_slice($S['chats'][$owner],-200);}
function queue(array $d,string $kind,array $params=[]):string{global $S;$pending=0;foreach($S['jobs'] as $j)if($j['device']===$d['id']&&in_array($j['status'],['queued','dispatched'],true)&&$j['expires']>time())$pending++;if($pending>=5)fail('В очереди уже пять задач. Дождитесь выполнения.',409);$id=token();$S['jobs'][$id]=array_merge(['id'=>$id,'device'=>$d['id'],'type'=>$kind,'status'=>'queued','expires'=>time()+600],$params);return $id;}
function ai_configured():bool{global $cfg;return !empty($cfg['alem_key'])&&!empty($cfg['alem_endpoint'])&&!empty($cfg['alem_model']);}
function http_json(string $url,?array $body=null,array $headers=[],bool $form=false):array{
 if(parse_url($url,PHP_URL_SCHEME)!=='https')throw new RuntimeException('HTTPS required');
 $c=curl_init($url);$buffer='';curl_setopt_array($c,[CURLOPT_RETURNTRANSFER=>true,CURLOPT_FOLLOWLOCATION=>false,CURLOPT_CONNECTTIMEOUT=>8,CURLOPT_TIMEOUT=>15,CURLOPT_PROTOCOLS=>CURLPROTO_HTTPS,CURLOPT_SSL_VERIFYPEER=>true,CURLOPT_SSL_VERIFYHOST=>2,CURLOPT_WRITEFUNCTION=>function($ch,$data)use(&$buffer){if(strlen($buffer)+strlen($data)>262144)return 0;$buffer.=$data;return strlen($data);}]);
 if($body!==null){curl_setopt($c,CURLOPT_POST,true);curl_setopt($c,CURLOPT_POSTFIELDS,$form?http_build_query($body):json_encode($body,JSON_UNESCAPED_UNICODE));$headers[]='Content-Type: '.($form?'application/x-www-form-urlencoded':'application/json');}curl_setopt($c,CURLOPT_HTTPHEADER,$headers);$ok=curl_exec($c);$status=curl_getinfo($c,CURLINFO_HTTP_CODE);curl_close($c);if($ok===false||$status!==200)throw new RuntimeException('External provider unavailable');$r=json_decode($buffer,true,32,JSON_THROW_ON_ERROR);if(!is_array($r))throw new RuntimeException('Invalid response');return $r;
}
function provider(string $system,array $context,int $tokens=1000):string{global $cfg;$r=http_json($cfg['alem_endpoint'],['model'=>$cfg['alem_model'],'max_tokens'=>$tokens,'messages'=>[['role'=>'system','content'=>$system],['role'=>'user','content'=>json_encode($context,JSON_UNESCAPED_UNICODE)]]],['Authorization: Bearer '.$cfg['alem_key']]);$text=$r['choices'][0]['message']['content']??null;if(!is_string($text)||trim($text)==='')throw new RuntimeException('Invalid model response');return mb_substr(trim($text),0,12000);}
function model_json(string $text):array{$text=preg_replace('/^```(?:json)?\s*|\s*```$/u','',trim($text));$r=json_decode($text,true,32,JSON_THROW_ON_ERROR);if(!is_array($r))throw new RuntimeException('Invalid model JSON');return $r;}
function b64(string $text):string{$r=base64_decode(strtr($text,'-_','+/'),true);if($r===false)throw new RuntimeException('Invalid token');return $r;}
function google_identity(string $jwt,string $nonce):array{global $cfg;$parts=explode('.',$jwt);if(count($parts)!==3)throw new RuntimeException('Invalid token');$h=json_decode(b64($parts[0]),true,16,JSON_THROW_ON_ERROR);$c=json_decode(b64($parts[1]),true,16,JSON_THROW_ON_ERROR);if(($h['alg']??'')!=='RS256')throw new RuntimeException('Invalid signature algorithm');$certs=http_json('https://www.googleapis.com/oauth2/v1/certs');$cert=$certs[$h['kid']??'']??'';if(!$cert||openssl_verify($parts[0].'.'.$parts[1],b64($parts[2]),$cert,OPENSSL_ALGO_SHA256)!==1)throw new RuntimeException('Invalid signature');if(($c['aud']??'')!==$cfg['google_id']||!in_array($c['iss']??'',['accounts.google.com','https://accounts.google.com'],true)||($c['exp']??0)<time()||($c['iat']??0)>time()+60||!hash_equals($nonce,$c['nonce']??'')||($c['email_verified']??false)!==true||empty($c['sub']))throw new RuntimeException('Invalid identity');return $c;}
function state_for(string $sk):array{global $S;$s=$S['sessions'][$sk];$owner=$s['id'];$ds=[];$ids=[];foreach($S['devices'] as $d)if($d['owner']===$owner&&$d['active']&&$d['expires']>time()){$ids[]=$d['id'];$ds[]=['id'=>$d['id'],'name'=>$d['name'],'online'=>time()-$d['seen']<25,'access'=>$d['access'],'telemetry'=>$d['telemetry']??null,'telemetryAt'=>$d['telemetryAt']??0];}$actions=[];foreach($S['jobs'] as $j)if(in_array($j['device'],$ids,true))$actions[]=['id'=>$j['id'],'type'=>$j['type'],'status'=>$j['status'],'summary'=>$j['summary']??'','phase'=>$j['phase']??''];return ['csrf'=>$s['csrf'],'user'=>$s['user'],'google'=>$s['google']??false,'fullMode'=>$S['users'][$owner]['full']??false,'devices'=>$ds,'events'=>$S['events'][$owner]??[],'actions'=>array_slice($actions,-20),'chat'=>$S['chats'][$owner]??[],'aiConfigured'=>ai_configured()];}
function jobs_for(string $dk):array{
 global $S;$d=&$S['devices'][$dk];$d['seen']=time();if(!$d['access'])return ['jobs'=>[]];
 foreach($S['jobs'] as &$j){if($j['device']===$d['id']&&$j['expires']<time()&&in_array($j['status'],['queued','dispatched'],true)){$j['status']='expired';$j['summary']='Срок ожидания команды истёк. Повторите запрос при подключённом приложении.';chat_add($d['owner'],'assistant',$j['summary']);}}unset($j);
 foreach($S['jobs'] as $j)if($j['device']===$d['id']&&$j['status']==='dispatched')return ['jobs'=>[]];
 foreach($S['jobs'] as &$j)if($j['device']===$d['id']&&$j['status']==='queued'){$j['status']='dispatched';$out=$j;unset($j);return ['jobs'=>[$out]];}unset($j);return ['jobs'=>[]];
}
function stream_event(array $data):never{unlock();header('Content-Type: text/event-stream');header('X-Accel-Buffering: no');echo "retry: 5000\n: connected\ndata: ".json_encode($data,JSON_UNESCAPED_UNICODE)."\n\n";exit;}
function validate_telemetry(array $r):void{
 $allowed=['cpuPercent','memoryPercent','memoryGB','cpuName','os','cores','inventory','processes','gpuControl','elevated','agentVersion'];if(array_diff(array_keys($r),$allowed))throw new InvalidArgumentException();foreach(['cpuPercent','memoryPercent'] as $k)if(!isset($r[$k])||!is_numeric($r[$k])||$r[$k]<0||$r[$k]>100)throw new InvalidArgumentException();if(!isset($r['memoryGB'],$r['cores'],$r['cpuName'],$r['os'])||!is_numeric($r['memoryGB'])||$r['memoryGB']<=0||$r['memoryGB']>=100000||!is_int($r['cores'])||$r['cores']<1||$r['cores']>4096)throw new InvalidArgumentException();foreach(['cpuName','os'] as $k)if(!is_string($r[$k])||mb_strlen($r[$k])>200)throw new InvalidArgumentException();if(isset($r['elevated'])&&!is_bool($r['elevated']))throw new InvalidArgumentException();if(isset($r['agentVersion'])&&(!is_string($r['agentVersion'])||!preg_match('/^\d+\.\d+\.\d+(?:\.\d+)?$/D',$r['agentVersion'])))throw new InvalidArgumentException();if(isset($r['gpuControl'])&&(!is_array($r['gpuControl'])||strlen(json_encode($r['gpuControl']))>1500))throw new InvalidArgumentException();if(isset($r['inventory'])&&(!is_array($r['inventory'])||strlen(json_encode($r['inventory'],JSON_UNESCAPED_UNICODE))>30000||array_diff(array_keys($r['inventory']),['cpu','gpu','board','bios','memory','disks','applications','startup','services','network','unavailable'])))throw new InvalidArgumentException();if(isset($r['processes'])){if(!is_array($r['processes'])||count($r['processes'])>15)throw new InvalidArgumentException();foreach($r['processes'] as $v)if(!is_array($v)||count($v)!==3||!isset($v['name'],$v['cpuPercent'],$v['memoryMB'])||!is_string($v['name'])||mb_strlen($v['name'])>100||!is_numeric($v['cpuPercent'])||$v['cpuPercent']<0||$v['cpuPercent']>100||!is_numeric($v['memoryMB'])||$v['memoryMB']<0||$v['memoryMB']>10000000)throw new InvalidArgumentException();}}
try{
 $now=time();foreach(['sessions','pairs','oauth'] as $collection)foreach($S[$collection] as $k=>$v)if(($v['expires']??0)<$now)unset($S[$collection][$k]);foreach($S['limits'] as $k=>$v)if($now-$v['t']>86400)unset($S['limits'][$k]);foreach($S['jobs'] as $k=>$v)if(($v['expires']??0)<$now-604800)unset($S['jobs'][$k]);
 $ip=hashed($_SERVER['REMOTE_ADDR']??'');limit('ip:'.$ip,100,10);
 $sk=hashed($_COOKIE['moot']??'');$dk=hashed(preg_replace('/^Bearer /','',$_SERVER['HTTP_AUTHORIZATION']??''));$session=$S['sessions'][$sk]??null;$device=$S['devices'][$dk]??null;if($device&&(!$device['active']||$device['expires']<$now))$device=null;
 $isAgent=str_starts_with($path,'/api/agent/');$actor=$isAgent?$device:$session;
 $rates=['/api/chat'=>[8,.5],'/api/pair'=>[2,5/60],'/api/agent/pair'=>[3,5/60],'/api/promo'=>[2,5/60],'/api/agent/telemetry'=>[4,.5],'/api/state'=>[10,1],'/api/events'=>[10,1],'/api/agent/events'=>[10,1],'/download/MooT-Setup.exe'=>[2,.05]];[$burst,$rate]=$rates[$path]??[30,4];limit($path.':'.($actor['id']??$ip),$burst,$rate);
 if($method==='POST'){
  if(($_SERVER['CONTENT_LENGTH']??0)>48000)fail('Максимальный размер запроса — 48 КБ',413);
  if(!str_starts_with($_SERVER['CONTENT_TYPE']??'','application/json'))fail('Требуется JSON',415);
  $raw=file_get_contents('php://input',false,null,0,48001);if(strlen($raw)>48000)fail('Слишком большой запрос',413);$body=json_decode($raw,true,32,JSON_THROW_ON_ERROR);if(!is_array($body)||(!str_starts_with(trim($raw),'{')))fail('Некорректные данные');
  if(isset($_SERVER['HTTP_ORIGIN'])&&$_SERVER['HTTP_ORIGIN']!==$origin)fail('Origin rejected',403);
  if(!$isAgent&&$path!=='/api/session'){if(!$session)fail('Войдите в аккаунт',401);if(!hash_equals($session['csrf'],$_SERVER['HTTP_X_CSRF_TOKEN']??''))fail('CSRF rejected',403);}
 }
 if($path==='/api/agent-release'&&$method==='GET'){ $file=__DIR__.'/agent-release.json';if(!is_file($file))fail('Release unavailable',503);output(json_decode(file_get_contents($file),true,512,JSON_THROW_ON_ERROR));}
 if($path==='/api/config')output(['version'=>'1.3.3-php','googleRequired'=>!$local,'googleAvailable'=>!empty($cfg['google_id'])&&!empty($cfg['google_secret']),'aiConfigured'=>ai_configured()]);
 if($path==='/auth/google/start'){
  if(empty($cfg['google_id'])||empty($cfg['google_secret']))fail('Вход через Google ещё не настроен владельцем сайта',503);
  $state=token();$nonce=token();$verifier=token();$bind=token();$S['oauth'][$state]=['nonce'=>$nonce,'verifier'=>$verifier,'binding'=>hashed($bind),'expires'=>$now+600];cookie('moot_oauth',$bind,600);
  redirect('https://accounts.google.com/o/oauth2/v2/auth?'.http_build_query(['client_id'=>$cfg['google_id'],'redirect_uri'=>$origin.'/auth/google/callback','response_type'=>'code','scope'=>'openid email profile','state'=>$state,'nonce'=>$nonce,'code_challenge'=>rtrim(strtr(base64_encode(hash('sha256',$verifier,true)),'+/','-_'),'='),'code_challenge_method'=>'S256','prompt'=>'select_account']));
 }
 if($path==='/auth/google/callback'){
  $flow=$S['oauth'][$_GET['state']??'']??null;unset($S['oauth'][$_GET['state']??'']);if(!$flow||!hash_equals($flow['binding'],hashed($_COOKIE['moot_oauth']??''))||empty($_GET['code']))redirect('/optimization/?login=failed');
  unlock();try{$r=http_json('https://oauth2.googleapis.com/token',['code'=>$_GET['code'],'client_id'=>$cfg['google_id'],'client_secret'=>$cfg['google_secret'],'redirect_uri'=>$origin.'/auth/google/callback','grant_type'=>'authorization_code','code_verifier'=>$flow['verifier']],[],true);$claims=google_identity($r['id_token'],$flow['nonce']);}catch(Throwable $e){relock();redirect('/optimization/?login=failed');}relock();$uid=$claims['sub'];$S['users'][$uid]??=['id'=>(string)random_int(10000000,99999999),'name'=>mb_substr($claims['name']??'Пользователь',0,100),'full'=>false];$sid=token();$S['sessions'][hashed($sid)]=['id'=>$uid,'user'=>$S['users'][$uid],'csrf'=>token(),'google'=>true,'expires'=>$now+86400];cookie('moot',$sid);cookie('moot_oauth','',-1);redirect('/optimization/');
 }
 if($path==='/api/session'&&$method==='POST'){
  if(!$local)fail('Войдите через Google',403);if(($_SERVER['HTTP_ORIGIN']??'')!==$origin)fail('Origin required',403);$sid=token();$uid=token();$user=['id'=>(string)random_int(10000000,99999999),'name'=>'Локальный пользователь','full'=>false];$S['users'][$uid]=$user;$S['sessions'][hashed($sid)]=['id'=>$uid,'user'=>$user,'csrf'=>token(),'google'=>false,'expires'=>$now+86400];cookie('moot',$sid);output(['ok'=>true]);
 }
 if($path==='/api/agent/pair'&&$method==='POST'){
  $failures=array_values(array_filter($S['failures'][$ip]??[],fn($t)=>$t>$now-900));if(count($failures)>=5)fail('Повторите сопряжение через 15 минут',429);$code=(string)($body['code']??'');$pair=$S['pairs'][$code]??null;unset($S['pairs'][$code]);if(!$pair){$failures[]=$now;$S['failures'][$ip]=$failures;fail('Код недействителен или истёк');}foreach($S['devices'] as &$d)if($d['owner']===$pair['owner'])$d['active']=false;unset($d);$t=token();$S['devices'][hashed($t)]=['id'=>token(),'owner'=>$pair['owner'],'name'=>mb_substr((string)($body['name']??'Windows PC'),0,60),'active'=>true,'access'=>false,'scope'=>'','seen'=>$now,'expires'=>$now+2592000];event($pair['owner'],'Агент сопряжён. Ожидается согласие на ПК.');output(['token'=>$t,'user'=>$pair['user']]);
 }
 if($isAgent){
  if(!$device)fail('Устройство отключено',401);$d=&$S['devices'][$dk];
  if($method==='GET'&&in_array($path,['/api/agent/events','/api/agent/jobs'],true)){$r=jobs_for($dk);if(str_ends_with($path,'events'))stream_event($r);output($r);}
  if($path==='/api/agent/update-download'&&$method==='GET'){unlock();$file=__DIR__.'/downloads/MooT-Setup.exe';header('Content-Type: application/octet-stream');header('Content-Disposition: attachment; filename="MooT-Setup.exe"');header('Content-Length: '.filesize($file));readfile($file);exit;}
  if($method!=='POST')fail('Not found',404);
  if($path==='/api/agent/grant'){if(($body['consent']??false)!==true||!in_array($body['scope']??'',['diagnostics.read','optimization.v1'],true))fail('Недопустимый доступ');$d['access']=true;$d['scope']=$body['scope'];event($d['owner'],'Доступ предоставлен в Windows-приложении');output(['ok'=>true]);}
  if($path==='/api/agent/disconnect'){$d['active']=false;output(['ok'=>true]);}
  if($path==='/api/agent/progress'){
   if(!$d['access'])fail('Доступ не предоставлен',403);$id=$body['id']??'';$phase=$body['phase']??'';$job=$S['jobs'][$id]??null;
   if(!$job||$job['device']!==$d['id']||$job['status']!=='dispatched'||!in_array($phase,['running','waiting_confirmation'],true))fail('Invalid progress');
   $S['jobs'][$id]['phase']=$phase;$d['seen']=$now;output(['ok'=>true]);
  }
  if(!in_array($path,['/api/agent/result','/api/agent/telemetry'],true))fail('Not found',404);if(!$d['access'])fail('Доступ не предоставлен',403);
  $telemetry=$path==='/api/agent/telemetry';$id=$body['id']??'';$j=$telemetry?['device'=>$d['id'],'type'=>'diagnostics.read','status'=>'dispatched','automatic'=>true]:($S['jobs'][$id]??null);$r=$body['result']??null;
  if(!$j||$j['device']!==$d['id']||!is_array($r))fail('Invalid job');if(in_array($j['status'],['complete','failed'],true)&&($j['result']??null)===$r)output(['ok'=>true]);if(!in_array($j['status'],['dispatched','interrupted'],true))fail('Invalid job');
  if($j['type']!=='diagnostics.read'||($r['status']??'')==='failed'){
   if(array_diff(array_keys($r),['status','summary','snapshotId'])||!in_array($r['status']??'',['complete','failed'],true)||!is_string($r['summary']??null)||mb_strlen($r['summary'])>500)fail('Invalid result');$snapshot=$r['snapshotId']??'';if(!is_string($snapshot)||($snapshot!==''&&!preg_match('/^[a-f0-9]{32}$/D',$snapshot)))fail('Invalid snapshot');if($r['status']==='complete'&&in_array($j['type'],['optimization.apply','optimization.rollback','power.apply','overclock.apply','overclock.rollback'],true)&&!$snapshot)fail('Missing snapshot');$S['jobs'][$id]=array_merge($j,['status'=>$r['status'],'summary'=>$r['summary'],'result'=>$r]);$d['seen']=$now;
   if($r['status']==='complete'){
    if(in_array($j['type'],['optimization.apply','power.apply'],true))$d['snapshotId']=$snapshot;
    if($j['type']==='optimization.rollback')unset($d['snapshotId']);if($j['type']==='overclock.apply')$d['gpuSnapshot']=$snapshot;if($j['type']==='overclock.rollback')unset($d['gpuSnapshot']);
    if($j['type']==='elevation.request'&&($j['next']??'')==='overclock.apply')queue($d,'overclock.apply');
    if($j['type']==='elevation.request'&&($j['next']??'')==='optimization.apply')queue($d,'optimization.apply',['mode'=>$j['mode'],'plan'=>$j['plan']]);
   }event($d['owner'],$r['summary']);chat_add($d['owner'],'assistant',($r['status']==='complete'?'Выполнено: ':'Не выполнено: ').$r['summary']);output(['ok'=>true]);
  }
  validate_telemetry($r);$d['telemetry']=array_merge($d['telemetry']??[],$r);$d['telemetryAt']=$now;$d['seen']=$now;if(!$telemetry)$S['jobs'][$id]=array_merge($j,['status'=>'complete','result'=>$r]);$rule=$d['memoryRule']??null;if($rule&&$r['memoryPercent']>=$rule['threshold']&&$now-($rule['lastRun']??0)>=600&&$d['scope']==='optimization.v1'){try{queue($d,'memory.clean');$d['memoryRule']['lastRun']=$now;}catch(RuntimeException $e){}}output(['ok'=>true]);
 }
 if(!$session)fail('Войдите через Google',401);$s=&$S['sessions'][$sk];$owner=$s['id'];
 if($path==='/api/state'&&$method==='GET')output(state_for($sk));
 if($path==='/api/events'&&$method==='GET')stream_event(state_for($sk));
 if($path==='/download/MooT-Setup.exe'&&$method==='GET'){unlock();$file=__DIR__.'/downloads/MooT-Setup.exe';header('Content-Type: application/octet-stream');header('Content-Disposition: attachment; filename="MooT-Setup.exe"');header('Content-Length: '.filesize($file));readfile($file);exit;}
 if($method!=='POST')fail('Not found',404);
 if($path==='/api/logout'){unset($S['sessions'][$sk]);cookie('moot','',-1);output(['ok'=>true]);}
 if($path==='/api/promo'){if(!hash_equals(mb_strtolower($cfg['promo']),mb_strtolower(trim((string)($body['code']??'')))))fail('Неверный промокод',403);$S['users'][$owner]['full']=true;output(['ok'=>true,'message'=>'Полный режим активирован для аккаунта. Действия требуют согласия на ПК.']);}
 if($path==='/api/pair'){foreach($S['pairs'] as $k=>$p)if($p['owner']===$owner)unset($S['pairs'][$k]);do{$code=(string)random_int(1000000,9999999);}while(isset($S['pairs'][$code]));$S['pairs'][$code]=['owner'=>$owner,'expires'=>$now+180,'user'=>$s['user']['name'].' · ID '.$s['user']['id']];output(['code'=>$code,'expiresIn'=>180]);}
 $ds=array_filter($S['devices'],fn($d)=>$d['owner']===$owner&&$d['active']&&$d['expires']>$now);
 if($path==='/api/disconnect'){foreach($ds as $k=>$d)$S['devices'][$k]['active']=false;event($owner,'Доступ всех устройств отозван');output(['ok'=>true]);}
 if($path==='/api/diagnose'){if(!$ds)fail('Сначала подключите агент',409);$sent=0;foreach($ds as $d)if($d['access']){queue($d,'diagnostics.read');$sent++;}if(!$sent)fail('Подтвердите доступ в приложении',409);output(['ok'=>true]);}
 if($path!=='/api/chat')fail('Not found',404);
 require __DIR__.'/chat.php';
}catch(Throwable $e){if(!$locked){try{relock();}catch(Throwable $ignored){http_response_code(503);exit;}}if(isset($owner))unset($S['busy'][$owner]);if($e instanceof InvalidArgumentException||$e instanceof JsonException)fail('Некорректные данные',400);fail('Не удалось выполнить запрос. Проверьте подключение и настройки сервера.',502);}
