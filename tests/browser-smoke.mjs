// Windows / Node 24: real Edge browser, no npm dependencies. All data and profiles are isolated.
import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile, readdir, copyFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import path from 'node:path';
import net from 'node:net';
import { setTimeout as delay } from 'node:timers/promises';

const workspace = process.cwd();
const root = path.join(workspace, '.test-data', 'web-' + new Date().toISOString().replace(/[:.]/g, '-'));
await mkdir(root, { recursive: true });
const defaultKey = process.argv.includes('--default-key-file');
const exe = process.argv.find(arg => arg.startsWith('--server-exe='))?.slice(13) ?? path.join(workspace, 'artifacts/server/MyPan.Server.exe');
const keyFile = defaultKey ? path.join(path.dirname(exe), 'api-key.txt') : path.join(root, 'api-key.txt');
const edge = process.env.MYPAN_TEST_EDGE ?? 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const socket = net.createServer(); await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
const port = socket.address().port; await new Promise(resolve => socket.close(resolve));
const url = 'http://127.0.0.1:' + port;
const env = { ...process.env, ASPNETCORE_URLS: url, MyPan__DataPath: path.join(root, 'server'), MyPan__MaxUploadBytes: String(8 * 1024 * 1024), Logging__LogLevel__Default: 'Warning' };
env.MyPan__UnattendedSetup = 'true'; env.MyPan__AdminPassword = 'Browser-fixture-password-0123456789'; env.MyPan__InstallationFile = path.join(root, 'server-settings.json');
const testDownloads = process.argv.includes('--client-downloads');
const desktopPackage = 'yunsevo-drive-win-x64.zip';
if (testDownloads) {
  env.MyPan__ClientDownloadsPath = path.join(root, 'client-packages');
  await mkdir(env.MyPan__ClientDownloadsPath);
  for (const name of [desktopPackage, 'yunsevo-headless-win-x64.zip']) await copyFile(path.join(path.dirname(exe), 'downloads', name), path.join(env.MyPan__ClientDownloadsPath, name));
  await writeFile(path.join(env.MyPan__ClientDownloadsPath, 'api-key.txt'), 'private-fixture-not-for-download');
  await writeFile(path.join(env.MyPan__ClientDownloadsPath, 'unpublished.zip'), 'unpublished-fixture');
}
for (const name of Object.keys(env)) if (name.toUpperCase() === 'MYPAN__APIKEY' || name.toUpperCase() === 'MYPAN__APIKEYFILE') delete env[name];
if (!defaultKey) env.MyPan__ApiKeyFile = keyFile;
let server, browser, cdp, inputNode, serverLog = '', browserLog = '', checks = 0;
const errors = [];
function assert(condition, message) { if (!condition) throw new Error(message); checks++; console.log('PASS: ' + message); }
async function until(operation, description, timeout = 20000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { const result = await operation(); if (result) return result; await delay(100); }
  throw new Error('Timeout: ' + description);
}
async function stop(child) { if (child && child.exitCode === null) { child.kill(); await Promise.race([new Promise(resolve => child.once('exit', resolve)), delay(10000)]); } }
class DevTools {
  constructor(ws) {
    this.ws = ws; this.sequence = 0; this.pending = new Map(); this.acceptDialog = true;
    ws.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (message.id) { const pending = this.pending.get(message.id); this.pending.delete(message.id); if (message.error) pending?.reject(new Error(message.error.message)); else pending?.resolve(message.result); }
      else if (message.method === 'Page.javascriptDialogOpening') this.send('Page.handleJavaScriptDialog', { accept: this.acceptDialog }).catch(() => {});
      else if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text);
      else if (message.method === 'Log.entryAdded' && message.params.entry.level === 'error' && !/\b(401|413)\b/.test(message.params.entry.text)) errors.push(message.params.entry.text);
    });
  }
  send(method, params = {}) {
    const id = ++this.sequence;
    return new Promise((resolve, reject) => { this.pending.set(id, { resolve, reject }); this.ws.send(JSON.stringify({ id, method, params })); });
  }
  async evaluate(expression) {
    const result = await this.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true, userGesture: true });
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description ?? 'Browser script failed');
    return result.result.value;
  }
}
async function waitDom(expression, message) { return until(() => cdp.evaluate(expression), message); }
async function click(id) { await cdp.evaluate(`document.getElementById(${JSON.stringify(id)}).click()`); }
async function input(id, value) { await cdp.evaluate(`document.getElementById(${JSON.stringify(id)}).value=${JSON.stringify(value)}`); }
async function submit(id) { await cdp.evaluate(`document.getElementById(${JSON.stringify(id)}).requestSubmit()`); }
async function reloadView(condition, description, signedIn = true) {
  const marker = Math.random().toString(36); await cdp.evaluate(`window.__reloadProbe=${JSON.stringify(marker)}`);
  await cdp.send('Page.reload', { ignoreCache: true });
  await waitDom(`window.__reloadProbe!==${JSON.stringify(marker)} && !!window.MyPan && ${signedIn?'!document.getElementById("app-view").hidden':'!document.getElementById("login-view").hidden'} && (${condition})`, description);
  if(signedIn)await cdp.evaluate('window.showSaveFilePicker=undefined');
  if(inputNode){const documentNode=await cdp.send('DOM.getDocument');inputNode=await cdp.send('DOM.querySelector',{nodeId:documentNode.root.nodeId,selector:'#file-input'});}
}
async function screenshot(name) {
  await waitDom('document.documentElement.getBoundingClientRect().width > 0 && document.documentElement.getBoundingClientRect().height > 0', 'page layout before capture');
  await cdp.evaluate('document.fonts.ready.then(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))))');
  const { data } = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false }); await writeFile(path.join(root, name + '.png'), Buffer.from(data, 'base64'));
}
async function action(name, label) {
  await cdp.evaluate(`(() => {const row=Array.from(document.querySelectorAll('#file-list tr')).find(x=>x.querySelector('.entry-name')?.textContent===${JSON.stringify(name)});const button=Array.from(row?.querySelectorAll('button')??[]).find(x=>x.textContent===${JSON.stringify(label)});if(!button)throw new Error('Missing row action');button.click()})()`);
}
try {
  server = spawn(exe, [], { cwd: workspace, env, windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  server.stdout.on('data', data => { serverLog += data; }); server.stderr.on('data', data => { serverLog += data; });
  await until(async () => { if (server.exitCode !== null) throw new Error('Server exited'); try { return (await fetch(url + '/health')).ok; } catch { return false; } }, 'server startup');
  const key = (await readFile(keyFile, 'utf8')).trim();
  assert(/^[0-9A-F]{64}$/i.test(key), 'Server has a persisted random API key');
  const headers = { Authorization: 'Bearer ' + key };
  const catalog = async () => (await fetch(url + '/api/entries', { headers })).json();
  const profile = path.join(root, 'edge-profile');
  browser = spawn(edge, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-extensions', '--remote-debugging-port=0', '--user-data-dir=' + profile, '--window-size=1440,1000', 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  browser.stderr.on('data', data => { browserLog += data; });
  const devtoolsPort = await until(async () => { try { return Number((await readFile(path.join(profile, 'DevToolsActivePort'), 'utf8')).split('\n')[0]); } catch { return false; } }, 'Edge DevTools startup');
  const targets = await (await fetch('http://127.0.0.1:' + devtoolsPort + '/json/list')).json();
  const ws = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.addEventListener('open', resolve, { once: true }); ws.addEventListener('error', reject, { once: true }); });
  cdp = new DevTools(ws);
  await cdp.send('Page.enable'); await cdp.send('Runtime.enable'); await cdp.send('Log.enable');
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  await cdp.send('Page.navigate', { url });
  await waitDom('!!window.MyPan && !document.getElementById("login-view").hidden', 'login page');
  await screenshot('login-desktop');
  if (testDownloads) {
    assert(await cdp.evaluate('document.querySelectorAll(".client-download").length===2 && document.getElementById("client-download-title").textContent==="客户端下载"'), 'Login page offers only the two actually published clients without authentication');
    const packageUrl = url + '/downloads/' + desktopPackage;
    const localPackage = await readFile(path.join(env.MyPan__ClientDownloadsPath, desktopPackage));
    const head = await fetch(packageUrl, { method: 'HEAD' });
    assert(head.status === 200 && Number(head.headers.get('content-length')) === localPackage.length && head.headers.get('content-disposition')?.includes(desktopPackage), 'Public download supports HEAD and the correct attachment name and length');
    const range = await fetch(packageUrl, { headers: { Range: 'bytes=0-31' } });
    assert(range.status === 206 && Buffer.from(await range.arrayBuffer()).equals(localPackage.subarray(0, 32)), 'Public download supports byte ranges for resuming');
    const invalidRange = await fetch(packageUrl, { headers: { Range: 'bytes=' + localPackage.length + '-' } });
    assert(invalidRange.status === 416, 'Invalid package ranges are rejected');
    for (const target of ['api-key.txt', 'unpublished.zip', 'yunsevo-drive-osx-arm64.tar.gz', '%2e%2e%2fapi-key.txt']) {
      const response = await fetch(url + '/downloads/' + target);
      assert(response.status === 404, 'Public downloads reject missing or unapproved package: ' + target);
    }
    assert((await fetch(url + '/api/entries')).status === 401, 'Public downloads do not bypass cloud file authentication');
    const downloadPath = path.join(root, 'login-downloads');
    await mkdir(downloadPath);
    await cdp.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath });
    await cdp.evaluate('document.querySelector(".client-download").click()');
    const downloaded = await until(async () => { try { const file = await readFile(path.join(downloadPath, desktopPackage)); return file.length === localPackage.length ? file : false; } catch { return false; } }, 'download client from login page');
    const hash = bytes => createHash('sha256').update(bytes).digest('hex');
    assert(hash(downloaded) === hash(localPackage), 'Clicking download before login saves the exact published ZIP');
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
    await screenshot('login-mobile');
    assert(await cdp.evaluate('document.documentElement.scrollWidth<=innerWidth && !!document.querySelector(".client-download").getClientRects().length'), 'Mobile login and download links fit the screen width');
    await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  }
  assert(await cdp.evaluate('document.getElementById("account-mode").checked && !document.getElementById("account-fields").hidden'), 'New browser sessions default to personal account login');
  await cdp.evaluate('document.getElementById("account-mode").checked=false;document.getElementById("account-mode").dispatchEvent(new Event("change"))');
  await input('api-key', 'X'.repeat(64)); await submit('login-form');
  await waitDom('!document.getElementById("login-error").hidden', 'wrong key error');
  assert(await cdp.evaluate('!document.getElementById("login-view").hidden'), 'Wrong API key cannot enter file manager');
  await input('api-key', key); await submit('login-form');
  await waitDom('!document.getElementById("app-view").hidden', 'file manager login');
  await cdp.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: path.join(root, 'downloads') });
  await cdp.evaluate('window.showSaveFilePicker=undefined');
  assert(await cdp.evaluate('document.getElementById("api-key").value==="" && localStorage.length===0 && sessionStorage.length===0'), 'Login clears password field and does not persist key in browser storage');
  let external = await fetch(url + '/api/folders', { method: 'POST', headers: { ...headers, 'Content-Type': 'application/json' }, body: JSON.stringify({ path: '客户端资料' }) });
  assert(external.ok, 'Cloud directory is available for both web and clients');
  external = await fetch(url + '/api/content?path=' + encodeURIComponent('客户端资料/客户端新文件.txt'), { method: 'PUT', headers: { ...headers, 'If-None-Match': '*' }, body: '由另一客户端上传的内容' });
  if (!external.ok) throw new Error('External upload failed');
  const externalFile = await external.json();
  await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="客户端资料")', 'automatically discover client directory');
  await cdp.evaluate('Array.from(document.querySelectorAll(".folder-name")).find(x=>x.textContent==="客户端资料").click()');
  await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="客户端新文件.txt")', 'SSE discovers the uploaded child after its folder commit');
  assert(await cdp.evaluate('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="客户端新文件.txt")'), 'Web automatically shows externally uploaded files without manual refresh');
  external = await fetch(url + '/api/entries/' + externalFile.id, { method: 'DELETE', headers: { ...headers, 'If-Match': '"' + externalFile.version + '"' } });
  if (!external.ok) throw new Error('External delete failed');
  await waitDom('!Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="客户端新文件.txt")', 'automatically remove client-deleted file');
  assert(await cdp.evaluate('!document.getElementById("empty-state").hidden'), 'Web automatically reflects client-side deletions');
  await cdp.evaluate('document.querySelector("#breadcrumbs button").click()');
  await click('new-folder'); await input('entry-name', '网页测试'); await submit('name-form');
  await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="网页测试")', 'create folder');
  await cdp.evaluate('Array.from(document.querySelectorAll("#file-list tr")).find(x=>x.querySelector(".entry-name")?.textContent==="网页测试").dispatchEvent(new MouseEvent("dblclick",{bubbles:true}))');
  assert(await cdp.evaluate('document.getElementById("breadcrumbs").textContent.includes("网页测试")'), 'Folder creation and breadcrumb navigation work');
  assert(await cdp.evaluate('!document.getElementById("copy-directory").hidden'), 'Web directory offers copying its name for client configuration');
  await reloadView('window.MyPan.state.folder==="网页测试"','restore nested folder after refresh');
  assert(await cdp.evaluate('window.MyPan.state.authenticated && window.MyPan.state.key==="" && document.getElementById("breadcrumbs").textContent.includes("网页测试")'), 'Refresh restores a nested folder using the HttpOnly cookie without saving a bearer token');
  assert(await cdp.evaluate('!document.cookie.includes("mypan-session") && localStorage.length===0 && sessionStorage.length===0') && !(await cdp.evaluate('location.href')).includes(key), 'Restored login keeps credentials out of JavaScript cookies, web storage and URL');
  const filePath = path.join(root, '网页上传.txt'), zeroPath = path.join(root, '空文件.txt');
  const payload = '通过真实浏览器上传；客户端可按需下载。\n';
  await writeFile(filePath, payload); await writeFile(zeroPath, '');
  const documentNode = await cdp.send('DOM.getDocument');
  inputNode = await cdp.send('DOM.querySelector', { nodeId: documentNode.root.nodeId, selector: '#file-input' });
  await cdp.send('DOM.setFileInputFiles', { nodeId: inputNode.nodeId, files: [filePath, zeroPath] });
  await waitDom('document.getElementById("upload-title").textContent.includes("2 成功")', 'multiple uploads');
  let uploaded = (await catalog()).entries.find(entry => entry.path === '网页测试/网页上传.txt');
  assert(uploaded && uploaded.size === Buffer.byteLength(payload), 'Real browser uploads multiple Unicode-named files');
  assert((await catalog()).entries.some(entry => entry.path === '网页测试/空文件.txt' && entry.size === 0), 'Zero-byte browser uploads are preserved');
  assert(await (await fetch(url + '/api/content/' + uploaded.id, { headers })).text() === payload, 'Browser upload content matches server download bytes');
  await screenshot('files-desktop');
  await click('close-uploads');
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true });
  await screenshot('files-mobile');
  assert(await cdp.evaluate('document.documentElement.scrollWidth<=window.innerWidth'), 'Mobile viewport has no page overflow');
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false });
  await click('close-uploads');
  await input('search', '不存在'); await cdp.evaluate('document.getElementById("search").dispatchEvent(new Event("input"))');
  await waitDom('!document.getElementById("empty-state").hidden && document.querySelectorAll("#file-list tr").length===0', 'server-side search results');
  assert(await cdp.evaluate('!document.getElementById("empty-state").hidden && document.querySelectorAll("#file-list tr").length===0'), 'Search filters file list and explains empty results');
  await input('search', ''); await cdp.evaluate('document.getElementById("search").dispatchEvent(new Event("input"))');
  await waitDom('document.querySelectorAll("#file-list tr").length===2', 'clear server-side search');
  await writeFile(filePath, '新版本');
  cdp.acceptDialog = false;
  await cdp.send('DOM.setFileInputFiles', { nodeId: inputNode.nodeId, files: [filePath] });
  await waitDom('document.getElementById("upload-title").textContent.includes("1 跳过")', 'skip duplicate');
  assert((await catalog()).entries.find(entry => entry.id === uploaded.id).version === uploaded.version, 'Cancelling overwrite preserves original cloud version');
  cdp.acceptDialog = true;
  await cdp.send('DOM.setFileInputFiles', { nodeId: inputNode.nodeId, files: [filePath] });
  await waitDom('document.getElementById("upload-title").textContent.includes("1 成功")', 'replace duplicate');
  assert((await catalog()).entries.find(entry => entry.id === uploaded.id).version !== uploaded.version, 'Confirmed overwrite updates file with version guard');
  await click('close-uploads');
  await action('网页上传.txt', '重命名'); await input('entry-name', '已重命名.txt'); await submit('name-form');
  await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="已重命名.txt")', 'rename');
  assert((await catalog()).entries.some(entry => entry.path === '网页测试/已重命名.txt'), 'Browser rename updates server catalog');
  await action('已重命名.txt', '删除');
  await until(async () => !(await catalog()).entries.some(entry => entry.path.endsWith('已重命名.txt')), 'delete');
  await click('nav-trash');
  await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="已重命名.txt")', 'trash');
  await screenshot('trash-desktop');
  await cdp.evaluate('Array.from(document.querySelectorAll("#file-list tr")).find(x=>x.querySelector(".entry-name")?.textContent==="已重命名.txt").dispatchEvent(new MouseEvent("dblclick",{bubbles:true}))');
  await waitDom('document.querySelector("#preview-content pre")?.textContent==="新版本"','double-click deleted text preview');
  assert(true,'Double-click previews recycled files without restoring them');
  await screenshot('trash-preview');await cdp.evaluate('document.getElementById("preview-dialog").close()');
  await action('已重命名.txt','下载');
  await until(async()=>{try{return await readFile(path.join(root,'downloads','已重命名.txt'),'utf8')==='新版本';}catch{return false;}},'recycled browser download');
  assert(true,'Recycle-bin download returns the original bytes and filename');
  await action('已重命名.txt', '恢复');
  await until(async () => (await catalog()).entries.some(entry => entry.path.endsWith('已重命名.txt')), 'restore');
  assert((await catalog()).entries.some(entry => entry.path === '网页测试/已重命名.txt'), 'Browser delete and recycle-bin restore preserve original path');
  await click('nav-files');
  await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="网页测试")', 'back to files');
  await cdp.evaluate('(() => { const data=new DataTransfer();data.items.add(new File(["拖放内容"],"拖放.txt",{type:"text/plain"}));document.getElementById("drop-area").dispatchEvent(new DragEvent("drop",{bubbles:true,cancelable:true,dataTransfer:data})); })()');
  await until(async () => (await catalog()).entries.some(entry => entry.path === '拖放.txt'), 'drag and drop');
  assert((await catalog()).entries.some(entry => entry.path === '拖放.txt'), 'Drag-and-drop uploads through browser event handler');
  // Download is streamed to the chosen file in supported browsers; the fallback uses an authenticated blob.
  await cdp.evaluate('Object.defineProperty(window,"showSaveFilePicker",{configurable:true,value:undefined});delete window.showSaveFilePicker');
  await cdp.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: path.join(root, 'downloads') });
  await action('拖放.txt', '下载');
  await until(async () => { try { return await readFile(path.join(root, 'downloads', '拖放.txt'), 'utf8') === '拖放内容'; } catch { return false; } }, 'authenticated browser download');
  assert(true, 'Browser downloads authenticated content with original Unicode filename');
  const largePath = path.join(root, '超出限制.bin'); await writeFile(largePath, Buffer.alloc(8 * 1024 * 1024 + 1));
  await cdp.send('DOM.setFileInputFiles', { nodeId: inputNode.nodeId, files: [largePath, zeroPath] });
  await waitDom('document.getElementById("upload-title").textContent.includes("1 成功 · 1 失败")', 'isolate upload errors');
  assert(await cdp.evaluate('document.getElementById("upload-list").textContent.includes("超过服务端允许")'), 'Oversized upload explains the limit and remaining files still upload');
  // Throttle a real request so cancel is tested before the server can commit its body.
  await cdp.send('Network.enable');
  await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: 20, downloadThroughput: -1, uploadThroughput: 16384 });
  const slowPath = path.join(root, '取消上传.bin'); await writeFile(slowPath, Buffer.alloc(512 * 1024));
  await cdp.send('DOM.setFileInputFiles', { nodeId: inputNode.nodeId, files: [slowPath] });
  await waitDom('document.querySelector("#upload-list progress")?.value>0', 'upload starts before cancellation');
  await click('cancel-upload');
  await waitDom('!document.getElementById("close-uploads").hidden', 'upload cancellation completes');
  assert(!(await catalog()).entries.some(entry => entry.path === '取消上传.bin'), 'Cancelling an in-progress browser upload leaves no cloud entry');
  await cdp.send('Network.emulateNetworkConditions', { offline: false, latency: 0, downloadThroughput: -1, uploadThroughput: -1 });
  await cdp.send('DOM.setFileInputFiles', { nodeId: inputNode.nodeId, files: [filePath] });
  await waitDom('document.getElementById("upload-title").textContent.includes("1 成功")', 'upload after cancellation');
  assert((await catalog()).entries.some(entry => entry.path === '网页上传.txt'), 'New uploads work after cancelling an earlier request');
  if (process.argv.includes('--v2')) {
    await click('close-uploads');
    await click('nav-admin');
    await waitDom('!document.getElementById("admin-dialog").hidden', 'administration dialog');
    await input('user-name', 'browseruser'); await input('user-display', '网页测试用户'); await input('user-password', 'Web-user-password-0123456789'); await submit('user-form');
    await waitDom('document.getElementById("users-list").textContent.includes("browseruser")', 'create user in browser');
    assert(true, 'Administrator creates a login account from the browser');
    const createdUser = (await (await fetch(url + '/api/admin/users', { headers })).json()).find(user => user.username === 'browseruser');
    await screenshot('admin-users');
    await input('users-query','browseruser');await cdp.evaluate('document.getElementById("users-query").dispatchEvent(new Event("input"))');await waitDom('location.hash.includes("query=browseruser")','user search saved');
    await reloadView('window.MyPan.state.view==="admin-dialog" && window.MyPan.state.adminMode==="users"','restore filtered user management');
    assert(await cdp.evaluate('document.getElementById("users-query").value==="browseruser" && document.getElementById("users-list").textContent.includes("browseruser")'), 'Refreshing user management preserves the user filter and reloads matching accounts');
    await click('nav-files');await waitDom('window.MyPan.state.view==="files" && !window.MyPan.state.refreshing','load files before groups');await click('nav-groups');
    await waitDom('!document.getElementById("admin-dialog").hidden && !document.getElementById("groups-section").hidden', 'separate groups menu');
    await input('group-name', '网页用户组');
    await cdp.evaluate(`Array.from(document.getElementById('group-members').options).forEach(o=>o.selected=o.value===${JSON.stringify(createdUser.id)})`);
    await submit('group-form'); await waitDom('document.getElementById("groups-list").textContent.includes("网页用户组")', 'create group');
    const createdGroup = (await (await fetch(url + '/api/admin/groups', { headers })).json()).find(group => group.name === '网页用户组');
    assert(createdGroup.members.includes(createdUser.id), 'Administrator assigns user-group membership in the browser');
    await click('nav-admin'); await waitDom('!document.getElementById("users-section").hidden', 'return to user page');
    await cdp.evaluate('Array.from(document.querySelectorAll("#users-list .management-row")).find(x=>x.textContent.includes("browseruser")).querySelectorAll("button")[1].click()');
    await waitDom('document.getElementById("reset-dialog").open', 'dedicated reset password form'); await screenshot('reset-password-form');
    await input('reset-password','Web-user-password-0123456789'); await submit('reset-form');
    await waitDom('!document.getElementById("reset-result").hidden', 'password reset completed');
    assert(await cdp.evaluate('document.getElementById("reset-result-value").value.length>=12'), 'Administrator can reset a password through its dedicated workflow');
    await cdp.evaluate('document.getElementById("reset-dialog").close()');
    await waitDom('document.getElementById("reset-result-value").value===""', 'password result cleared after close event');
    assert(await cdp.evaluate('document.getElementById("reset-result-value").value===""'), 'Reset password is cleared when its dialog closes');
    await screenshot('admin-users-groups');
    await click('nav-groups');await waitDom('window.MyPan.state.adminMode==="groups" && !document.getElementById("groups-section").hidden','open groups before refresh');
    await reloadView('window.MyPan.state.view==="admin-dialog" && window.MyPan.state.adminMode==="groups"','restore group management');
    assert(await cdp.evaluate('!document.getElementById("groups-section").hidden && document.getElementById("nav-groups").getAttribute("aria-current")==="page"'), 'Refreshing user-group management preserves its page and selected navigation');
    await click('nav-files');await waitDom('window.MyPan.state.view==="files" && !window.MyPan.state.refreshing','return to loaded file list');
    await click('nav-settings'); await waitDom('!document.getElementById("settings-dialog").hidden', 'separate server settings menu');
    await reloadView('window.MyPan.state.view==="settings-dialog"','restore settings page');
    assert(await cdp.evaluate('!document.getElementById("settings-dialog").hidden && document.getElementById("nav-settings").classList.contains("selected")'), 'Refreshing settings remains in settings with its configuration loaded');
    assert(await cdp.evaluate('!!document.getElementById("setting-webdav") && !!document.getElementById("setting-smb") && !!document.getElementById("setting-ftp")'), 'Server settings exposes separate protocol switches'); await screenshot('server-settings');
    await input('environment-key', 'abcdef0123456789abcdef0123456789');
    await cdp.evaluate('document.getElementById("environment-address").value="amap";document.getElementById("environment-weather").value="amap"');
    await submit('environment-form'); await waitDom('document.getElementById("environment-key-status").textContent.includes("已配置")', 'save encrypted environment key');
    assert(await cdp.evaluate('document.getElementById("environment-key").value===""'), 'Saving provider settings clears the secret input and shows only configured status');
    await reloadView('window.MyPan.state.view==="settings-dialog"','reload environment settings');
    assert(await cdp.evaluate('document.getElementById("environment-address").value==="amap" && document.getElementById("environment-weather").value==="amap" && document.getElementById("environment-key").value===""'), 'Address and weather settings survive refresh without returning the provider key');
    await click('download-backup');
    await until(async()=>{const names=await readdir(path.join(root,'downloads'));return names.some(x=>/^yunsevo-backup.*\.zip$/.test(x));},'one-click migration ZIP download');
    assert(true,'Administrator downloads a migration archive through the settings page without a token in the URL');
    await click('nav-files');await waitDom('window.MyPan.state.view==="files" && !window.MyPan.state.refreshing','load files after audit');
    await click('nav-audit'); await waitDom('!document.getElementById("audit-dialog").hidden && document.getElementById("audit-list").children.length>0', 'audit menu records');
    assert(await cdp.evaluate('document.getElementById("audit-list").textContent.includes("admin") && document.getElementById("audit-list").textContent.includes("回收站恢复")'), 'Audit menu shows identified users and recycle-bin operation records'); await screenshot('audit-log');
    await input('audit-kind','trash'); await cdp.evaluate('document.getElementById("audit-kind").dispatchEvent(new Event("change"))'); await screenshot('recycle-audit');
    await waitDom('location.hash.includes("kindFilter=trash")','audit filter saved in navigation');
    await reloadView('window.MyPan.state.view==="audit-dialog" && document.getElementById("audit-kind").value==="trash"','restore filtered audit page');
    assert(await cdp.evaluate('document.getElementById("audit-list").textContent.includes("admin")'), 'Refreshing audit keeps the selected recycle-bin filter and reloads its records');
    await click('nav-files');await waitDom('window.MyPan.state.view==="files" && !window.MyPan.state.refreshing','load files after policy');
    await action('客户端资料', '设置'); await waitDom('document.getElementById("policy-dialog").open', 'folder policy');
    await input('policy-visibility', 'restricted'); await input('policy-quota', '10'); await input('policy-versions', '2'); await click('add-grant');
    await cdp.evaluate(`document.querySelector('#grant-list .grant-subject').value=${JSON.stringify('group:' + createdGroup.id)};document.querySelector('#grant-list .grant-rights').value='3'`);
    await screenshot('folder-permissions-quota'); await submit('policy-form'); await waitDom('!document.getElementById("policy-dialog").open', 'save folder policy');
    const sharedFolder = (await catalog()).entries.find(entry => entry.path === '客户端资料');
    const policy = await (await fetch(url + '/api/folders/' + sharedFolder.id + '/policy', { headers })).json();
    assert(policy.policy.quotaBytes === 10 * 1073741824 && policy.policy.keepVersions === 2 && policy.policy.grants.some(grant => grant.id === createdGroup.id), 'Browser saves group permissions, 10 GiB quota and version retention');
    async function apiUpload(name, body, existing = null) {
      const response = await fetch(url + '/api/content?path=' + encodeURIComponent(name), { method: 'PUT', headers: { ...headers, ...(existing ? { 'If-Match': '"' + existing.version + '"' } : { 'If-None-Match': '*' }) }, body });
      if (!response.ok) throw new Error('Fixture upload failed: HTTP ' + response.status); return response.json();
    }
    const textPayload = '<img src=x onerror="window.previewXss=true">\n安全文本预览';
    const textEntry = await apiUpload('预览.txt', textPayload);
    await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="预览.txt")', 'preview file discovered');
    await cdp.evaluate('Array.from(document.querySelectorAll("#file-list tr")).find(x=>x.querySelector(".entry-name")?.textContent==="预览.txt").dispatchEvent(new MouseEvent("dblclick",{bubbles:true}))'); await waitDom('!!document.querySelector("#preview-content pre")', 'text preview');
    assert(await cdp.evaluate(`document.querySelector('#preview-content pre').textContent===${JSON.stringify(textPayload)} && !window.previewXss`), 'Text preview displays markup as inert text'); await screenshot('text-preview');
    await cdp.evaluate('document.getElementById("preview-dialog").close()');
    const unsupported=await fetch(url+'/api/content?path='+encodeURIComponent('不可预览.bin'),{method:'PUT',headers:{...headers,'If-None-Match':'*'},body:'download fallback bytes'});if(!unsupported.ok)throw new Error('unsupported preview fixture');
    await cdp.evaluate('window.MyPan.refresh()');await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="不可预览.bin")','unsupported file listed');
    await cdp.evaluate('Array.from(document.querySelectorAll("#file-list tr")).find(x=>x.querySelector(".entry-name")?.textContent==="不可预览.bin").dispatchEvent(new MouseEvent("dblclick",{bubbles:true}))');
    await until(async()=>{try{return await readFile(path.join(root,'downloads','不可预览.bin'),'utf8')==='download fallback bytes';}catch{return false;}},'double-click downloads unsupported file');assert(true,'Double-click downloads files whose format cannot be previewed');
    await apiUpload('图片.png', Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aOioAAAAASUVORK5CYII=', 'base64'));
    await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="图片.png")', 'image discovered');
    await action('图片.png', '预览'); await waitDom('document.querySelector("#preview-content img")?.naturalWidth===1', 'image preview loads'); assert(true, 'Authenticated image preview renders actual image bytes');
    await cdp.evaluate('document.getElementById("preview-dialog").close()');
    const videoPath = path.join(root, '视频.mp4');
    const ffmpeg = spawn(process.env.MYPAN_TEST_FFMPEG ?? 'ffmpeg', ['-f','lavfi','-i','color=c=blue:s=160x90:d=1','-c:v','libx264','-pix_fmt','yuv420p','-movflags','+faststart','-y','-loglevel','error',videoPath], { windowsHide: true, stdio: 'ignore' });
    await new Promise((resolve, reject) => { ffmpeg.on('error', reject); ffmpeg.on('exit', code => code === 0 ? resolve() : reject(new Error('Video fixture generation failed'))); });
    await apiUpload('视频.mp4', await readFile(videoPath));
    await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="视频.mp4")', 'video discovered');
    await action('视频.mp4', '预览'); await waitDom('document.querySelector("#preview-content video")?.readyState>=1', 'video metadata');
    assert(await cdp.evaluate('document.querySelector("#preview-content video").duration>0.5'), 'Authenticated video preview loads real MP4 metadata and byte ranges');
    await cdp.evaluate('document.querySelector("#preview-content video").currentTime=0.5'); await screenshot('video-preview');
    await cdp.evaluate('document.getElementById("preview-dialog").close()');
    const newer = await apiUpload('预览.txt', '第二版', textEntry);
    await waitDom(`document.querySelector('#file-list tr[data-entry-id="${textEntry.id}"]')?.dataset.version===${JSON.stringify(newer.version)}`, 'version update reaches web');
    await action('预览.txt', '版本'); await waitDom('document.getElementById("versions-dialog").open', 'version history');
    assert(await cdp.evaluate('document.querySelectorAll("#version-list .management-row").length===2'), 'Browser lists current and retained historical versions');
    await cdp.evaluate('Array.from(document.querySelectorAll("#version-list button")).find(b=>b.textContent==="恢复").click()');
    await waitDom('!document.getElementById("versions-dialog").open', 'version restore');
    assert(await (await fetch(url + '/api/content/' + textEntry.id, { headers })).text() === textPayload, 'Browser restores a historical file version');
    await apiUpload('客户端资料/共享.txt', '通过用户组访问');
    const pageFolder=await fetch(url+'/api/folders',{method:'POST',headers:{...headers,'Content-Type':'application/json'},body:JSON.stringify({path:'分页演示'})});if(!pageFolder.ok)throw new Error('Paging fixture folder failed');
    let deletedFixture;
    for(let i=0;i<23;i++){
      const response=await fetch(url+'/api/content?path='+encodeURIComponent('分页演示/分页'+String(i).padStart(2,'0')+'.txt'),{method:'PUT',headers:{...headers,'If-None-Match':'*'},body:'page '+i});if(!response.ok)throw new Error('Paging fixture upload failed');const entry=await response.json();if(i===22)deletedFixture=entry;
    }
    await cdp.evaluate('document.querySelector("#breadcrumbs button").click()');
    await waitDom('Array.from(document.querySelectorAll(".folder-name")).some(x=>x.textContent==="分页演示")','paging fixture discovered');
    await cdp.evaluate('Array.from(document.querySelectorAll(".folder-name")).find(x=>x.textContent==="分页演示").click()');
    await waitDom('document.querySelectorAll("#file-list tr").length===20','default server page size');
    await cdp.evaluate('const size=document.querySelector("#files-pager select");size.value="10";size.dispatchEvent(new Event("change"))');
    await waitDom('document.querySelectorAll("#file-list tr").length===10','ten-row page');
    await input('search','分页');await cdp.evaluate('document.getElementById("search").dispatchEvent(new Event("input"))');await waitDom('window.MyPan.state.query==="分页" && !window.MyPan.state.refreshing','paging search applied');
    await cdp.evaluate('document.querySelector("[data-sort=name]").click()');await waitDom('!window.MyPan.state.ascending && !window.MyPan.state.refreshing','descending name sort');
    const firstPage=await cdp.evaluate('Array.from(document.querySelectorAll("#file-list tr")).map(x=>x.dataset.entryId)');
    await cdp.evaluate('Array.from(document.querySelectorAll("#files-pager button")).find(x=>x.textContent==="下一页").click()');
    await waitDom('document.querySelector("#files-pager span").textContent.includes("2 / 3")','second file page');
    assert((await cdp.evaluate('Array.from(document.querySelectorAll("#file-list tr")).map(x=>x.dataset.entryId)')).every(id=>!firstPage.includes(id)),'File pagination shows distinct rows without downloading the full catalog');
    const pageBeforeReload=await cdp.evaluate('Array.from(document.querySelectorAll("#file-list tr")).map(x=>x.dataset.entryId)');
    await reloadView('window.MyPan.state.folder==="分页演示" && window.MyPan.state.page===2','restore paged search');
    assert(await cdp.evaluate('window.MyPan.state.pageSize===10 && window.MyPan.state.query==="分页" && !window.MyPan.state.ascending && document.getElementById("search").value==="分页"') && JSON.stringify(pageBeforeReload)===JSON.stringify(await cdp.evaluate('Array.from(document.querySelectorAll("#file-list tr")).map(x=>x.dataset.entryId)')), 'Refresh preserves folder, search, sort, page size, page number and visible file rows');
    await screenshot('files-pagination');
    const deleteResponse=await fetch(url+'/api/entries/'+deletedFixture.id,{method:'DELETE',headers:{...headers,'If-Match':'"'+deletedFixture.version+'"','X-MyPan-Device':Buffer.from('QA-PC-01').toString('base64')}});if(!deleteResponse.ok)throw new Error('Delete metadata fixture failed');
    await click('nav-trash');await input('search','分页22');await cdp.evaluate('document.getElementById("search").dispatchEvent(new Event("input"))');
    await waitDom('window.MyPan.state.query==="分页22" && !window.MyPan.state.refreshing','recycle-bin search applied before refresh');
    await waitDom('document.querySelector("#file-list")?.textContent.includes("QA-PC-01")','persisted deletion device is rendered');
    assert(await cdp.evaluate('document.querySelector("#file-list").textContent.includes("admin")&&document.querySelector("#file-list").textContent.includes("127.0.0.1")'),'Recycle bin displays who deleted the file, reported computer, observed address and deletion time');
    await reloadView('window.MyPan.state.trash && window.MyPan.state.query==="分页22"','restore filtered recycle bin');
    assert(await cdp.evaluate('document.getElementById("nav-trash").classList.contains("selected") && document.getElementById("file-list").textContent.includes("QA-PC-01")'), 'Refreshing the recycle bin preserves its search and deletion metadata');
    await screenshot('trash-provenance');
    await cdp.send('Emulation.setDeviceMetricsOverride',{width:1024,height:600,deviceScaleFactor:1,mobile:false});await screenshot('compact-desktop');
    assert(await cdp.evaluate('document.documentElement.scrollWidth<=innerWidth && document.querySelector(".file-panel").getBoundingClientRect().bottom<=innerHeight'),'Compact desktop fits the available width and height with table scrolling');
    await cdp.send('Emulation.setDeviceMetricsOverride',{width:1440,height:1000,deviceScaleFactor:1,mobile:false});
    await click('logout'); await waitDom('!document.getElementById("login-view").hidden', 'admin logout');
    await cdp.evaluate('document.getElementById("account-mode").checked=true;document.getElementById("account-mode").dispatchEvent(new Event("change"))');
    await input('login-username', 'browseruser'); await input('login-password', 'Web-user-password-0123456789'); await submit('login-form');
    await waitDom('!document.getElementById("app-view").hidden', 'user account login');
    assert(await cdp.evaluate('document.getElementById("nav-admin").hidden && !Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="预览.txt")'), 'Normal account login hides administration and unauthorized root files');
    await cdp.evaluate('history.replaceState(null,"","/#"+new URLSearchParams({account:window.MyPan.state.user.id,view:"settings",folder:"个人/admin"}))');
    await reloadView('window.MyPan.state.view==="files" && window.MyPan.state.folder===""','reject unauthorized saved administrator navigation');
    assert(await cdp.evaluate('document.getElementById("nav-settings").hidden && document.getElementById("settings-dialog").hidden'), 'A regular account cannot restore an administrator page through a saved or forged URL');
    await cdp.evaluate('Array.from(document.querySelectorAll(".folder-name")).find(x=>x.textContent==="客户端资料").click()');
    await waitDom('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="共享.txt")', 'shared files arrive through the paged API');
    assert(await cdp.evaluate('Array.from(document.querySelectorAll(".entry-name")).some(x=>x.textContent==="共享.txt")'), 'User sees files shared with their assigned group'); await screenshot('group-user-files');
    await click('account-security');await input('password-current','Web-user-password-0123456789');await input('password-new','Updated-user-password-0123456789');await input('password-confirm','Updated-user-password-0123456789');await submit('password-form');
    await waitDom('!document.getElementById("login-view").hidden','password change signs out previous sessions');
    await input('login-username','browseruser');await input('login-password','Updated-user-password-0123456789');await submit('login-form');await waitDom('!document.getElementById("app-view").hidden','login with changed password');
    assert(true,'Personal account security changes the password and requires a fresh login');
  }
  await cdp.evaluate('window.__originalFetch=window.fetch;window.fetch=(url,options)=>url==="/api/auth/logout"?Promise.reject(new TypeError("Simulated disconnection")):window.__originalFetch(url,options)');
  await click('logout');await waitDom('document.getElementById("toast").textContent.includes("未能退出登录")','failed logout explained');
  assert(await cdp.evaluate('window.MyPan.state.authenticated && !document.getElementById("app-view").hidden'),'A failed logout is reported without falsely claiming the still-valid session has ended');
  await cdp.evaluate('window.fetch=window.__originalFetch;delete window.__originalFetch');
  const cookies=(await cdp.send('Network.getCookies',{urls:[url]})).cookies;
  const session=cookies.find(x=>x.name==='mypan-session');
  assert(session?.httpOnly,'Authenticated browser session uses an HttpOnly cookie');
  const revoked=await fetch(url+'/api/auth/logout',{method:'POST',headers:{Authorization:'Bearer '+session.value}});assert(revoked.ok,'Server can invalidate the browser session independently of its local UI');
  await reloadView('!window.MyPan.state.authenticated','expired session returns to login',false);
  assert(await cdp.evaluate('document.getElementById("app-view").hidden && location.hash===""'),'Refresh of an invalidated session shows login and clears stale navigation');
  await cdp.evaluate('document.getElementById("account-mode").checked=false;document.getElementById("account-mode").dispatchEvent(new Event("change"))');
  await input('api-key',key);await submit('login-form');await waitDom('!document.getElementById("app-view").hidden','login again before explicit logout');
  await click('logout');
  await waitDom('!document.getElementById("login-view").hidden', 'logout completes');
  await reloadView('!window.MyPan.state.authenticated','logout remains logged out after refresh',false);
  assert(await cdp.evaluate('document.getElementById("app-view").hidden'),'Explicit logout remains logged out after refreshing');
  assert(await cdp.evaluate('!document.getElementById("login-view").hidden && document.getElementById("api-key").value===""'), 'Logout returns to login and clears credentials');
  assert(!serverLog.includes(key), 'Generated key is absent from server logs');
  assert(errors.length === 0, 'Browser has no script or content-security-policy errors');
  await writeFile(path.join(root, 'verification.json'), JSON.stringify({ checks, browser: 'Microsoft Edge (headless)', url, screenshots: ['login-desktop.png','files-desktop.png','files-mobile.png','trash-desktop.png'], defaultKeyFile: defaultKey }, null, 2));
  console.log('ALL PASSED: ' + checks + ' browser checks. Artifacts: ' + root);
} catch (error) {
  console.error(error.message);
  if (errors.length) console.error(errors.join('\n'));
  await writeFile(path.join(root, 'browser.log'), browserLog);
  if (cdp) { try { await screenshot('failure'); } catch {} }
  process.exitCode = 1;
} finally {
  if (cdp) { try { await cdp.send('Browser.close'); } catch {} cdp.ws.close(); }
  await stop(browser); await stop(server);
  await writeFile(path.join(root, 'server.log'), serverLog);
}
