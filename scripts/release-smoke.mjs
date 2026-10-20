// Run native executables on their matching host, using isolated test data.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { mkdir, readFile, writeFile, stat, copyFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { randomBytes, createHash } from 'node:crypto';
import { createServer } from 'node:net';
import path from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const [rid, published] = process.argv.slice(2);
if (!/^(win|linux|osx)-(x64|arm64)$/.test(rid ?? '')) throw new Error('Expected target RID and published directory');
const base = path.resolve(published ?? 'artifacts/v2/' + rid);
const root = path.resolve('.test-data', 'release-' + rid + '-' + Date.now());
await mkdir(root, { recursive: true });
const checks = [], extension = rid.startsWith('win-') ? '.exe' : '';
const executable = role => {
  const bundle = path.join(base, 'desktop', 'Yunsevo.app', 'Contents', 'MacOS', 'Yunsevo.Desktop');
  return role === 'Desktop' && rid.startsWith('osx-') && existsSync(bundle) ? bundle : path.join(base, role.toLowerCase(), 'Yunsevo.' + role + extension);
};
const assert = (condition, name) => { if (!condition) throw new Error(name); checks.push(name); console.log('PASS: ' + name); };
for (const role of ['Server', 'Desktop', 'Headless']) {
  const bytes = await readFile(executable(role)), arm = rid.endsWith('arm64');
  if (rid.startsWith('win-')) {
    const pe = bytes.readUInt32LE(0x3c), optional = pe + 24;
    assert(bytes.readUInt32LE(pe) === 0x4550 && bytes.readUInt16LE(pe + 4) === (arm ? 0xaa64 : 0x8664) && bytes.readUInt32LE(optional + 112 + 14 * 8) === 0, role + ' is a native ' + rid + ' PE without a CLR entry');
  } else if (rid.startsWith('linux-')) {
    assert(bytes.subarray(0, 4).toString('hex') === '7f454c46' && bytes.readUInt16LE(18) === (arm ? 183 : 62), role + ' is a matching ' + rid + ' ELF executable');
  } else {
    assert(bytes.readUInt32LE(0) === 0xfeedfacf && bytes.readUInt32LE(4) === (arm ? 0x0100000c : 0x01000007), role + ' is a matching ' + rid + ' Mach-O executable');
  }
}
const socket = createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
const port = socket.address().port; await new Promise(resolve => socket.close(resolve));
const url = 'http://127.0.0.1:' + port, password = randomBytes(24).toString('hex');
const env = { ...process.env };
for (const key of Object.keys(env)) if (key.toLowerCase().startsWith('yunsevo__') || key.startsWith('YUNSEVO_')) delete env[key];
Object.assign(env, { ASPNETCORE_URLS: url, Yunsevo__UnattendedSetup: 'true', Yunsevo__AdminUsername: 'releaseadmin', Yunsevo__AdminPassword: password,
  Yunsevo__DataPath: path.join(root, 'data'), Yunsevo__ApiKeyFile: path.join(root, 'api-key.txt'), Yunsevo__InstallationFile: path.join(root, 'server-settings.json'), Logging__LogLevel__Default: 'Warning' });
