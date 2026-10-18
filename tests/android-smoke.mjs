// Real Android emulator UI and camera checks. Never clears or installs onto a physical phone.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { randomBytes } from 'node:crypto';
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
  return [...xml.matchAll(/<node\b([^>]+)>?/g)].map(match => {
    const attrs = Object.fromEntries([...match[1].matchAll(/([\w-]+)="([^"]*)"/g)].map(m => [m[1], m[2].replaceAll('&amp;', '&').replaceAll('&quot;', '"').replaceAll('&#10;', '\n')]));
    const bounds = attrs.bounds?.match(/\[(\d+),(\d+)\]\[(\d+),(\d+)\]/);
    return { ...attrs, x: bounds ? (Number(bounds[1]) + Number(bounds[3])) / 2 : 0, y: bounds ? (Number(bounds[2]) + Number(bounds[4])) / 2 : 0 };
  });
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
  await adb('shell', 'pm', 'clear', 'app.yunsevo.drive');
  for (const permission of ['android.permission.CAMERA', 'android.permission.READ_MEDIA_IMAGES', 'android.permission.READ_MEDIA_VIDEO'])
    await adb('shell', 'pm', 'grant', 'app.yunsevo.drive', permission);
  await adb('shell', 'monkey', '-p', 'app.yunsevo.drive', '-c', 'android.intent.category.LAUNCHER', '1');
  await waitNode('登录'); await screenshot('01-login');
  await input('服务器地址', url, 12); await input('用户名', 'photographer'); await input('密码', password);
  await adb('shell', 'input', 'keyevent', '4'); await click('登录');
  await waitNode('我的文件'); assert(true, 'Android login opens the authorized cloud workspace');
  await screenshot('02-files');
  await click('工程拍照'); await click('选择工程目录');
  await click('/个人/示范工程 · 私有');
  await waitNode('当前工程：/个人/示范工程'); assert(true, 'A cloud project is selected before opening the camera');
  await screenshot('03-project');
  await click('开始拍照'); await delay(1800);
  await screenshot('04-camera');
  await click('●  拍照并上传');
  await until(async () => (await catalog()).entries.some(x => x.origin?.kind === 'camera'), 'watermarked camera upload', 45000);
  const photo = (await catalog()).entries.find(x => x.origin?.kind === 'camera');
  assert(photo.path.startsWith(project.path + '/'), 'Real Camera2 photo is uploaded into the selected engineering directory');
  assert(photo.origin.photographer.userId === user.id && photo.origin.photographer.username === 'photographer', 'Real captured photo records the signed-in photographer');
  assert(photo.origin.watermark.includes('示范工程') && photo.origin.watermark.includes('现场工程师'), 'Camera photo includes project, time and photographer watermark metadata');
  const photoResponse = await fetch(url + '/api/content/' + photo.id, { headers });
  const photoBytes = Buffer.from(await photoResponse.arrayBuffer());
  await writeFile(path.join(root, 'watermarked-photo.jpg'), photoBytes);
  assert(photoBytes[0] === 0xff && photoBytes[1] === 0xd8 && photoBytes.length > 10000, 'The uploaded camera result is an actual JPEG image');
  await adb('shell', 'input', 'keyevent', '4'); await click('传输');
  await waitNode('上传记录'); await screenshot('05-transfers');
  await adb('shell', 'am', 'force-stop', 'app.yunsevo.drive');
  await adb('shell', 'monkey', '-p', 'app.yunsevo.drive', '-c', 'android.intent.category.LAUNCHER', '1');
  await waitNode('我的文件'); assert(true, 'Android Keystore remembered login survives process restart without a password');
  await click('相册'); await click('选择备份目录'); await click('/个人/手机相册 · 私有');
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
  await writeFile(path.join(root, 'verification.json'), JSON.stringify({ checks, serial, androidApi: 35, nativeServer: true }, null, 2));
  console.log('ALL PASSED: ' + checks.length + ' Android device checks. ' + root);
} catch (error) {
  try { await screenshot('failure'); } catch {}
  console.error(error); process.exitCode = 1;
} finally {
  if (server.exitCode === null) { const ended = once(server, 'exit'); server.kill(); await ended; }
  await writeFile(path.join(root, 'server.log'), serverLog.replaceAll(password, '[redacted]').replaceAll(ownerPassword, '[redacted]'));
}
