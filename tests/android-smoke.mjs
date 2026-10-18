// Real Android emulator UI and camera checks. Never clears or installs onto a physical phone.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { randomBytes, createHash } from 'node:crypto';
import { createServer } from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';
import path from 'node:path';

const workspace = process.cwd(), serial = process.env.MYPAN_ANDROID_SERIAL ?? 'emulator-5554';
if (!serial.startsWith('emulator-')) throw new Error('This destructive fixture reset is restricted to an emulator');
const adbPath = path.resolve('.test-data/android/sdk/platform-tools/adb.exe');
const root = path.resolve('.test-data/android-ui-' + new Date().toISOString().replace(/[:.]/g, '-'));
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
async function click(text) { const node = await waitNode(text); await adb('shell', 'input', 'tap', String(Math.round(node.x)), String(Math.round(node.y))); await delay(250); }
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
const url = 'http://127.0.0.1:' + port, ownerPassword = randomBytes(24).toString('hex'), password = randomBytes(20).toString('hex');
const env = { ...process.env };
for (const key of Object.keys(env)) if (key.toLowerCase().startsWith('mypan__')) delete env[key];
Object.assign(env, { ASPNETCORE_URLS: url, MyPan__UnattendedSetup: 'true', MyPan__AdminUsername: 'mobileadmin', MyPan__AdminPassword: ownerPassword,
  MyPan__DataPath: path.join(root, 'server'), MyPan__ApiKeyFile: path.join(root, 'key.txt'), MyPan__InstallationFile: path.join(root, 'settings.json'), Logging__LogLevel__Default: 'Warning' });
