// Isolated HTTP/process-contract tests. The stub does not test reconstruction quality.
import {spawn} from 'node:child_process';
import {mkdir, readFile, writeFile, access} from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import {randomBytes, randomUUID} from 'node:crypto';
import {setTimeout as delay} from 'node:timers/promises';

const root=path.resolve('.test-data/quality-api-'+Date.now());await mkdir(root,{recursive:true});
const executable=path.resolve(process.argv.find(x=>x.startsWith('--server-exe='))?.slice(13)??'artifacts/reconstruction-quality/server/MyPan.Server.exe');
const engine=path.resolve(process.argv.find(x=>x.startsWith('--engine='))?.slice(9)??'artifacts/reconstruction-native/win-x64/yunsevo-reconstruct.exe');
const python=process.env.MYPAN_QUALITY_PYTHON;if(!python)throw new Error('Set MYPAN_QUALITY_PYTHON to a local Python executable.');
const worker=path.join(root,'worker.py');
await writeFile(worker,`import sys,json,time,struct\nfrom pathlib import Path\nif sys.argv[1:]==['--capabilities']:\n print(json.dumps({'cpu':True,'highQuality':True,'highQualityVideo':True}));sys.exit(0)\nsource,output=Path(sys.argv[1]),Path(sys.argv[2])\nassert sys.argv[3:] == ['cpu','2','object']\nvideo=source/'video.mp4'\nassert video.exists() and len(list(source.iterdir()))==1\nprint('25\\tContract test worker',flush=True)\nif video.read_bytes()==b'cancel':time.sleep(60)\ntext=json.dumps({'asset':{'version':'2.0'},'scene':0,'scenes':[{}]}).encode()\ntext+=b' '*((-len(text))%4)\noutput.write_bytes(struct.pack('<IIIII',0x46546c67,2,20+len(text),len(text),0x4e4f534a)+text)\noutput.with_suffix('.quality.json').write_text(json.dumps({'inputPhotos':12}))\nprint('100\\tContract test complete',flush=True)\n`);
const socket=net.createServer();await new Promise(r=>socket.listen(0,'127.0.0.1',r));const port=socket.address().port;await new Promise(r=>socket.close(r));
const url='http://127.0.0.1:'+port;
const env={...process.env,ASPNETCORE_URLS:url,MyPan__DataPath:path.join(root,'data'),MyPan__ApiKeyFile:path.join(root,'api-key.txt'),MyPan__InstallationFile:path.join(root,'settings.json'),MyPan__UnattendedSetup:'true',MyPan__AdminPassword:randomBytes(24).toString('hex'),MyPan__ReconstructionEngine:engine,MyPan__ReconstructionQualityPython:path.resolve(python),MyPan__ReconstructionQualityWorker:worker,Logging__LogLevel__Default:'Warning'};
let server,logs='',checks=[];
function assert(ok,message){if(!ok)throw new Error(message);checks.push(message);console.log('PASS',message);}
async function until(fn,label,timeout=30000){const end=Date.now()+timeout;while(Date.now()<end){const value=await fn();if(value)return value;await delay(150);}throw new Error('Timeout: '+label);}
try{
 server=spawn(executable,[],{env,windowsHide:true,stdio:['ignore','pipe','pipe']});server.stdout.on('data',x=>logs+=x);server.stderr.on('data',x=>logs+=x);
 await until(async()=>{if(server.exitCode!==null)throw new Error('Server exited: '+logs);try{return(await fetch(url+'/health')).ok;}catch{return false;}},'startup');
 const key=(await readFile(env.MyPan__ApiKeyFile,'utf8')).trim(),headers={Authorization:'Bearer '+key};
 const request=(route,method='GET',body)=>fetch(url+route,{method,headers:{...headers,...(body?{'Content-Type':'application/json'}:{})},body:body?JSON.stringify(body):undefined});
 const api=async(...args)=>{const r=await request(...args);if(!r.ok)throw new Error(r.status+' '+await r.text());return r.status===204?null:r.json();};
 const caps=await api('/api/reconstruction/capabilities');assert(caps.highQuality&&caps.highQualityVideo,'Configured independent worker exposes video capability');
 const folder=await api('/api/folders','POST',{path:'Quality contract fixtures'});
 const upload=async(name,bytes)=>{const r=await fetch(url+'/api/content?path='+encodeURIComponent(folder.path+'/'+name),{method:'PUT',headers:{...headers,'If-None-Match':'*'},body:bytes});if(!r.ok)throw new Error(await r.text());return r.json();};
 const video=await upload('source.mp4',Buffer.from('contract fixture')),photo=await upload('photo.jpg',Buffer.from('contract fixture'));
 const submit={folderId:folder.id,name:'Contract fixture',photos:[],video:{id:video.id,version:video.version},quality:2,backend:'cpu',subject:'object',clientRequestId:randomUUID()};
 assert((await request('/api/reconstruction/jobs','POST',{...submit,photos:[{id:photo.id,version:photo.version}]})).status===400,'Mixed photo/video input rejected before enqueue');
 assert((await request('/api/reconstruction/jobs','POST',{...submit,quality:1})).status===400,'Video cannot silently use the draft engine');
 assert((await request('/api/reconstruction/jobs','POST',{...submit,backend:'vulkan'})).status===400,'CPU quality worker rejects explicit Vulkan');
 assert((await request('/api/reconstruction/jobs','POST',{...submit,video:{id:photo.id,version:photo.version}})).status===400,'Non-video source extension rejected');
 assert((await request('/api/reconstruction/jobs','POST',{...submit,video:{...submit.video,version:'stale'}})).status===409,'Source version is checked before worker access');
 assert((await request('/api/reconstruction/jobs','POST',{...submit,video:{id:randomUUID(),version:'missing'}})).status===409,'Unavailable source cannot bypass snapshot read permissions');
 assert((await request('/api/reconstruction/jobs','POST',{...submit,folderId:randomUUID()})).status===403,'Destination requires write permission');
 const job=await api('/api/reconstruction/jobs','POST',submit);
 assert(job.quality===2,'Accepted job reports the actual requested algorithm without silently downgrading');
 assert((await api('/api/reconstruction/jobs','POST',submit)).id===job.id,'Video submission retries are idempotent');
 const complete=await until(async()=>{const j=(await api('/api/reconstruction/jobs')).find(x=>x.id===job.id);if(j.state==='failed')throw new Error(j.message);return j.state==='completed'?j:false;},'publish');
 assert(complete.photoCount===12&&complete.model?.path.endsWith('.glb'),'External worker output and extracted-frame count are published through CloudStore');
 const report=await api('/api/reconstruction/jobs/'+job.id+'/quality');assert(report.inputPhotos===12,'Owning account can inspect the generated quality report');
 assert((await fetch(url+'/api/reconstruction/jobs/'+job.id+'/quality')).status===401,'Quality report is not public');
 const content=await fetch(url+'/api/content/'+complete.model.id+'?version='+complete.model.version,{headers});
 assert(content.ok&&Buffer.from(await content.arrayBuffer()).toString('ascii',0,4)==='glTF','Published output can be downloaded with authenticated version');
 const cancelVideo=await upload('cancel.mp4',Buffer.from('cancel'));
 const cancelled=await api('/api/reconstruction/jobs','POST',{...submit,clientRequestId:randomUUID(),video:{id:cancelVideo.id,version:cancelVideo.version}});
 await until(async()=>(await api('/api/reconstruction/jobs')).find(x=>x.id===cancelled.id)?.progress===25,'running worker');
 await api('/api/reconstruction/jobs/'+cancelled.id,'DELETE');
 await until(async()=>{try{await access(path.join(root,'data/reconstruction',cancelled.id.replaceAll('-',''),'input'));return false;}catch{return true;}},'cancel cleanup');
 const terminal=(await api('/api/reconstruction/jobs')).find(x=>x.id===cancelled.id);
 assert(terminal.state==='cancelled'&&!terminal.model,'Cancelling a running quality process cleans scratch input and publishes no model');
 await writeFile(path.join(root,'verification.json'),JSON.stringify({success:true,scope:'HTTP and process contract; not visual reconstruction quality',checks},null,2));
 console.log('Verification:',root);
}finally{if(server?.exitCode===null)server.kill();await writeFile(path.join(root,'server.log'),logs);}
