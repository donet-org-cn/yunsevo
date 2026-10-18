// Synthetic metadata workload: compare permission-filtered catalogs, never file-transfer throughput.
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { randomUUID, createHash } from 'node:crypto';
import path from 'node:path';
import net from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';
const args = Object.fromEntries(process.argv.slice(2).map(arg => { const split=arg.indexOf('='); return [arg.slice(2,split),arg.slice(split+1)]; }));
const root=path.join(process.cwd(),'.test-data','catalog-performance-'+new Date().toISOString().replace(/[:.]/g,'-')); await mkdir(root,{recursive:true});
const original=JSON.parse(await readFile(path.join(args.fixture,'data/catalog.json'),'utf8'));
const password=(await readFile(path.join(args.fixture,'key.txt'),'utf8')).trim();
const state=structuredClone(original), administrator=state.Users.find(x=>x.Username==='admin');
const viewer={...administrator,Id:randomUUID(),Username:'viewer',DisplayName:'Viewer',IsAdmin:false}; state.Users.push(viewer);
state.Entries=state.Entries.filter(x=>x.IsDirectory); state.History=[]; state.Trash=[]; state.Sessions=[]; state.Revision++;
function folder(name) { const item={Id:randomUUID(),Path:name,IsDirectory:true,Size:0,ModifiedUtc:new Date().toISOString(),Version:randomUUID().replaceAll('-',''),Access:7}; state.Entries.push(item);return item; }
function policy(item,visibility) { state.Policies.push({FolderId:item.Id,OwnerId:administrator.Id,Visibility:visibility,PublicAccess:visibility==='public'?1:0,Grants:[],QuotaBytes:null,KeepVersions:null}); }
policy(folder('团队'),'public'); const folders=[];
for(let i=0;i<100;i++){const item=folder('团队/目录-'+String(i).padStart(3,'0'));folders.push(item);policy(item,i%5===0?'private':'public');}
for(let i=0;i<3000;i++){const parent=folders[i%folders.length];state.Entries.push({Id:randomUUID(),Path:parent.Path+'/file-'+i+'.txt',IsDirectory:false,Size:0,ModifiedUtc:new Date().toISOString(),Version:randomUUID().replaceAll('-',''),Access:7});}
const results=[];
for(const [label,exe] of [['before',args.before],['after',args.after]]){
  const base=path.join(root,label),data=path.join(base,'data'); await mkdir(data,{recursive:true}); await writeFile(path.join(data,'catalog.json'),JSON.stringify(state));
  const socket=net.createServer();await new Promise(resolve=>socket.listen(0,'127.0.0.1',resolve));const port=socket.address().port;await new Promise(resolve=>socket.close(resolve));const url='http://127.0.0.1:'+port;
  const env={...process.env,ASPNETCORE_URLS:url,MyPan__ApiKey:password,MyPan__ApiKeyFile:path.join(base,'key.txt'),MyPan__InstallationFile:path.join(base,'settings.json'),MyPan__DataPath:data,Logging__LogLevel__Default:'Warning'};
  const server=spawn(exe,[],{env,windowsHide:true,stdio:['ignore','pipe','pipe']});let log='';server.stdout.on('data',bytes=>{log+=bytes});server.stderr.on('data',bytes=>{log+=bytes});
  try{
    let ready=false;for(let attempt=0;attempt<150;attempt++){if(server.exitCode!==null)throw new Error(label+' server exited');try{if((await fetch(url+'/health')).ok){ready=true;break;}}catch{}await delay(100);}if(!ready)throw new Error('Startup timeout');
    const login=await fetch(url+'/api/auth/device/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username:'viewer',password})});if(!login.ok)throw new Error('Viewer login failed'); const auth=await login.json();const headers={Authorization:'Bearer '+auth.accessToken};
    async function query(endpoint){const response=await fetch(url+endpoint,{headers});if(!response.ok)throw new Error('Catalog failed');return response.json();}
    const first=await query('/api/entries');if(first.entries.some(x=>x.path.startsWith('团队/目录-000/')||x.path==='个人/admin'))throw new Error('Private directory leak');
    const catalog=[],picker=[];for(let run=0;run<3;run++){let at=performance.now();await query('/api/entries');catalog.push(performance.now()-at);at=performance.now();await query('/api/sync-folders');picker.push(performance.now()-at);}
    const signature=createHash('sha256').update(JSON.stringify(first.entries.map(x=>({path:x.path,access:x.access})).sort((a,b)=>a.path<b.path?-1:a.path>b.path?1:0))).digest('hex');
    const median=values=>[...values].sort((a,b)=>a-b)[1];const result={label,files:3000,policies:state.Policies.length,visibleEntries:first.entries.length,catalogMilliseconds:catalog,catalogMedian:median(catalog),folderPickerMilliseconds:picker,folderPickerMedian:median(picker),permissionSignature:signature};results.push(result);console.log(JSON.stringify(result));
  }finally{if(server.exitCode===null){server.kill();await new Promise(resolve=>server.once('exit',resolve));}await writeFile(path.join(base,'server.log'),log);}
}
if(results[0].permissionSignature!==results[1].permissionSignature)throw new Error('Permission-filtered catalogs differ');
await writeFile(path.join(root,'comparison.json'),JSON.stringify({results,permissionsMatch:true,conditions:'Synthetic catalog only. 3000 metadata files, 100 additional folder policies; normal device user; three warm samples; native Windows server; no file payload transfer.'},null,2));console.log('Comparison: '+root);
