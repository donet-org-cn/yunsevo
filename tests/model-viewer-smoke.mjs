// Existing GLB viewing, ACL, trash and removed generation endpoints. No capture
// or reconstruction runtime, production data, phone files or external assets.
import {spawn} from 'node:child_process';
import {mkdir,readFile,writeFile} from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import {randomBytes,createHash} from 'node:crypto';
import {setTimeout as delay} from 'node:timers/promises';
const root=path.resolve('.test-data/model-viewer-'+Date.now());await mkdir(root,{recursive:true});
const executable=path.resolve(process.argv.find(x=>x.startsWith('--server='))?.slice(9)??'src/Yunsevo.Server/bin/Release/net10.0/Yunsevo.Server.dll');
const socket=net.createServer();await new Promise(r=>socket.listen(0,'127.0.0.1',r));const port=socket.address().port;await new Promise(r=>socket.close(r));
const url='http://127.0.0.1:'+port,password=randomBytes(24).toString('hex');
const env={...process.env,ASPNETCORE_URLS:url,Yunsevo__DataPath:path.join(root,'data'),Yunsevo__ApiKeyFile:path.join(root,'api-key.txt'),Yunsevo__InstallationFile:path.join(root,'settings.json'),Yunsevo__UnattendedSetup:'true',Yunsevo__AdminPassword:password,Logging__LogLevel__Default:'Warning'};
let server,browser,ws,logs='',browserLog='',sequence=0;const pending=new Map(),errors=[],checks=[];
function assert(ok,label){if(!ok)throw new Error(label);checks.push(label);console.log('PASS',label);}
async function until(test,label,timeout=30000){const end=Date.now()+timeout;while(Date.now()<end){const value=await test();if(value)return value;await delay(200);}throw new Error('Timeout: '+label);}
async function send(method,params={}){const id=++sequence;return new Promise((resolve,reject)=>{pending.set(id,{resolve,reject});ws.send(JSON.stringify({id,method,params}));});}
async function evaluate(expression){const r=await send('Runtime.evaluate',{expression,awaitPromise:true,returnByValue:true,userGesture:true});if(r.exceptionDetails)throw new Error(r.exceptionDetails.exception?.description??'Browser error');return r.result.value;}
async function screenshot(name){const {data}=await send('Page.captureScreenshot',{format:'png',captureBeyondViewport:false});await writeFile(path.join(root,name+'.png'),Buffer.from(data,'base64'));return createHash('sha256').update(data).digest('hex');}
try{
 server=spawn(executable.endsWith('.dll')?'dotnet':executable,executable.endsWith('.dll')?[executable]:[],{env,windowsHide:true,stdio:['ignore','pipe','pipe']});server.stdout.on('data',x=>logs+=x);server.stderr.on('data',x=>logs+=x);
 await until(async()=>{if(server.exitCode!==null)throw new Error(logs);try{return(await fetch(url+'/health')).ok;}catch{return false;}},'server startup');
 const key=(await readFile(env.Yunsevo__ApiKeyFile,'utf8')).trim(),headers={Authorization:'Bearer '+key};
 const api=async(route,method='GET',body)=>{const r=await fetch(url+route,{method,headers:{...headers,...(body?{'Content-Type':'application/json'}:{})},body:body?JSON.stringify(body):undefined});if(!r.ok)throw new Error(route+' '+r.status+' '+await r.text());return r.status===204?null:r.json();};
 assert((await fetch(url+'/model-viewer/index.html')).status===401,'Model viewer requires login');
 for(const endpoint of ['/api/reconstruction/capabilities','/api/reconstruction/jobs','/assets/reconstruction.js'])assert((await fetch(url+endpoint,{headers})).status===404,'Retired endpoint removed: '+endpoint);
 assert(!(await(await fetch(url)).text()).includes('open-reconstruction'),'Web page has no capture or synthesis action');
 const folder=await api('/api/folders','POST',{path:'Models'}),bytes=await readFile('tests/fixtures/colored-box.glb');
 const uploaded=await fetch(url+'/api/content?path='+encodeURIComponent(folder.path+'/existing.glb'),{method:'PUT',headers:{...headers,'If-None-Match':'*'},body:bytes});assert(uploaded.ok,'GLB can be uploaded as an ordinary file');const model=await uploaded.json();
 const preview=await fetch(url+'/api/preview/'+model.id+'?version='+model.version,{headers});assert(preview.headers.get('content-type')==='model/gltf-binary'&&Buffer.from(await preview.arrayBuffer()).equals(bytes),'Authenticated model preview preserves exact GLB bytes');
 const other=await api('/api/admin/users','POST',{username:'viewer-other',displayName:'Other',password:randomBytes(24).toString('hex')});assert(other.id,'Independent account created');
 const login=await fetch(url+'/api/auth/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username:'admin',password})});assert(login.ok,'Web login works');const cookie=login.headers.get('set-cookie').split(';')[0];
 const profile=path.join(root,'edge-profile');browser=spawn('C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',['--headless=new','--no-first-run','--no-default-browser-check','--disable-extensions','--remote-debugging-port=0','--user-data-dir='+profile,'--window-size=1280,900','about:blank'],{windowsHide:true,stdio:['ignore','ignore','pipe']});browser.stderr.on('data',x=>browserLog+=x);
 const debugPort=await until(async()=>{try{return Number((await readFile(path.join(profile,'DevToolsActivePort'),'utf8')).split('\n')[0]);}catch{return false;}},'Edge startup');const targets=await(await fetch('http://127.0.0.1:'+debugPort+'/json/list')).json();ws=new WebSocket(targets.find(x=>x.type==='page').webSocketDebuggerUrl);await new Promise((resolve,reject)=>{ws.onopen=resolve;ws.onerror=reject;});
 ws.onmessage=e=>{const v=JSON.parse(e.data);if(v.id){const p=pending.get(v.id);pending.delete(v.id);v.error?p?.reject(new Error(v.error.message)):p?.resolve(v.result);}else if(v.method==='Runtime.exceptionThrown')errors.push(v.params.exceptionDetails.exception?.description??v.params.exceptionDetails.text);};
 await send('Page.enable');await send('Runtime.enable');await send('Network.enable');await send('Network.setCookie',{url,name:'yunsevo-session',value:cookie.slice('yunsevo-session='.length),httpOnly:true,sameSite:'Strict'});
 await send('Page.navigate',{url:url+'/#view=files&folder='+encodeURIComponent(folder.path)});await until(()=>evaluate('!!window.Yunsevo?.state.authenticated'),'browser session');
 assert(await evaluate("!document.getElementById('open-reconstruction')"),'Authenticated browser has no generation button');
 const viewer=url+'/model-viewer/index.html?id='+model.id+'&version='+model.version;await send('Page.navigate',{url:viewer});await until(()=>evaluate('!!document.getElementById("model")?.loaded'),'WebGL model',120000);
 assert(await evaluate('document.getElementById("model").modelIsVisible'),'Existing GLB renders in real WebGL');const before=await screenshot('model-before');
 await evaluate('document.getElementById("model").cameraOrbit="90deg 70deg auto"');await delay(500);const rotated=await screenshot('model-rotated');assert(rotated!==before,'Changing camera rotation changes rendered pixels');
 await evaluate('document.getElementById("model").cameraOrbit="90deg 70deg 2.5m"');await delay(500);assert(await screenshot('model-zoomed')!==rotated,'Zoom changes rendered pixels');
 const angle=await evaluate('document.getElementById("model").turntableRotation');await evaluate('document.getElementById("rotate").click()');await delay(1000);assert(await evaluate('document.getElementById("model").turntableRotation')!==angle,'Automatic orbit remains available');
 await evaluate('document.getElementById("reset").click()');assert(await evaluate('!document.getElementById("model").autoRotate'),'Reset stops automatic rotation');
 await send('Emulation.setDeviceMetricsOverride',{width:412,height:915,deviceScaleFactor:1,mobile:true});assert(await evaluate('document.documentElement.scrollWidth<=innerWidth'),'Viewer fits mobile width');await screenshot('model-mobile');
 const deleted=await fetch(url+'/api/entries/'+model.id,{method:'DELETE',headers:{...headers,'If-Match':'"'+model.version+'"'}});assert(deleted.ok,'Model can be moved to the recycle bin with version protection');
 const trash=await fetch(url+'/api/trash/'+model.id+'/preview?version='+model.version,{headers});assert(trash.ok&&Buffer.from(await trash.arrayBuffer()).equals(bytes),'Recycle-bin model preview remains available');
 await send('Page.navigate',{url:viewer+'&trash=1'});await until(()=>evaluate('!!document.getElementById("model")?.loaded'),'trash WebGL');assert(await evaluate('document.getElementById("model").modelIsVisible'),'Recycle-bin GLB renders');
 assert(errors.length===0,'No browser JavaScript exceptions');await writeFile(path.join(root,'verification.json'),JSON.stringify({success:true,checks},null,2));console.log('Verification:',root);
}catch(e){await writeFile(path.join(root,'failure.json'),JSON.stringify({error:e.stack,checks,errors},null,2));throw e;}
finally{ws?.close();if(browser?.exitCode===null)browser.kill();if(server?.exitCode===null)server.kill();await writeFile(path.join(root,'server.log'),logs);await writeFile(path.join(root,'browser.log'),browserLog);}