let serverLog = '';
const server = spawn(executable('Server'), [], { env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
server.stdout.on('data', b => serverLog += b); server.stderr.on('data', b => serverLog += b);
let serverError;
server.on('error', error => serverError = error);
async function run(file, args, extra = {}, timeout = 90000) {
  const child = spawn(file, args, { env: { ...env, ...extra }, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  let output = ''; child.stdout.on('data', b => output += b); child.stderr.on('data', b => output += b);
  const timer = setTimeout(() => child.kill('SIGKILL'), timeout);
  try { const [code] = await once(child, 'exit'); if (code !== 0) throw new Error('Native process failed (' + code + '): ' + output); return output; }
  finally { clearTimeout(timer); }
}
try {
  const deadline = Date.now() + 60000;
  for (;;) {
    if (serverError) throw serverError;
    if (server.exitCode !== null || server.signalCode !== null) throw new Error('Server exited before health check');
    try { if ((await fetch(url + '/health')).ok) break; } catch {}
    if (Date.now() > deadline) throw new Error('Server startup timed out');
    await delay(150);
  }
  const loginPage = await (await fetch(url)).text();
  assert(['android-arm64', 'android-x64'].every(target => loginPage.includes('yunsevo-' + target + '.apk')), 'Packaged login page exposes both signed Android downloads');
  for (const target of ['android-arm64', 'android-x64']) {
    const file = 'yunsevo-' + target + '.apk';
    const download = await fetch(url + '/downloads/' + file, { headers: { Range: 'bytes=0-3' } });
    assert(download.status === 206 && Buffer.from(await download.arrayBuffer()).toString('hex') === '504b0304', 'Packaged ' + target + ' download supports an exact APK range');
  }
  assert((await fetch(url + '/api/entries')).status === 401, 'Unauthenticated cloud access is denied');
  const loginResponse = await fetch(url + '/api/auth/device/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username: 'releaseadmin', password }) });
  assert(loginResponse.ok, 'Native server accepts the configured account');
  const login = await loginResponse.json(), headers = { Authorization: 'Bearer ' + login.accessToken };
  assert((await fetch(url + '/api/reconstruction/jobs', { headers })).status === 404, 'Retired model-generation API is absent from the packaged server');
  const folder = '个人/releaseadmin/release-' + rid;
  assert((await fetch(url + '/api/folders', { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json' }, body: JSON.stringify({ path: folder }) })).ok, 'Native server creates a cloud folder');
  const payload = 'Yunsevo native release: ' + rid + '\n', filePath = folder + '/cloud.txt';
  const upload = await fetch(url + '/api/content?path=' + encodeURIComponent(filePath), { method: 'PUT', headers: { ...headers, 'If-None-Match': '*' }, body: payload });
  assert(upload.ok, 'Native server stores file content');
  const entry = await upload.json();
  assert(await (await fetch(url + '/api/content/' + entry.id, { headers })).text() === payload, 'Native server downloads the exact stored bytes');
  const local = path.join(root, 'headless-files'), config = path.join(root, 'client.json');
  await writeFile(config, JSON.stringify({ server: url, username: 'releaseadmin', remoteDirectory: folder, syncRoot: local }));
  await run(executable('Headless'), ['--config', config, '--once'], { YUNSEVO_PASSWORD: password });
  assert(await readFile(path.join(local, 'cloud.txt'), 'utf8') === payload, 'Native headless client authenticates and downloads');
  await writeFile(path.join(local, 'local.txt'), 'headless bidirectional upload');
  await run(executable('Headless'), ['--config', config, '--once'], { YUNSEVO_PASSWORD: password });
  const catalog = await (await fetch(url + '/api/entries', { headers })).json();
  const uploaded = catalog.entries.find(x => x.path === folder + '/local.txt');
  assert(uploaded && await (await fetch(url + '/api/content/' + uploaded.id, { headers })).text() === 'headless bidirectional upload', 'Native headless client uploads exact local bytes');
  const desktopLocal = path.join(root, 'desktop-files');
  await run(executable('Desktop'), ['--probe', '--server', url, '--root', desktopLocal, '--remote-dir', folder], { YUNSEVO_USERNAME: 'releaseadmin', YUNSEVO_PASSWORD: password });
  assert(await readFile(path.join(desktopLocal, 'cloud.txt'), 'utf8') === payload, 'Native desktop binary authenticates and synchronizes without a GUI');
  const desktopHome = path.join(root, 'desktop-ui');
  const command = rid.startsWith('linux-') ? 'xvfb-run' : executable('Desktop');
  const args = rid.startsWith('linux-') ? ['-a', executable('Desktop'), '--smoke'] : ['--smoke'];
  await run(command, args, { YUNSEVO_DESKTOP_HOME: desktopHome });
  const screenshot = await readFile(path.join(desktopHome, 'window.png'));
  assert(screenshot.subarray(0, 8).toString('hex') === '89504e470d0a1a0a' && screenshot.length > 1000, 'Avalonia desktop starts, renders a real window and shuts down');
  const report = { rid, checkedAt: new Date().toISOString(), checks, gui: 'Real native Avalonia login window rendered', sync: 'Native server plus headless and desktop synchronization smoke', placeholders: rid.startsWith('win-') ? 'Not exercised by this CI smoke; separate Windows CFAPI regression required' : 'Not implemented; full local copies', signing: 'No commercial signing or Apple notarization', binaries: {} };
  for (const role of ['Server', 'Desktop', 'Headless']) report.binaries[role] = { bytes: (await stat(executable(role))).size, sha256: createHash('sha256').update(await readFile(executable(role))).digest('hex') };
  await mkdir('artifacts/release', { recursive: true });
  await copyFile(path.join(desktopHome, 'window.png'), 'artifacts/release/desktop-' + rid + '.png');
  await writeFile('artifacts/release/verification-' + rid + '.json', JSON.stringify(report, null, 2) + '\n');
  console.log('Native smoke passed: ' + checks.length + ' checks on ' + rid);
} finally {
  if (!serverError && server.exitCode === null && server.signalCode === null) { const exited = once(server, 'exit'); server.kill(); await exited; }
  await writeFile(path.join(root, 'server.log'), serverLog.replaceAll(password, '[redacted]'));
}