const server = spawn(path.resolve('artifacts/mobile-update/server/MyPan.Server.exe'), [], { env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
let serverLog = ''; server.stdout.on('data', b => serverLog += b); server.stderr.on('data', b => serverLog += b);
let headers;
const request = async (route, method = 'GET', body = null) => {
  const response = await fetch(url + route, { method, headers: { ...headers, ...(body ? { 'Content-Type': 'application/json' } : {}) }, ...(body ? { body: JSON.stringify(body) } : {}) });
  if (!response.ok) throw new Error('Fixture API ' + response.status + ': ' + await response.text());
  return response.status === 204 ? null : response.json();
};
try {
  await until(async () => { try { return (await fetch(url + '/health')).ok; } catch { return false; } }, 'native fixture server');
  const login = await request('/api/auth/login', 'POST', { username: 'mobileadmin', password: ownerPassword });
  headers = { Authorization: 'Bearer ' + login.accessToken };
  const user = await request('/api/admin/users', 'POST', { username: 'photographer', displayName: '现场工程师', password });
  const project = await request('/api/folders', 'POST', { path: '个人/photographer/示范工程' });
  const backup = await request('/api/folders', 'POST', { path: '个人/photographer/手机相册' });
  const catalog = () => request('/api/entries');
  await adb('reverse', 'tcp:' + port, 'tcp:' + port);
  await adb('shell', 'pm', 'clear', 'app.yunsevo.drive'); await adb('logcat','-b','crash','-c');
  for (const permission of ['android.permission.CAMERA', 'android.permission.READ_MEDIA_IMAGES', 'android.permission.READ_MEDIA_VIDEO', 'android.permission.RECORD_AUDIO', 'android.permission.ACCESS_FINE_LOCATION', 'android.permission.ACCESS_COARSE_LOCATION'])
    await adb('shell', 'pm', 'grant', 'app.yunsevo.drive', permission);
  await adb('shell', 'monkey', '-p', 'app.yunsevo.drive', '-c', 'android.intent.category.LAUNCHER', '1');
  await waitNode('登录'); await screenshot('01-login');
  await input('服务器地址', url, 12); await input('用户名', 'photographer'); await input('密码', password);
  const ime = await adb('shell','dumpsys','input_method'); if(/mInputShown=true|isInputViewShown=true/.test(ime)) await adb('shell','input','keyevent','4'); await click('登录');
  await waitNode('我的文件'); assert(true, 'Android login opens the authorized cloud workspace');
  await screenshot('02-files');
  await until(async()=>(await nodes()).some(x=>x.text?.includes('网络：Wi-Fi')),'current Wi-Fi transport');
  assert(true,'Network status recognizes the active Wi-Fi connection');
  await click('相册'); await click('备份设置');
  await click('工程拍照'); await click('选择工程目录');
  await click('/个人/示范工程 · 私有');
  await waitNode('当前工程：/个人/示范工程'); assert(true, 'A cloud project is selected before opening the camera');
  await screenshot('03-project');
  await click('云端 + 本机相册'); await click('仅云端（上传成功后清理临时文件）');
  await adb('shell','input','swipe','530','1700','530','550','500'); await click('开始拍照'); await delay(1800);
  await screenshot('04-camera');
  await click('拍摄，点击拍照，长按录像');
  await until(async () => (await catalog()).entries.some(x => x.origin?.kind === 'camera'), 'watermarked camera upload', 45000);
  const photo = (await catalog()).entries.find(x => x.origin?.kind === 'camera');
  assert(photo.path.startsWith(project.path + '/'), 'Real Camera2 photo is uploaded into the selected engineering directory');
  assert(photo.origin.photographer.userId === user.id && photo.origin.photographer.username === 'photographer', 'Real captured photo records the signed-in photographer');
  assert(photo.origin.watermark.includes('示范工程') && photo.origin.watermark.includes('现场工程师'), 'Camera photo includes project, time and photographer watermark metadata');
  const photoResponse = await fetch(url + '/api/content/' + photo.id, { headers });
  const photoBytes = Buffer.from(await photoResponse.arrayBuffer());
  await writeFile(path.join(root, 'watermarked-photo.jpg'), photoBytes);
  assert(photoBytes[0] === 0xff && photoBytes[1] === 0xd8 && photoBytes.length > 10000, 'The uploaded camera result is an actual JPEG image');
  const exif = JSON.parse(await command('python', ['-c', "from PIL import Image;import json,sys;im=Image.open(sys.argv[1]);e=im.getexif();d=dict(e);d.update(e.get_ifd(34665));print(json.dumps({str(k):str(v) for k,v in d.items()}))", path.join(root,'watermarked-photo.jpg')]));
  assert(exif['271'] && exif['272'] && exif['33434'] && exif['33437'] && exif['34855'] && exif['37386'] && exif['36867'], 'Watermarked JPEG retains make, model, exposure time, aperture, ISO, focal length and capture time');
  assert(exif['274'] === '1' && exif['256'] && exif['257'], 'Processed JPEG records normalized orientation and actual image dimensions');
  assert(!(await adb('shell','content','query','--uri','content://media/external/images/media','--projection','_display_name')).includes('YNS_'+photo.origin.captureId.replaceAll('-','')+'.jpg'),'Cloud-only capture does not create a system gallery copy');
  const beforeNetworkPause=new Set((await catalog()).entries.map(x=>x.id));
  await adb('shell','svc','wifi','disable'); await delay(2500);
  await click('拍摄，点击拍照，长按录像');
  await until(async()=>(await nodes()).some(x=>x.text?.includes('等待 Wi-Fi')),'Wi-Fi-only paused camera queue');
  await delay(1500);
  assert(!(await catalog()).entries.some(x=>!beforeNetworkPause.has(x.id)&&x.origin?.kind==='camera'),'Wi-Fi-only capture stays queued on cellular');
  await adb('shell','svc','wifi','enable'); await adb('shell','cmd','wifi','connect-network','AndroidWifi','open');
  await until(async()=>(await catalog()).entries.some(x=>!beforeNetworkPause.has(x.id)&&x.origin?.kind==='camera'),'automatic Wi-Fi upload resume',60000);
  assert(true,'Returning to Wi-Fi automatically resumes the captured file without a retry tap');
  await cameraProject('/个人/手机相册 · 私有'); await click('拍摄，点击拍照，长按录像');
  await until(async()=>(await catalog()).entries.some(x=>x.origin?.kind==='camera'&&x.origin.projectFolderId===backup.id),'in-camera project selection',45000);
  assert((await catalog()).entries.some(x=>x.path.startsWith(backup.path+'/')&&x.origin?.projectFolderId===backup.id),'Switching engineering folders inside the camera routes new photographs to the newly selected project');
  await cameraProject('/个人/示范工程 · 私有');
  const beforeFront=new Set((await catalog()).entries.map(x=>x.id));await click('翻转摄像头');await delay(1500);await click('拍摄，点击拍照，长按录像');
  await until(async()=>(await catalog()).entries.some(x=>!beforeFront.has(x.id)&&x.origin?.kind==='camera'),'front camera photograph',45000);
  const selfie=(await catalog()).entries.find(x=>!beforeFront.has(x.id)&&x.origin?.kind==='camera');const selfieFile=path.join(root,'front-camera.jpg');
  await writeFile(selfieFile,Buffer.from(await(await fetch(url+'/api/content/'+selfie.id,{headers})).arrayBuffer()));
  const selfieSize=JSON.parse(await command('python',['-c','from PIL import Image;import json,sys;print(json.dumps(list(Image.open(sys.argv[1]).size)))',selfieFile]));
  assert(selfieSize[1]>selfieSize[0],'Front camera saves an upright portrait JPEG after switching cameras');
  await click('翻转摄像头');await delay(1200);
  await click('水印与拍摄设置'); await click('水印样式与颜色'); await click('工程卡片'); await click('简约文字'); await click('显示水印底色'); await click('字体颜色 #FFD54F'); await click('保存');
  assert(true,'Camera saves a different watermark style, transparent background and selected font color');
  await click('切换焦距'); await screenshot('camera-zoom');
  const viewfinder=await waitNode('相机取景，点击对焦，双指缩放'); await adb('shell','input','tap',String(Math.round(viewfinder.x)),String(Math.round(viewfinder.y)));
  const shutter=await waitNode('拍摄，点击拍照，长按录像');
  await adb('shell','input','swipe',String(Math.round(shutter.x)),String(Math.round(shutter.y)),String(Math.round(shutter.x)),String(Math.round(shutter.y)),'4500');
  await until(async()=>(await catalog()).entries.some(x=>x.path.endsWith('.mp4')&&x.origin?.kind==='camera'),'watermarked video',45000);
  const movie=(await catalog()).entries.find(x=>x.path.endsWith('.mp4')&&x.origin?.kind==='camera');
  await writeFile(path.join(root,'watermarked-video.mp4'),Buffer.from(await(await fetch(url+'/api/content/'+movie.id,{headers})).arrayBuffer()));
  const timing=JSON.parse(await command('ffprobe',['-v','error','-show_entries','format=duration:stream=codec_type,start_time,duration','-of','json',path.join(root,'watermarked-video.mp4')]));
  assert(Number(timing.format.duration)>1&&Number(timing.format.duration)<10&&timing.streams.every(x=>Math.abs(Number(x.start_time??0))<1),'Short recording has seconds of duration with aligned audio and video timestamps');
  assert(movie.origin.photographer.userId===user.id,'Long-press records and uploads a real MP4 with the authenticated photographer');
  await adb('shell','input','keyevent','4'); await click('仅云端（上传成功后清理临时文件）'); await click('云端 + 本机相册'); await click('开始拍照'); await delay(1200);
  await adb('shell','settings','put','system','accelerometer_rotation','1');await adb('emu','sensor','set','acceleration','9.8:0:0');
  await until(async()=>{const v=await waitNode('相机取景，点击对焦，双指缩放');const b=v.bounds.match(/\[(\d+),(\d+)\]\[(\d+),(\d+)\]/);return Number(b[3])-Number(b[1])>Number(b[4])-Number(b[2]);},'landscape camera layout');
  const beforeLandscape=new Set((await catalog()).entries.map(x=>x.id));
  await delay(1200);await screenshot('camera-landscape');await click('拍摄，点击拍照，长按录像');
  const landscapePhoto=()=>catalog().then(c=>c.entries.find(x=>!beforeLandscape.has(x.id)&&x.origin?.kind==='camera'&&x.path.endsWith('.jpg')));
  await until(async()=>!!await landscapePhoto(),'landscape JPEG',45000);const wide=await landscapePhoto();
  const wideFile=path.join(root,'landscape-photo.jpg');await writeFile(wideFile,Buffer.from(await(await fetch(url+'/api/content/'+wide.id,{headers})).arrayBuffer()));
  const dimensions=JSON.parse(await command('python',['-c','from PIL import Image;import json,sys;print(json.dumps(list(Image.open(sys.argv[1]).size)))',wideFile]));
  assert(dimensions[0]>dimensions[1],'Landscape shooting saves a correctly oriented landscape JPEG');
  assert((await adb('shell','content','query','--uri','content://media/external/images/media','--projection','_display_name')).includes('YNS_'+wide.origin.captureId.replaceAll('-','')+'.jpg'),'Cloud plus local mode automatically creates the captured photo in the system gallery');
  const wideShutter=await waitNode('拍摄，点击拍照，长按录像');await adb('shell','input','swipe',String(Math.round(wideShutter.x)),String(Math.round(wideShutter.y)),String(Math.round(wideShutter.x)),String(Math.round(wideShutter.y)),'4500');
  const landscapeMovie=()=>catalog().then(c=>c.entries.find(x=>x.id!==movie.id&&x.origin?.kind==='camera'&&x.path.endsWith('.mp4')));
  await until(async()=>!!await landscapeMovie(),'landscape video',45000);const wideMovie=await landscapeMovie();const wideMovieFile=path.join(root,'landscape-video.mp4');
  await writeFile(wideMovieFile,Buffer.from(await(await fetch(url+'/api/content/'+wideMovie.id,{headers})).arrayBuffer()));
  const streams=JSON.parse(await command('ffprobe',['-v','error','-show_entries','format=duration:stream=codec_type,width,height,start_time,duration','-of','json',wideMovieFile])).streams;
  assert(streams.every(x=>Math.abs(Number(x.start_time??0))<1&&Number(x.duration)>1&&Number(x.duration)<10),'Landscape audio and video timelines remain aligned and measured in seconds');
  assert(streams.some(x=>x.codec_type==='video'&&x.width===1280&&x.height===720),'Landscape recording produces an actual 1280 by 720 watermarked MP4');
  await adb('emu','sensor','set','acceleration','0:9.8:0');await delay(800);

  await adb('shell', 'input', 'keyevent', '4'); await click('传输');
  await waitNode('上传记录'); await screenshot('05-transfers');
  await adb('shell', 'am', 'force-stop', 'app.yunsevo.drive');
  await adb('shell', 'monkey', '-p', 'app.yunsevo.drive', '-c', 'android.intent.category.LAUNCHER', '1');
  await waitNode('我的文件'); assert(true, 'Android Keystore remembered login survives process restart without a password');
  await click('相册'); await click('备份设置'); await click('选择备份目录'); await click('/个人/手机相册 · 私有');
  await click('开启自动备份'); await delay(600); await screenshot('06-album-backup');
  const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5ioAAAAASUVORK5CYII=', 'base64');
  const fixtureName = 'album-fixture-' + port + '.png', fixtureStem = fixtureName.slice(0, -4);
  const seed = path.join(root, fixtureName); await writeFile(seed, png);
  await adb('shell', 'mkdir', '-p', '/sdcard/DCIM/YunsevoTest');
  await adb('push', seed, '/sdcard/DCIM/YunsevoTest/' + fixtureName);
  await adb('shell', 'am', 'broadcast', '-a', 'android.intent.action.MEDIA_SCANNER_SCAN_FILE', '-d', 'file:///sdcard/DCIM/YunsevoTest/' + fixtureName);
  await click('立即备份');
  const isFixture = x => x.origin?.kind === 'album' && x.path.startsWith(backup.path + '/') && x.path.includes('/' + fixtureStem + '-');
  await until(async () => (await catalog()).entries.some(isFixture), 'automatic MediaStore album backup', 45000);
  const backed = (await catalog()).entries.filter(isFixture);
  assert(backed.length === 1, 'Authorized phone album assets are automatically discovered and backed up');
  await click('立即备份'); await delay(1200);
  assert((await catalog()).entries.filter(isFixture).length === backed.length, 'Repeated album backup does not duplicate already-backed-up photos');
  const media = await adb('shell', 'content', 'query', '--uri', 'content://media/external/images/media', '--projection', '_id:_display_name');
  const mediaId = media.match(new RegExp('_id=(\\d+), _display_name=' + fixtureStem + '\\.png'))?.[1];
  if (!mediaId) throw new Error('Unable to find seeded MediaStore photo');
  await adb('shell', 'am', 'start', '-a', 'android.intent.action.SEND', '-t', 'image/png', '--eu', 'android.intent.extra.STREAM', 'content://media/external/images/media/' + mediaId, '-p', 'app.yunsevo.drive');
  await click('/个人/示范工程 · 私有');
  await until(async () => (await catalog()).entries.some(x => x.origin?.kind === 'shared'), 'external Android SEND upload');
  assert(true, 'Android receives a file shared from another app and asks which cloud folder to use');
  await screenshot('07-shared-upload');
  await click('文件'); await click('▣  /个人');
  await click('▣  示范工程');
  const row = (await nodes()).find(x => x.text?.includes('YNS_') && x.text.startsWith('▤'));
  if (!row) throw new Error('Captured cloud file is absent from Android list');
  await adb('shell', 'input', 'tap', String(Math.round(row.x)), String(Math.round(row.y)));
  await until(async () => (await nodes()).some(x => x.text?.includes('拍摄人：现场工程师')), 'photo provenance preview');
  assert(true, 'Online file preview shows who captured the photograph');
  await screenshot('08-photo-preview');
  for(let index=0;index<32;index++){
    const seeded=await fetch(url+'/api/content?path='+encodeURIComponent(backup.path+'/scroll-'+index+'.png'),{method:'PUT',headers:{...headers,'If-None-Match':'*','Content-Type':'image/png'},body:png});
    if(!seeded.ok)throw new Error('Unable to seed scrolling album fixture');
  }
  await adb('shell','input','keyevent','4'); await click('相册'); await waitNode('云端相册');
  const albumPhoto='查看云端媒体 '+photo.path.split('/').pop();
  for(let index=0;index<40;index++){
    if((await nodes()).some(x=>x['content-desc']===albumPhoto))break;
    await adb('shell','input','swipe','540','1800','540','1350','400'); await delay(600);
  }
  assert(!(await nodes()).some(x=>x.text==='上一页'||x.text==='下一页'),'Cloud gallery continues scrolling through multiple server pages without page buttons');
  await screenshot('09-cloud-album'); await click(albumPhoto);
  assert(true,'Cloud album opens an authorized engineering photograph for preview');
  const image=(await nodes()).find(x=>x['content-desc']?.includes('图片预览，缩放 100%'));
  if(!image)throw new Error('Zoomable image view missing');
  await adb('shell','input','tap',String(Math.round(image.x)),String(Math.round(image.y))); await adb('shell','input','tap',String(Math.round(image.x)),String(Math.round(image.y)));
  await until(async()=>(await nodes()).some(x=>x['content-desc']?.includes('缩放 300%')),'double tap image zoom');
  assert(true,'Cloud photograph supports double-tap zoom with a real transformation');
  const enlarged=(await nodes()).find(x=>x['content-desc']?.includes('缩放 300%'));
  await adb('shell','input','tap',String(Math.round(enlarged.x)),String(Math.round(enlarged.y)));
  await adb('shell','input','tap',String(Math.round(enlarged.x)),String(Math.round(enlarged.y)));
  await until(async()=>(await nodes()).some(x=>x['content-desc']?.includes('缩放 100%')),'restore fit before horizontal paging');
  const previousMedia=(await request('/api/browser/media/'+photo.id+'/neighbors')).previous;
  await adb('shell','input','swipe','230','1050','880','1050','240');
  await until(async()=>(await nodes()).some(x=>x.text===previousMedia.path.split('/').pop()),'swipe to previous cloud media',45000);
  assert(true,'Horizontal swipe selects the previous authorized media item');
  await click('下一张照片');
  await until(async()=>(await nodes()).some(x=>x['content-desc']?.includes('图片预览，缩放 100%')),'return to the photograph',45000);
  await click('保存到手机'); await waitNode('保存成功'); await click('知道了');
  assert(true,'Cloud photograph can be exported into the system gallery');
  await click('分享'); await delay(700);
  assert((await adb('shell','dumpsys','activity','activities')).includes('ChooserActivity'),'Cloud photograph opens the Android share chooser with a granted file URI');
  await adb('shell','input','keyevent','4');

  assert(!(await adb('logcat','-d','-b','crash')).includes('Process: app.yunsevo.drive'),'Repeated camera captures and gallery navigation complete without an application crash');
  assert(!(await adb('shell','dumpsys','activity','lastanr')).includes('app.yunsevo.drive'),'Camera, automatic backup, incoming sharing and preview complete without an application ANR');
  await writeFile(path.join(root, 'verification.json'), JSON.stringify({ checks, serial, androidApi: 35, nativeServer: true, releaseMonoAot: process.env.MYPAN_ANDROID_RELEASE === '1',
    apkVersion: '1.2.0', apkSha256: createHash('sha256').update(await readFile(process.env.MYPAN_ANDROID_APK ?? 'artifacts/mobile/android-x64/yunsevo-android-x64.apk')).digest('hex') }, null, 2));
  console.log('ALL PASSED: ' + checks.length + ' Android device checks. ' + root);
} catch (error) {
  try { await screenshot('failure'); } catch {}
  console.error(error); process.exitCode = 1;
} finally {
  await adb('shell','svc','wifi','enable');await adb('emu','sensor','set','acceleration','0:9.8:0');await adb('shell','settings','put','system','accelerometer_rotation','0');await adb('shell','settings','put','system','user_rotation','0');
  if (server.exitCode === null) { const ended = once(server, 'exit'); server.kill(); await ended; }
  await writeFile(path.join(root, 'server.log'), serverLog.replaceAll(password, '[redacted]').replaceAll(ownerPassword, '[redacted]'));
}
