// Isolated native server + local Photon fixture; no provider credentials or external network required.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createServer } from 'node:http';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';
import path from 'node:path';

const root=path.resolve('.test-data/environment-'+new Date().toISOString().replace(/[:.]/g,'-'));
await mkdir(root,{recursive:true});
let count=0, failed=false, agent='', target='';
const photon=createServer((req,res)=>{count++;agent=req.headers['user-agent'];target=req.url;res.setHeader('Content-Type','application/json');
  if(failed){res.writeHead(503);res.end('{}');return;}
  res.end(JSON.stringify({features:[{properties:{country:'中国',city:'测试市',street:'现场路',housenumber:'8号'}}]}));});
await new Promise(r=>photon.listen(0,'127.0.0.1',r));
const allocator=createServer();await new Promise(r=>allocator.listen(0,'127.0.0.1',r));const port=allocator.address().port;await new Promise(r=>allocator.close(r));
const url='http://127.0.0.1:'+port, key=randomBytes(32).toString('hex'), password=randomBytes(24).toString('hex');
const env={...process.env};for(const name of Object.keys(env))if(name.toLowerCase().startsWith('mypan__'))delete env[name];
Object.assign(env,{ASPNETCORE_URLS:url,MyPan__UnattendedSetup:'true',MyPan__AdminUsername:'owner',MyPan__AdminPassword:password,
  MyPan__ApiKey:key,MyPan__ApiKeyFile:path.join(root,'api-key.txt'),MyPan__DataPath:path.join(root,'data'),MyPan__InstallationFile:path.join(root,'installation.json'),Logging__LogLevel__Default:'Warning'});
const server=spawn(path.resolve('artifacts/mobile-update/server/MyPan.Server.exe'),[],{env,windowsHide:true,stdio:['ignore','pipe','pipe']});
let log='';server.stdout.on('data',b=>log+=b);server.stderr.on('data',b=>log+=b);
const checks=[], assert=(value,label)=>{if(!value)throw new Error(label);checks.push(label);console.log('PASS: '+label);};
const request=(route,method='GET',body)=>fetch(url+route,{method,headers:{Authorization:'Bearer '+key,...(body?{'Content-Type':'application/json'}:{})},body:body?JSON.stringify(body):undefined});
async function json(route,method,body){const r=await request(route,method,body);if(!r.ok)throw new Error('Fixture HTTP '+r.status);return r.json();}
try{
  for(let i=0;i<100;i++){try{if((await fetch(url+'/health')).ok)break;}catch{}await delay(100);}
  assert((await fetch(url+'/api/mobile/environment',{method:'POST',headers:{'Content-Type':'application/json'},body:'{"latitude":39,"longitude":116}'})).status===401,'Environment lookup requires authentication');
  const config={enabled:true,addressProvider:'photon',weatherProvider:'disabled',photonServer:'http://127.0.0.1:'+photon.address().port,contact:'https://github.com/hnlyf168',cacheMinutes:10};
  await json('/api/admin/environment-settings','PUT',config);
  const first=await json('/api/mobile/environment','POST',{latitude:39.912345,longitude:116.412345});
  assert(first.address==='中国 测试市 现场路 8号'&&first.addressSource.includes('Photon'),'Reverse geocoding returns the configured provider address and attribution');
  assert(first.weather===null&&first.weatherError.includes('关闭'),'Disabled weather is explicitly unavailable');
  await json('/api/mobile/environment','POST',{latitude:39.912345,longitude:116.412345});
  assert(count===1,'Repeated location lookup reuses the bounded server cache');
  assert(agent.includes('Yunsevo')&&agent.includes('https://github.com/hnlyf168')&&target.includes('lat=39.9123'),'Upstream requests identify the application and round coordinates');
  failed=true;await json('/api/admin/environment-settings','PUT',config);
  const unavailable=await json('/api/mobile/environment','POST',{latitude:39.913,longitude:116.413});
  assert(unavailable.address===null&&unavailable.addressError.includes('503'),'Provider errors produce an explicit unavailable result without fabricated address data');
  assert((await request('/api/mobile/environment','POST',{latitude:91,longitude:116})).status===400,'Out-of-range coordinates are rejected before upstream calls');
  await writeFile(path.join(root,'verification.json'),JSON.stringify({checks,fixture:root},null,2));
  console.log('ALL PASSED: '+checks.length+' environment checks. '+root);
}catch(e){console.error(e);process.exitCode=1;}
finally{if(server.exitCode===null){const end=once(server,'exit');server.kill();await end;}photon.close();await writeFile(path.join(root,'server.log'),log.replaceAll(key,'[redacted]').replaceAll(password,'[redacted]'));}
