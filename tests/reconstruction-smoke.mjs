// Isolated Native AOT server + real Edge WebGL. Never touches production data or phone media.
import {spawn} from 'node:child_process';
import {mkdir,readFile,writeFile,readdir} from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import {randomBytes} from 'node:crypto';
import {setTimeout as delay} from 'node:timers/promises';
const root=path.resolve('.test-data/reconstruction-test-'+Date.now());await mkdir(root,{recursive:true});
const executable=path.resolve(process.argv.find(x=>x.startsWith('--server-exe='))?.slice(13)??'artifacts/reconstruction-update/server/MyPan.Server.exe');
const engine=path.resolve(process.argv.find(x=>x.startsWith('--engine='))?.slice(9)??'artifacts/reconstruction-native/win-x64/yunsevo-reconstruct.exe');
const socket=net.createServer();await new Promise(r=>socket.listen(0,'127.0.0.1',r));const port=socket.address().port;await new Promise(r=>socket.close(r));
const url='http://127.0.0.1:'+port,password=randomBytes(24).toString('hex'),env={...process.env,ASPNETCORE_URLS:url,MyPan__DataPath:path.join(root,'data'),MyPan__ApiKeyFile:path.join(root,'api-key.txt'),MyPan__InstallationFile:path.join(root,'settings.json'),MyPan__UnattendedSetup:'true',MyPan__AdminPassword:password,MyPan__ReconstructionEngine:engine,Logging__LogLevel__Default:'Warning'};
let server,browser,ws,logs='',browserLog='',checks=[];const errors=[];
function assert(ok,text){if(!ok)throw new Error(text);checks.push(text);console.log('PASS',text);}
async function until(test,label,timeout=30000){const end=Date.now()+timeout;while(Date.now()<end){const value=await test();if(value)return value;await delay(200);}throw new Error('Timed out: '+label);}
let cdp,sequence=0;const pending=new Map();
async function send(method,params={}){const id=++sequence;return new Promise((resolve,reject)=>{pending.set(id,{resolve,reject});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const value=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true,userGesture:true});if(value.exceptionDetails)throw new Error(value.exceptionDetails.exception?.description??'Browser failure');return value.result.value;}
async function screenshot(name){const {data}=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await writeFile(path.join(root,name+'.png'),Buffer.from(data,'base64'));}
try{
 server=spawn(executable,[],{env,windowsHide:true,stdio:['ignore','pipe','pipe']});server.stdout.on('data',x=>logs+=x);server.stderr.on('data',x=>logs+=x);
 await until(async()=>{if(server.exitCode!==null)throw new Error('Server exited: '+logs);try{return(await fetch(url+'/health')).ok;}catch{return false;}},'server startup');
 const key=(await readFile(env.MyPan__ApiKeyFile,'utf8')).trim(),headers={Authorization:'Bearer '+key};
 const api=async(route,method='GET',body,extra={})=>{const r=await fetch(url+route,{method,headers:{...headers,...(body?{'Content-Type':'application/json'}:{}),...extra},body:body?JSON.stringify(body):undefined});if(!r.ok)throw new Error(route+' '+r.status+' '+await r.text());return r.status===204?null:r.json();};
 assert((await fetch(url+'/api/reconstruction/jobs')).status===401,'Reconstruction jobs require authentication');
 assert((await fetch(url+'/model-viewer/index.html')).status===401,'Model viewer is authenticated');
 const caps=await api('/api/reconstruction/capabilities');assert(caps.cpu&&caps.vulkan&&caps.experimental,'Actual native Vulkan and CPU capabilities are reported');
 const folder=await api('/api/folders','POST',{path:'三维原片'});const photos=[];
 for(const name of (await readdir('.test-data/reconstruction/synthetic48')).sort()){
  const bytes=await readFile(path.join('.test-data/reconstruction/synthetic48',name));const r=await fetch(url+'/api/content?path='+encodeURIComponent(folder.path+'/'+name),{method:'PUT',headers:{...headers,'If-None-Match':'*'},body:bytes});if(!r.ok)throw new Error('Photo upload failed');const entry=await r.json();photos.push({id:entry.id,version:entry.version});
 }
 assert(photos.length===48,'48 original photographs uploaded through normal ACL and quota path');
 const submit={folderId:folder.id,name:'测试设备',photos,backend:'vulkan',subject:'object',quality:0,clientRequestId:'62f36db8-1576-46d0-ac83-9c02a491f151'};
 const invalid=await fetch(url+'/api/reconstruction/jobs',{method:'POST',headers:{...headers,'Content-Type':'application/json'},body:JSON.stringify({...submit,photos:photos.slice(0,5)})});assert(invalid.status===400,'Insufficient capture set is rejected');
 const duplicate=await fetch(url+'/api/reconstruction/jobs',{method:'POST',headers:{...headers,'Content-Type':'application/json'},body:JSON.stringify({...submit,photos:Array(6).fill(photos[0])})});assert(duplicate.status===400,'Duplicate photo references are rejected');
 const job=await api('/api/reconstruction/jobs','POST',submit);assert(job.state==='queued'&&job.userId,'Durable job is owned by the authenticated account');
 const retry=await api('/api/reconstruction/jobs','POST',submit);assert(retry.id===job.id,'Retrying a mobile submission is idempotent');
 const cancelled=await api('/api/reconstruction/jobs','POST',{...submit,name:'已取消任务',clientRequestId:'eb44d72f-7551-49ca-a1dd-7d8be62499cd'});await api('/api/reconstruction/jobs/'+cancelled.id,'DELETE');assert((await api('/api/reconstruction/jobs')).find(x=>x.id===cancelled.id).state==='cancelled','Queued job cancellation is retained');
 const login=await fetch(url+'/api/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username:'admin',password})});assert(login.ok,'Web account login succeeds');
 const cookie=login.headers.get('set-cookie').split(';')[0];
 const profile=path.join(root,'edge-profile');browser=spawn('C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe',['--headless=new','--no-first-run','--no-default-browser-check','--disable-extensions','--remote-debugging-port=0','--user-data-dir='+profile,'--window-size=1280,900','about:blank'],{windowsHide:true,stdio:['ignore','ignore','pipe']});browser.stderr.on('data',x=>browserLog+=x);
 const debugPort=await until(async()=>{try{return Number((await readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0]);}catch{return false;}},'Edge startup');const targets=await(await fetch('http://127.0.0.1:'+debugPort+'/json/list')).json();ws=new WebSocket(targets.find(x=>x.type==='page').webSocketDebuggerUrl);await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject;});
 ws.onmessage=event=>{const value=JSON.parse(event.data);if(value.id){const p=pending.get(value.id);pending.delete(value.id);if(value.error)p?.reject(new Error(value.error.message));else p?.resolve(value.result);}else if(value.method==='Runtime.exceptionThrown')errors.push(value.params.exceptionDetails.exception?.description??value.params.exceptionDetails.text);};
 await send('Page.enable');await send('Runtime.enable');await send('Network.enable');await send('Network.setCookie',{url,name:'mypan-session',value:cookie.slice('mypan-session='.length),httpOnly:true,sameSite:'Strict'});
 await send('Page.navigate',{url:url+'/#view=files&folder='+encodeURIComponent(folder.path)});
 await until(()=>evaluate('!!window.MyPan&&window.MyPan.state.authenticated'),'restore browser session');
 await evaluate(`window.MyPan.state.folder=${JSON.stringify(folder.path)};document.getElementById('open-reconstruction').click()`);
 await until(()=>evaluate("document.querySelectorAll('#reconstruction-photos input').length===48"),'source photo checklist');
 await until(()=>evaluate("document.querySelectorAll('#reconstruction-jobs .management-row').length===2"),'job list rendering');
 assert(await evaluate("document.getElementById('reconstruction-dialog').open&&document.querySelectorAll('#reconstruction-jobs .management-row').length===2"),'Web reconstruction dialog shows original photos and durable jobs');await screenshot('server-reconstruction');
 const completed=await until(async()=>{const value=(await api('/api/reconstruction/jobs')).find(x=>x.id===job.id);if(value.state==='failed')throw new Error(value.message);return value.state==='completed'?value:false;},'real mesh reconstruction',600000);
 assert(completed.model?.path.endsWith('.glb')&&completed.progress===100,'Vulkan reconstructed actual GLB and published it through CloudStore');
 const response=await fetch(url+'/api/preview/'+completed.model.id+'?version='+completed.model.version,{headers});assert(response.headers.get('content-type')==='model/gltf-binary','GLB preview preserves authenticated version and MIME');const bytes=Buffer.from(await response.arrayBuffer());assert(bytes.toString('ascii',0,4)==='glTF'&&bytes.readUInt32LE(8)===bytes.length,'Generated GLB has valid binary header and length');
 const metadata=JSON.parse(bytes.subarray(20,20+bytes.readUInt32LE(12)).toString());assert(metadata.extras.registeredCameras>=34&&metadata.meshes[0].primitives.reduce((n,p)=>n+metadata.accessors[p.indices].count,0)>3000&&metadata.extras.backend.includes('vulkan'),'GLB contains measured multi-view mesh and actual Vulkan provenance');
 assert(metadata.images?.length>0&&metadata.meshes[0].primitives.some(p=>p.attributes.TEXCOORD_0!==undefined),'Generated model embeds original-photo textures and UV coordinates');
 const iframe=url+'/model-viewer/index.html?id='+completed.model.id+'&version='+completed.model.version;await send('Page.navigate',{url:iframe});await until(()=>evaluate('!!document.getElementById("model")?.loaded'),'WebGL mesh viewer',120000);assert(await evaluate('document.getElementById("model").modelIsVisible'),'Real WebGL model is visible');await screenshot('server-model-orbit');
 const before=await evaluate('document.getElementById("model").turntableRotation');await evaluate('document.getElementById("rotate").click()');await delay(2200);assert(await evaluate('document.getElementById("model").turntableRotation')!==before,'Automatic orbit rotates the actual 3D model');
 await evaluate('document.getElementById("reset").click()');assert(await evaluate('!document.getElementById("model").autoRotate&&document.getElementById("model").turntableRotation===0'),'Reset stops orbit and restores view');
 await send('Emulation.setDeviceMetricsOverride',{width:412,height:915,deviceScaleFactor:1,mobile:true});await screenshot('model-mobile-layout');assert(await evaluate('document.documentElement.scrollWidth<=innerWidth'),'Model viewer adapts to narrow mobile screens');

 const backupResponse=await fetch(url+'/api/admin/backup',{headers});assert(backupResponse.ok,'Administrator can back up generated models and job history');
 const archive=path.join(root,'migration.zip'),destination=path.join(root,'restored');await writeFile(archive,Buffer.from(await backupResponse.arrayBuffer()));
 const restore=spawn('powershell',['-NoProfile','-ExecutionPolicy','Bypass','-File','scripts/restore-backup.ps1','-Archive',archive,'-Destination',destination],{windowsHide:true,stdio:['ignore','pipe','pipe']});let restoreLog='';restore.stdout.on('data',x=>restoreLog+=x);restore.stderr.on('data',x=>restoreLog+=x);const restoreCode=await new Promise(r=>restore.once('exit',r));await writeFile(path.join(root,'restore.log'),restoreLog);assert(restoreCode===0,'Migration restore accepts strictly scoped reconstruction records and validates hashes');
 const restoredJob=JSON.parse(await readFile(path.join(destination,'data/reconstruction',job.id.replaceAll('-',''),'job.json'),'utf8'));assert((restoredJob.Job??restoredJob.job).Id===job.id,'Reconstruction job identity survives migration');
 assert(errors.length===0,'No browser JavaScript exceptions');
 await writeFile(path.join(root,'verification.json'),JSON.stringify({success:true,checks,model:completed.model.path,vertices:metadata.accessors[0].count,triangles:metadata.meshes[0].primitives.reduce((n,p)=>n+metadata.accessors[p.indices].count/3,0),backend:metadata.extras.backend},null,2));console.log('Verification:',root);
}catch(e){await writeFile(path.join(root,'failure.json'),JSON.stringify({error:e.stack,checks,errors},null,2));throw e;}
finally{ws?.close();if(browser?.exitCode===null)browser.kill();if(server?.exitCode===null)server.kill();await writeFile(path.join(root,'server.log'),logs);await writeFile(path.join(root,'browser.log'),browserLog);}
