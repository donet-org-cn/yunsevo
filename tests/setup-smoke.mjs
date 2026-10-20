// Real first-install UI and restart checks. Uses only isolated .test-data directories.
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile, access } from 'node:fs/promises';
import path from 'node:path';
import net from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';
const workspace = process.cwd();
const root = path.join(workspace, '.test-data', 'setup-' + new Date().toISOString().replace(/[:.]/g, '-'));
await mkdir(root, { recursive: true });
const exe = process.argv.find(x => x.startsWith('--server-exe='))?.slice(13) ?? path.join(workspace, 'src/Yunsevo.Server/bin/Release/net10.0/Yunsevo.Server.dll');
const launch = exe.endsWith('.dll') ? ['dotnet', [exe]] : [exe, []];
const socket = net.createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
const port = socket.address().port; await new Promise(resolve => socket.close(resolve));
const url = 'http://127.0.0.1:' + port;
const initialPath = path.join(root, 'initial-default');
const selectedPath = path.join(root, '文件存储');
const settings = path.join(root, 'server-settings.json');
const keyFile = path.join(root, 'api-key.txt');
const codeFile = path.join(root, 'setup-code.txt');
const env = { ...process.env, ASPNETCORE_URLS: url, Yunsevo__DataPath: initialPath, Yunsevo__ApiKeyFile: keyFile, Yunsevo__InstallationFile: settings, Logging__LogLevel__Default: 'Warning' };
for (const key of Object.keys(env)) if (['YUNSEVO__APIKEY', 'YUNSEVO__UNATTENDEDSETUP', 'YUNSEVO__ADMINPASSWORD', 'YUNSEVO__CERTIFICATEPATH'].includes(key.toUpperCase())) delete env[key];
let server, browser, ws, checks = 0, logs = '', browserLogs = '', sequence = 0;
const pending = new Map(), errors = [];
const password = 'Install-fixture-password-0123456789', username = 'cloudowner';
function assert(value, message) { if (!value) throw new Error(message); checks++; console.log('PASS: ' + message); }
async function exists(file) { try { await access(file); return true; } catch { return false; } }
async function until(operation, label, timeout = 20000) {
  const end = Date.now() + timeout; while (Date.now() < end) { const result = await operation(); if (result) return result; await delay(100); }
  throw new Error('Timeout: ' + label);
}
function start() {
  server = spawn(...launch, { cwd: workspace, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  server.stdout.on('data', bytes => { logs += bytes; }); server.stderr.on('data', bytes => { logs += bytes; });
  return server;
}
async function stop(child) { if (child && child.exitCode === null) { child.kill(); await Promise.race([new Promise(resolve => child.once('exit', resolve)), delay(10000)]); } }
async function health(expected) { await until(async () => { if (server.exitCode !== null) throw new Error('Server exited: ' + logs); try { return await (await fetch(url + '/health')).text() === expected; } catch { return false; } }, expected); }
async function request(endpoint, body, code, origin) {
  return fetch(url + endpoint, { method: body === undefined ? 'GET' : 'POST', headers: { 'Content-Type': 'application/json', ...(code ? { 'X-Yunsevo-Setup-Code': code } : {}), ...(origin ? { Origin: origin } : {}) }, ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
}
function cdp(method, params = {}) { const id = ++sequence; return new Promise((resolve, reject) => { pending.set(id, { resolve, reject }); ws.send(JSON.stringify({ id, method, params })); }); }
async function evaluate(expression) { const r = await cdp('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture: true }); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? 'Browser script failed'); return r.result.value; }
async function input(id, value) { await evaluate(`document.getElementById(${JSON.stringify(id)}).value=${JSON.stringify(value)}`); }
async function submit(id) { await evaluate(`document.getElementById(${JSON.stringify(id)}).requestSubmit()`); }
async function screenshot(name) { const r = await cdp('Page.captureScreenshot', { format: 'png' }); await writeFile(path.join(root, name + '.png'), Buffer.from(r.data, 'base64')); }
try {
  start(); await health('Yunsevo setup required');
  const code = (await readFile(codeFile, 'utf8')).trim(); const key = (await readFile(keyFile, 'utf8')).trim();
  assert(code.length === 64 && key.length === 64 && key !== code, 'Fresh server generates distinct random installation code and API key');
  assert((await (await fetch(url + '/api/setup/status')).json()).required, 'Fresh server requires installation');
  assert((await fetch(url + '/api/entries')).status === 503, 'File APIs remain unavailable before installation');
  assert((await request('/api/setup/defaults')).status === 403, 'Installation defaults require the local installation code');
  assert((await request('/api/setup/defaults', undefined, code, 'https://other-site.invalid')).status === 403, 'Cross-origin initialization is rejected even with a correct code');
  assert((await fetch(url + '/setup-code.txt')).status === 404 && (await fetch(url + '/api-key.txt')).status === 404, 'Neither installation code nor API key is exposed by the web server');
  const valid = { username, password, confirmPassword: password, dataPath: selectedPath };
  for (const [change, status, label] of [[{ password: 'short', confirmPassword: 'short' }, 400, 'short passwords'], [{ username: '../invalid' }, 400, 'invalid administrator names'], [{ dataPath: 'relative/path' }, 400, 'relative storage paths'], [{ username: null }, 400, 'null account fields'], [{ confirmPassword: 'mismatch' }, 400, 'mismatched passwords']]) {
    const response = await request('/api/setup/complete', { ...valid, ...change }, code);
    assert(response.status === status, 'Installation rejects ' + label);
  }
  const occupied = path.join(root, 'occupied'); await mkdir(occupied); await writeFile(path.join(occupied, 'keep.txt'), 'preserve');
  assert((await request('/api/setup/complete', { ...valid, dataPath: occupied }, code)).status === 409 && await readFile(path.join(occupied, 'keep.txt'), 'utf8') === 'preserve', 'Initialization preserves nonempty directories instead of overwriting files');
  const duplicate = spawn(...launch, { cwd: workspace, env, windowsHide: true, stdio: 'ignore' });
  const duplicateExit = await Promise.race([new Promise(resolve => duplicate.once('exit', resolve)), delay(10000).then(() => null)]);
  if (duplicateExit === null) await stop(duplicate);
  assert(duplicateExit !== null && duplicateExit !== 0, 'Only one installer can own the installation at a time');
  const profile = path.join(root, 'edge-profile');
  browser = spawn(process.env.YUNSEVO_TEST_EDGE ?? 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe', ['--headless=new','--disable-gpu','--no-first-run','--no-default-browser-check','--disable-extensions','--remote-debugging-port=0','--user-data-dir=' + profile,'--window-size=1440,1000','about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  browser.stderr.on('data', bytes => { browserLogs += bytes; });
  const debugPort = await until(async () => { try { return Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]); } catch { return false; } }, 'Edge startup');
  const targets = await (await fetch('http://127.0.0.1:' + debugPort + '/json/list')).json(); ws = new WebSocket(targets.find(x => x.type === 'page').webSocketDebuggerUrl);
  await new Promise(resolve => ws.addEventListener('open', resolve, { once: true }));
  ws.addEventListener('message', e => { const m = JSON.parse(e.data); if (m.id) { const p = pending.get(m.id); pending.delete(m.id); if (m.error) p?.reject(new Error(m.error.message)); else p?.resolve(m.result); } else if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description ?? m.params.exceptionDetails.text); });
  await cdp('Page.enable'); await cdp('Runtime.enable'); await cdp('Emulation.setDeviceMetricsOverride', { width:1440,height:1000,deviceScaleFactor:1,mobile:false }); await cdp('Page.navigate', { url });
  await until(() => evaluate('!!document.getElementById("setup-verify")'), 'installer UI'); await screenshot('setup-verify');
  await input('setup-code', 'wrong-installation-code'); await submit('setup-verify'); await until(() => evaluate('!document.getElementById("setup-error").hidden'), 'wrong code message');
  assert(await evaluate('!document.getElementById("setup-verify").hidden'), 'Wrong installation code keeps the browser on the verification step');
  await input('setup-code', code); await submit('setup-verify'); await until(() => evaluate('!document.getElementById("setup-configure").hidden'), 'configuration step');
  assert(await evaluate('document.getElementById("setup-path").value') === initialPath, 'Verified installer pre-fills the configured default storage path');
  await input('setup-username', username); await input('setup-password', password); await input('setup-confirm', password + '-different'); await input('setup-path', selectedPath); await submit('setup-configure');
  await until(() => evaluate('!document.getElementById("setup-error").hidden'), 'password mismatch message'); assert(await evaluate('!document.getElementById("setup-configure").hidden'), 'Password mismatch is explained without losing the installer form');
  await input('setup-confirm', password); await screenshot('setup-configure');
  await cdp('Emulation.setDeviceMetricsOverride', { width:390,height:844,deviceScaleFactor:1,mobile:true }); await screenshot('setup-mobile');
  assert(await evaluate('document.documentElement.scrollWidth<=innerWidth'), 'Installation form fits a mobile screen without horizontal overflow');
  await cdp('Emulation.setDeviceMetricsOverride', { width:1440,height:1000,deviceScaleFactor:1,mobile:false }); await submit('setup-configure');
  await health('Yunsevo OK'); await until(() => evaluate('!!document.getElementById("login-form") && !!window.Yunsevo && !document.getElementById("login-view").hidden'), 'automatic login page navigation');
  assert(!server.killed && server.exitCode === null, 'Installer transitions into the full server in the same process');
  assert(!(await (await fetch(url + '/api/setup/status')).json()).required && !await exists(codeFile), 'Successful installation disables setup and removes its one-time code');
  assert(!await exists(path.join(initialPath, 'catalog.json')) && await exists(path.join(selectedPath, 'catalog.json')), 'Cloud database is created only in the selected storage directory');
  const saved = JSON.parse(await readFile(settings, 'utf8')); assert(saved.complete && saved.dataPath === selectedPath && !JSON.stringify(saved).includes(password), 'Restart settings persist the selected path without storing an administrator password');
  const catalogText = await readFile(path.join(selectedPath, 'catalog.json'), 'utf8'); assert(!catalogText.includes(password) && !catalogText.includes(code) && !catalogText.includes(key), 'Account database contains password hashes rather than installer secrets');
  await input('login-username', username); await input('login-password', password); await submit('login-form'); await until(() => evaluate('!document.getElementById("app-view").hidden'), 'new administrator login');
  assert(await evaluate('!document.getElementById("nav-admin").hidden'), 'Custom administrator can sign in through the normal browser login'); await screenshot('installed-admin');
  const auth = await (await request('/api/auth/login', { username, password })).json(); assert(auth.user?.username === username && auth.user?.isAdmin, 'Selected administrator credentials are active');
  const headers = { Authorization: 'Bearer ' + auth.accessToken };
  const upload = await fetch(url + '/api/content?path=' + encodeURIComponent('个人/' + username + '/安装验证.txt'), { method:'PUT',headers:{...headers,'If-None-Match':'*'},body:'selected storage directory' });
  assert(upload.ok, 'New administrator uploads into the automatically created private folder'); const entry = await upload.json();
  assert((await request('/api/setup/complete', valid, code)).status === 401, 'Completed server does not accept installation requests');
  await stop(server); start(); await health('Yunsevo OK');
  assert(!(await (await fetch(url + '/api/setup/status')).json()).required, 'Restart goes directly to the installed server');
  const download = await fetch(url + '/api/content/' + entry.id, { headers }); assert(download.ok && await download.text() === 'selected storage directory', 'Selected storage directory, saved account and session survive process restart');
  const pendingSettings = JSON.parse(await readFile(settings, 'utf8')); pendingSettings.complete = false; await stop(server); await writeFile(settings, JSON.stringify(pendingSettings)); start(); await health('Yunsevo OK');
  assert(JSON.parse(await readFile(settings, 'utf8')).complete && (await fetch(url + '/api/content/' + entry.id, { headers })).ok, 'An interrupted installer with a committed catalog recovers without resetting accounts or files');
  await stop(server); env.Yunsevo__InstallationFile = path.join(root, 'missing-disk-settings.json'); await writeFile(env.Yunsevo__InstallationFile, JSON.stringify({ version:1,complete:true,dataPath:path.join(root,'unavailable-disk') })); start();
  await until(() => server.exitCode !== null, 'missing installed storage fails closed'); assert(server.exitCode !== 0 && !await exists(path.join(root,'unavailable-disk','catalog.json')), 'Missing storage after installation stops startup without creating a new administrator');
  env.Yunsevo__InstallationFile = path.join(root,'retry-server-settings.json'); const retryPath=path.join(root,'retry-empty-store'); await mkdir(path.join(retryPath,'blobs'),{recursive:true}); await writeFile(path.join(retryPath,'store.lock'),'');
  await writeFile(env.Yunsevo__InstallationFile,JSON.stringify({version:1,complete:false,dataPath:retryPath})); start(); await health('Yunsevo setup required');
  const retryCode=(await readFile(codeFile,'utf8')).trim(); const retryResponse=await request('/api/setup/complete',{...valid,dataPath:retryPath},retryCode); assert(retryResponse.ok,'Installer retries its own empty storage artifacts left before the first catalog commit'); await health('Yunsevo OK');
  assert((await request('/api/auth/login',{username,password})).ok,'Retried initialization creates the selected administrator successfully');
  assert(!logs.includes(password) && !logs.includes(code) && !logs.includes(key), 'Installation and restart logs contain no passwords, installation codes or API keys');
  assert(errors.length === 0, 'Installer and automatic navigation have no browser script errors');
  await writeFile(path.join(root,'verification.json'), JSON.stringify({ checks,server:exe,selectedPath,browser:'Microsoft Edge',passed:true },null,2)); console.log('ALL PASSED: ' + checks + ' setup checks. Artifacts: ' + root);
} catch (e) { if (ws?.readyState === WebSocket.OPEN) try { await screenshot('failure'); } catch {} console.error(e); process.exitCode = 1; }
finally { if (ws) ws.close(); await stop(browser); await stop(server); await writeFile(path.join(root,'server.log'),logs); await writeFile(path.join(root,'browser.log'),browserLogs); }
