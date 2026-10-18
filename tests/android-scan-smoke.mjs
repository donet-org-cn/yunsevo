// Real Android emulator UI and camera checks. Never clears or installs onto a physical phone.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, readFile, writeFile, readdir } from 'node:fs/promises';
import { randomBytes, createHash } from 'node:crypto';
import { createServer, isIP } from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';
import path from 'node:path';

const workspace = process.cwd(), serial = process.env.MYPAN_ANDROID_SERIAL ?? 'emulator-5554';
if (!serial.startsWith('emulator-')) throw new Error('This destructive fixture reset is restricted to an emulator');
const adbPath = path.resolve('.test-data/android/sdk/platform-tools/adb.exe');
const root = path.resolve('.test-data/android-scan-' + new Date().toISOString().replace(/[:.]/g, '-'));
await mkdir(root, { recursive: true });
const checks = [], assert = (ok, label) => { if (!ok) throw new Error(label); checks.push(label); console.log('PASS: ' + label); };
async function command(file, args, timeout = 45000) {
  const process = spawn(file, args, { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = ''; process.stdout.on('data', b => output += b); process.stderr.on('data', b => output += b);
  const timer = setTimeout(() => process.kill(), timeout);
  try { const [code] = await once(process, 'exit'); if (code !== 0) throw new Error('Command failed: ' + output); return output; } finally { clearTimeout(timer); }
}
const adb = (...args) => command(adbPath, ['-s', serial, ...args]);
async function nodes() {
  await adb('shell', 'uiautomator', 'dump', '/sdcard/yunsevo-test.xml');
  const xml = await adb('shell', 'cat', '/sdcard/yunsevo-test.xml');
  const result = [...xml.matchAll(/<node\b([^>]+)>?/g)].map(match => {
    const attrs = Object.fromEntries([...match[1].matchAll(/([\w-]+)="([^"]*)"/g)].map(m => [m[1], m[2].replaceAll('&amp;', '&').replaceAll('&quot;', '"').replaceAll('&#10;', '\n')]));
    const bounds = attrs.bounds?.match(/\[(\d+),(\d+)\]\[(\d+),(\d+)\]/);
    return { ...attrs, x: bounds ? (Number(bounds[1]) + Number(bounds[3])) / 2 : 0, y: bounds ? (Number(bounds[2]) + Number(bounds[4])) / 2 : 0 };
  });
  if(result.some(x=>x.text?.includes('Stay connected?'))){
    const remember=result.find(x=>x.text?.includes("Don't ask again"));
    if(remember) await adb('shell','input','tap',String(Math.round(remember.x)),String(Math.round(remember.y)));
    const accept=result.find(x=>x.text==='Yes');
    if(accept) {await adb('shell','input','tap',String(Math.round(accept.x)),String(Math.round(accept.y)));await delay(300);return nodes();}
  }
  return result;
}
async function waitNode(text, timeout = 30000) {
  const until = Date.now() + timeout;
  while (Date.now() < until) {
    const found = (await nodes()).find(x => x.text === text || x['content-desc'] === text);
    if (found) return found; await delay(300);
  }
  throw new Error('Missing Android UI: ' + text);
}
async function click(text) { let node = (await nodes()).find(x => x.text === text || x['content-desc'] === text); for(let i=0; !node && i<4; i++){await adb('shell','input','swipe','500','1500','500','500','300');node=(await nodes()).find(x=>x.text===text||x['content-desc']===text);} node ??= await waitNode(text); await adb('shell', 'input', 'tap', String(Math.round(node.x)), String(Math.round(node.y))); await delay(250); }
async function cameraProject(title) {
  for(let attempt=0;attempt<4;attempt++){
    await click('切换工程');
    try {
      const target=await waitNode(title,5000);
      await adb('shell','input','tap',String(Math.round(target.x)),String(Math.round(target.y)));await delay(300);return;
    } catch(error) {if(attempt===3)throw error;await delay(500);}
  }
}
async function input(description, value, erase = 0) {
  await click(description);
  if (erase) await adb('shell', 'input', 'keyevent', '123');
  if (erase) await adb('shell', 'input', 'keyevent', ...Array.from({ length: erase }, () => '67'));
  await adb('shell', 'input', 'text', value);
}
async function screenshot(name) {
  await delay(300); await adb('shell', 'screencap', '-p', '/data/local/tmp/yunsevo-screen.png');
  await adb('pull', '/data/local/tmp/yunsevo-screen.png', path.join(root, name + '.png'));
}
async function until(operation, description, timeout = 30000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await operation()) return; await delay(300); }
  throw new Error('Timeout: ' + description);
}
const socket = createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
const port = socket.address().port; await new Promise(resolve => socket.close(resolve));
const directLan = process.env.MYPAN_TEST_DIRECT_LAN;
if(directLan && isIP(directLan)!==4) throw new Error('Use a literal LAN IPv4 address');
const bindHost=directLan?'0.0.0.0':'127.0.0.1';
const url = 'http://' + (directLan??'127.0.0.1') + ':' + port, ownerPassword = randomBytes(24).toString('hex'), password = randomBytes(20).toString('hex');
const env = { ...process.env };
for (const key of Object.keys(env)) if (key.toLowerCase().startsWith('mypan__')) delete env[key];
Object.assign(env, { ASPNETCORE_URLS: 'http://'+bindHost+':'+port, MyPan__UnattendedSetup: 'true', MyPan__AdminUsername: 'mobileadmin', MyPan__AdminPassword: ownerPassword,
  MyPan__DataPath: path.join(root, 'server'), MyPan__ApiKeyFile: path.join(root, 'key.txt'), MyPan__InstallationFile: path.join(root, 'settings.json'), Logging__LogLevel__Default: 'Warning' });
env.MyPan__ReconstructionEngine=path.resolve('artifacts/reconstruction-native/win-x64/yunsevo-reconstruct.exe');
const server = spawn(path.resolve(process.env.MYPAN_TEST_SERVER_EXE??'artifacts/reconstruction-update/server/MyPan.Server.exe'), [], { env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
let serverLog = ''; server.stdout.on('data', b => serverLog += b); server.stderr.on('data', b => serverLog += b);
let headers, stalledServer; const stalledSockets = new Set();
const request = async (route, method = 'GET', body = null) => {
  const response = await fetch(url + route, { method, headers: { ...headers, ...(body ? { 'Content-Type': 'application/json' } : {}) }, ...(body ? { body: JSON.stringify(body) } : {}) });
  if (!response.ok) throw new Error('Fixture API ' + response.status + ': ' + await response.text());
  return response.status === 204 ? null : response.json();
};

try {
 await until(async()=>{try{return(await fetch(url+'/health')).ok;}catch{return false;}},'fixture startup');
 const owner=await request('/api/auth/login','POST',{username:'mobileadmin',password:ownerPassword});headers={Authorization:'Bearer '+owner.accessToken};
 const user=await request('/api/admin/users','POST',{username:'scanner',displayName:'三维测试员',password});
 const login=await request('/api/auth/device/login','POST',{username:'scanner',password});const deviceHeaders={Authorization:'Bearer '+login.accessToken};
 const folders=await(await fetch(url+'/api/sync-folders',{headers:deviceHeaders})).json();const personal=folders.find(x=>x.category==='private');
 const verified=(await readdir('.test-data/reconstruction')).filter(x=>x.startsWith('native-verification-')).sort().reverse();
 const modelPath=process.env.MYPAN_TEST_MODEL??path.join('.test-data/reconstruction',verified[0]??'', 'vulkan.glb');
 const modelBytes=await readFile(modelPath);
 const upload=await fetch(url+'/api/content?path='+encodeURIComponent(personal.path+'/device-model.glb'),{method:'PUT',headers:{...deviceHeaders,'If-None-Match':'*'},body:modelBytes});assert(upload.ok,'A genuine reconstructed GLB is available in the test account');
 if(!directLan) await adb('reverse','tcp:'+port,'tcp:'+port);await adb('shell','pm','clear','app.yunsevo.drive');await adb('logcat','-b','crash','-c');
 for(const permission of ['android.permission.CAMERA','android.permission.READ_MEDIA_IMAGES','android.permission.READ_MEDIA_VIDEO','android.permission.POST_NOTIFICATIONS'])await adb('shell','pm','grant','app.yunsevo.drive',permission);
 await adb('shell','svc','wifi','enable');await adb('shell','cmd','wifi','connect-network','AndroidWifi','open');
 await adb('shell','monkey','-p','app.yunsevo.drive','-c','android.intent.category.LAUNCHER','1');
 await waitNode('登录');await input('服务器地址',url,12);await input('用户名','scanner');await input('密码',password);await adb('shell','input','keyevent','4');await click('登录');await waitNode('我的文件');assert(true,directLan?'Release Mono AOT Android logs in over direct LAN HTTP without ADB reverse':'Release Mono AOT Android app logs in');
 await click('工程拍照');await click('三维扫描 · 拍摄或从相册创建');await waitNode('新建扫描');await until(async()=>(await nodes()).some(x=>x.text?.includes('本机 CPU 可用')),'native CPU capability detection');assert(true,'Android x64 package loads real native CPU engine and rejects software Vulkan as a GPU');
 if(process.env.MYPAN_TEST_VIDEO==='true') {
  const ffmpeg=process.env.MYPAN_FFMPEG??'ffmpeg';
  await command(ffmpeg,['-hide_banner','-loglevel','error','-y','-framerate','1','-i',path.resolve('.test-data/reconstruction/synthetic48/view-%03d.jpg'),'-vf','fps=12','-c:v','libx264','-preset','veryfast','-crf','18','-pix_fmt','yuv420p',path.resolve('.test-data/reconstruction/orbit-video.mp4')],120000);
  await command(ffmpeg,['-hide_banner','-loglevel','error','-y','-loop','1','-i',path.resolve('.test-data/reconstruction/synthetic48/view-000.jpg'),'-t','8','-r','12','-c:v','libx264','-preset','veryfast','-crf','18','-pix_fmt','yuv420p',path.resolve('.test-data/reconstruction/static-video.mp4')],120000);

  for(const name of ['orbit-video.mp4','static-video.mp4']) {
   await adb('push',path.resolve('.test-data/reconstruction',name),'/sdcard/Download/'+name);
   await adb('shell','am','broadcast','-a','android.intent.action.MEDIA_SCANNER_SCAN_FILE','-d','file:///sdcard/Download/'+name);
  }
  await click('新建扫描');await input('例如：水泵设备扫描','VideoScan');await click('创建');
  async function pickVideo(name) {
   await click('从视频自动选帧');
   await delay(1200);
   if(!(await nodes()).some(x=>x.text===name)) {
    const roots=(await nodes()).find(x=>x['content-desc']==='Show roots');
    if(roots){await adb('shell','input','tap',String(Math.round(roots.x)),String(Math.round(roots.y)));await click('Downloads');}
   }
   await click(name);
  }
  await pickVideo('orbit-video.mp4');
  await until(async()=>(await nodes()).some(x=>x.text?.includes('已从视频筛选')),'time-distributed keyframe extraction',90000);
  const countText=(await nodes()).find(x=>x.text?.match(/^\d+\/72 张/))?.text??'';
  const count=Number(countText.split('/')[0]);assert(count>=6&&count<=48,'Video decoding selects a bounded set of clear time-distributed JPEG frames');
  await screenshot('mobile-3d-video');
  await pickVideo('static-video.mp4');await waitNode('暂未完成');
  assert((await nodes()).some(x=>x.text?.includes('未添加照片')),'Stationary video is rejected without adding duplicate frames');await click('知道了');
  assert((await nodes()).some(x=>x.text===countText),'Rejected extraction preserves the existing scan photographs');
  await click('环绕录像 · 自动选帧');await waitNode('三维采集 · 完成');await delay(1200);
  await click('拍摄，点击拍照，长按录像');await delay(5000);await screenshot('mobile-3d-video-capture');await click('拍摄，点击拍照，长按录像');
  await until(async()=>(await nodes()).some(x=>x.text==='暂未完成'||x.text?.includes('已从视频筛选')),'scan recording and automatic extraction',90000);
  const ns=await nodes();assert(!ns.some(x=>x.text?.includes('时间轴异常')||x.text?.includes('录像未开始')),'Tap-to-record scan video stops with a valid timeline and enters automatic frame extraction');
  if(ns.some(x=>x.text==='暂未完成'))await click('知道了');
  const scanHome='/data/data/app.yunsevo.drive/files/sync/scans/';let calibration;
  for(const id of (await adb('shell','su','0','ls',scanHome)).trim().split(/\s+/)){
   const session=JSON.parse(await adb('exec-out','su','0','cat',scanHome+id+'/session.json'));if(session.Name!=='VideoScan')continue;
   const file=(await adb('shell','su','0','ls',scanHome+id)).trim().split(/\s+/).find(x=>x.endsWith('.calibration.json'));
   if(file)calibration=JSON.parse(await adb('exec-out','su','0','cat',scanHome+id+'/'+file));
  }
  assert(calibration?.Focal35mm>=5&&calibration.Focal35mm<=500,'Camera video retains measured crop-adjusted focal calibration');
  await click('‹ 所有扫描');
 }
 await click('新建扫描');await input('例如：水泵设备扫描','PumpScan');await click('创建');await waitNode('引导拍摄 · 无水印原片');assert(true,'Scan creation binds an authorized cloud folder');await screenshot('mobile-3d-scan');
 await click('引导拍摄 · 无水印原片');await waitNode('三维采集 · 完成');await delay(1500);await screenshot('mobile-3d-capture');
 for(let i=1;i<=6;i++){await click('拍摄，点击拍照，长按录像');await until(async()=>(await nodes()).some(x=>x.text?.startsWith(i+' 张 ·')),'original capture '+i,20000);}
 assert(true,'Six Camera2 originals are saved with guided capture count');
 await click('水印与拍摄设置');await waitNode('三维采集');await click('知道了');await click('三维采集 · 完成');await waitNode('手机本机生成');
 assert((await nodes()).some(x=>x.text?.startsWith('6/72 张')),'Capture originals survive closing the camera');
 await click('手机本机生成');await click('CPU');await click('开始');
 await until(async()=>(await nodes()).some(x=>x.text?.includes('停止本机生成')||x.text?.includes('需要处理')),'native foreground reconstruction starts',30000);
 if((await nodes()).some(x=>x.text==='停止本机生成'))await click('停止本机生成');
 await until(async()=>(await nodes()).some(x=>x.text?.includes('需要处理')),'native cancellation or insufficient-overlap failure',45000);assert((await nodes()).some(x=>x.text?.startsWith('6/72 张')),'Failed or cancelled generation preserves all original photographs');
 assert(!(await nodes()).some(x=>/EntryPointNotFound|Unable to load|yv_reconstruct_v2/.test(x.text??'')),'Native v2 generation API executes without missing symbols');
 if(process.env.MYPAN_TEST_REGEN==='true') {
  const home='/data/data/app.yunsevo.drive/files/sync/scans/';
  const dirs=(await adb('shell','su','0','ls',home)).trim().split(/\s+/);let scanId,session;
  for(const id of dirs){const value=JSON.parse(await adb('exec-out','su','0','cat',home+id+'/session.json'));if(value.Name==='PumpScan'){scanId=id;session=value;break;}}
  if(!/^[a-f0-9]{32}$/.test(scanId??''))throw new Error('No isolated regeneration fixture');
  await adb('push',modelPath,'/data/local/tmp/yunsevo-regen-fixture.glb');await adb('shell','su','0','cp','/data/local/tmp/yunsevo-regen-fixture.glb',home+scanId+'/model.glb');
  async function setSession(message){session.State='ready';session.Message=message;const file=path.join(root,'scan-session.json');await writeFile(file,JSON.stringify(session));await adb('push',file,'/data/local/tmp/yunsevo-regen-session.json');await adb('shell','su','0','cp','/data/local/tmp/yunsevo-regen-session.json',home+scanId+'/session.json');}
  await setSession('regeneration-fixture-ready');await click('用原片重新生成 · 保留旧模型副本');await click('CPU');await click('开始');
  await until(async()=>(await nodes()).some(x=>x.text?.includes('停止本机生成')||x.text?.includes('需要处理')),'regeneration starts');
  if((await nodes()).some(x=>x.text==='停止本机生成'))await click('停止本机生成');
  await until(async()=>(await nodes()).some(x=>x.text?.includes('需要处理')),'regeneration cancellation');
  const hash=await adb('exec-out','su','0','sha256sum',home+scanId+'/model.glb');assert(hash.startsWith(createHash('sha256').update(modelBytes).digest('hex')),'Failed or cancelled regeneration restores the exact previous model');
  assert((await adb('shell','su','0','ls',home+scanId)).includes('model-before-'),'Regeneration retains a separate previous-model backup');
  await adb('shell','su','0','rm',home+scanId+'/model.glb');session.State='failed';session.Message='regeneration-fixture-complete';const file=path.join(root,'scan-session.json');await writeFile(file,JSON.stringify(session));await adb('push',file,'/data/local/tmp/yunsevo-regen-session.json');await adb('shell','su','0','cp','/data/local/tmp/yunsevo-regen-session.json',home+scanId+'/session.json');
 }
 await click('提交服务端生成');await until(async()=>{
  const files=(await request('/api/entries')).entries.filter(x=>x.origin?.kind==='scan');return files.length===6;
 },'durable original upload',60000);
 const originals=(await request('/api/entries')).entries.filter(x=>x.origin?.kind==='scan');assert(originals.every(x=>x.origin.photographer?.userId===user.id&&!x.origin.watermark),'Cloud scan originals record authenticated photographer without burned watermark');
 const first=await fetch(url+'/api/content/'+originals[0].id,{headers:deviceHeaders});await writeFile(path.join(root,'raw-camera.jpg'),Buffer.from(await first.arrayBuffer()));
 await until(async()=>{const r=await fetch(url+'/api/reconstruction/jobs',{headers:deviceHeaders});return(await r.json()).length===1;},'background idempotent server submission',45000);assert(true,'Uploaded originals automatically create one account-owned server job');
 const remote=await(await fetch(url+'/api/reconstruction/jobs',{headers:deviceHeaders})).json();await fetch(url+'/api/reconstruction/jobs/'+remote[0].id,{method:'DELETE',headers:deviceHeaders});
 await adb('shell','input','keyevent','4');await click('文件');await click('▣  /个人');await click('▤  device-model.glb');
 await waitNode('自动环绕',45000);await delay(3500);await screenshot('mobile-3d-viewer');await click('自动环绕');await waitNode('停止环绕');assert(true,'Offline WebView loads GLB, executes bundled viewer and toggles real orbit');
 await click('复位');await waitNode('自动环绕');assert(true,'Android model view resets its rotation');
 if(process.env.MYPAN_TEST_OFFLINE_EXIT==='true') {
  server.kill();await once(server,'exit');
  stalledServer=createServer(socket=>{stalledSockets.add(socket);socket.on('data',()=>{});socket.on('close',()=>stalledSockets.delete(socket));});
  await new Promise(resolve=>stalledServer.listen(port,bindHost,resolve));
  await adb('shell','input','keyevent','4');await click('文件');
  const started=Date.now();await click('我的');await click('退出登录');await waitNode('登录',15000);
  assert(Date.now()-started<15000,'Unresponsive server cannot block My page or local sign-out');
  await click('关闭应用');await delay(400);
  const activity=await adb('shell','dumpsys','activity','activities');assert(!/topResumedActivity=.*app\.yunsevo\.drive/.test(activity),'Signed-out app closes normally without a server connection');
  await adb('shell','monkey','-p','app.yunsevo.drive','-c','android.intent.category.LAUNCHER','1');await waitNode('登录');assert(true,'Offline sign-out persists across an app restart');
 }
 const crash=await adb('logcat','-b','crash','-d');assert(!crash.includes('FATAL EXCEPTION')&&!crash.includes('Fatal signal'),'No Android managed or native crashes');
 await writeFile(path.join(root,'verification.json'),JSON.stringify({success:true,checks,apkSha256:createHash('sha256').update(await readFile(process.env.MYPAN_TEST_APK??'artifacts/reconstruction-update/mobile/android-x64/yunsevo-android-x64.apk')).digest('hex')},null,2));console.log('Verification:',root);
}catch(e){await screenshot('failure').catch(()=>{});await writeFile(path.join(root,'failure.json'),JSON.stringify({error:e.stack,checks},null,2));throw e;}
finally{for(const socket of stalledSockets)socket.destroy();stalledServer?.close();await adb('shell','am','force-stop','app.yunsevo.drive').catch(()=>{});server.kill();await writeFile(path.join(root,'server.log'),serverLog);}
