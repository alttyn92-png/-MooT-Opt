<?php
declare(strict_types=1);
ini_set('display_errors','0');
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');
$config=require __DIR__.'/config.php';
$local=getenv('MOOT_PHP_LOCAL')==='1'&&in_array($_SERVER['REMOTE_ADDR']??'',['127.0.0.1','::1'],true);
$origin=$local?'http://'.($_SERVER['HTTP_HOST']??''):$config['origin'];
function stats_reply(array $data,int $status=200):never{http_response_code($status);echo json_encode($data);exit;}
if(!$local&&(empty($_SERVER['HTTPS'])||($_SERVER['HTTP_HOST']??'')!==parse_url($origin,PHP_URL_HOST)))stats_reply(['error'=>'Invalid host'],403);
$method=$_SERVER['REQUEST_METHOD'];
if(!in_array($method,['GET','POST'],true))stats_reply(['error'=>'Method not allowed'],405);
$event=null;
if($method==='POST'){
 if(($_SERVER['HTTP_ORIGIN']??'')!==$origin)stats_reply(['error'=>'Origin rejected'],403);
 if(!str_starts_with($_SERVER['CONTENT_TYPE']??'','application/json'))stats_reply(['error'=>'JSON required'],415);
 $raw=file_get_contents('php://input',false,null,0,1025);
 if(strlen($raw)>1024)stats_reply(['error'=>'Request too large'],413);
 $body=json_decode($raw,true);$event=is_array($body)?($body['event']??null):null;
 if(!is_string($event)||!preg_match('/^[a-f0-9-]{32,36}$/D',$event))stats_reply(['error'=>'Invalid event'],400);
}
try{
 $db=new PDO('sqlite:'.__DIR__.'/project-stats.sqlite');$db->setAttribute(PDO::ATTR_ERRMODE,PDO::ERRMODE_EXCEPTION);
 $db->exec('PRAGMA busy_timeout=5000; CREATE TABLE IF NOT EXISTS project_counts (project TEXT PRIMARY KEY, opens INTEGER NOT NULL); CREATE TABLE IF NOT EXISTS project_events (event TEXT PRIMARY KEY, created INTEGER NOT NULL)');
 $db->exec('BEGIN IMMEDIATE');
 $db->exec("INSERT OR IGNORE INTO project_counts VALUES ('optimization',161)");
 if($event!==null){
  $q=$db->prepare('INSERT OR IGNORE INTO project_events VALUES (?,?)');$q->execute([$event,time()]);
  if($q->rowCount()===1)$db->exec("UPDATE project_counts SET opens=opens+1 WHERE project='optimization'");
  $db->exec('DELETE FROM project_events WHERE created<'.(time()-86400));
 }
 $count=(int)$db->query("SELECT opens FROM project_counts WHERE project='optimization'")->fetchColumn();
 $db->exec('COMMIT');stats_reply(['project'=>'optimization','opens'=>$count,'initialValue'=>161]);
}catch(Throwable $e){stats_reply(['error'=>'Counter temporarily unavailable'],503);}
