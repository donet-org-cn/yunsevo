'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const state = { key: '', authenticated: false, restoringView: false, adminMode: 'users', entries: [], revision: 0, maxUploadBytes: 0, trash: false, folder: '', query: '', sort: 'name', ascending: true, busy: false, refreshing: false, uploading: false, cancelled: false, xhr: null, epoch: 0, load: 0, page: 1, pageSize: 20, total: 0, folderAccess: 0, view: 'files' };
  let toastTimer, nameEntry;
  const icon = name => {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    const use = document.createElementNS(svg.namespaceURI, 'use');
    use.setAttribute('href', '#i-' + name); svg.append(use); svg.setAttribute('aria-hidden', 'true'); return svg;
  };
  function notify(message) {
    $('toast').textContent = message; $('toast').hidden = false;
    clearTimeout(toastTimer); toastTimer = setTimeout(() => { $('toast').hidden = true; }, 5500);
  }
  function errorText(status, body) {
    if (status === 401) return '登录信息无效或会话已失效，请重新登录。';
    if (status === 413) return '文件超过服务端允许的上传大小。';
    if (status === 412) return '文件已被其他设备修改，请刷新后重试。';
    try { const value = JSON.parse(body); if (value.error) return value.error; } catch { /* Not a JSON error. */ }
    return '操作失败（HTTP ' + status + '），请刷新后重试。';
  }
  async function request(path, options = {}) {
    const epoch = state.epoch;
    let response;
    try { response = await fetch(path, { ...options, headers: { ...(state.key ? { Authorization: 'Bearer ' + state.key } : {}), 'X-MyPan-Client': 'web', ...options.headers } }); }
    catch { throw new Error('无法连接服务端，请检查服务是否启动及网络连接。'); }
    if (epoch !== state.epoch) throw new Error('连接已断开。');
    if (!response.ok) {
      const message = errorText(response.status, await response.text());
      if (response.status === 401) disconnect(message);
      const failure = new Error(message); failure.status = response.status; throw failure;
    }
    return response;
  }
  async function json(path, method, body, headers = {}) {
    const response = await request(path, { method, headers: { ...(body ? { 'Content-Type': 'application/json' } : {}), ...headers }, ...(body ? { body: JSON.stringify(body) } : {}) });
    return response.status === 204 ? null : response.json();
  }
  function disconnect(message = '') {
    state.epoch++; state.load++; state.key = ''; state.authenticated = false; state.user = null; state.entries = []; state.cancelled = true;
    state.refreshing = false;
    if (state.xhr) state.xhr.abort();
    $('session-loading').hidden = true; $('app-view').hidden = true; $('login-view').hidden = false; $('upload-panel').hidden = true;
    history.replaceState(null, '', location.pathname + location.search);
    $('api-key').value = ''; $('login-error').textContent = message; $('login-error').hidden = !message;
    for (const field of document.querySelectorAll('input[type="password"]')) field.value = '';
    for (const dialog of document.querySelectorAll('dialog')) if (dialog.open) dialog.close();
    window.dispatchEvent(new Event('mypan-logout')); ($('account-mode').checked ? $('login-username') : $('api-key')).focus();
  }
  const basename = path => path.split('/').pop();
  const parent = path => path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '';
  const join = (folder, name) => folder ? folder + '/' + name : name;
  const same = (a, b) => a.toLocaleLowerCase() === b.toLocaleLowerCase();
  function size(bytes) {
    if (bytes < 1024) return bytes + ' B';
    const units = ['KB', 'MB', 'GB', 'TB']; let index = -1;
    do { bytes /= 1024; index++; } while (bytes >= 1024 && index < units.length - 1);
    return bytes.toLocaleString('zh-CN', { maximumFractionDigits: 1 }) + ' ' + units[index];
  }
  function render() {
    saveNavigation();
    const trash = state.trash;
    $('page-title').textContent = trash ? '回收站' : '我的文件';
    $('page-description').textContent = trash ? '保留删除时间、操作人和来源。设备名称由客户端上报。' : '让文件井然有序，让每台设备保持连接。';
    document.querySelector('.summary').hidden = trash;
    $('file-actions').hidden = trash;
    if (state.view === 'files') {
      $('nav-files').classList.toggle('selected', !trash); $('nav-trash').classList.toggle('selected', trash);
      $('nav-files').toggleAttribute('aria-current', !trash); $('nav-trash').toggleAttribute('aria-current', trash);
      (trash ? $('nav-trash') : $('nav-files')).setAttribute('aria-current', 'page');
    }
    $('search').placeholder = trash ? '搜索回收站' : '搜索当前目录及子目录';
    $('list-hint').textContent = trash ? '恢复文件夹时会同时恢复其中的文件' : '每 5 秒自动刷新 · 单文件最大 ' + size(state.maxUploadBytes);
    $('copy-directory').hidden = trash || !state.folder;
    const writable = !!(state.folderAccess & 2);
    for (const id of ['new-folder','upload-button','empty-upload']) $(id).disabled = !writable;
    const crumbs = $('breadcrumbs'); crumbs.replaceChildren();
    const addCrumb = (title, folder) => {
      if (crumbs.children.length) { const sep = document.createElement('span'); sep.className = 'breadcrumb-separator'; sep.textContent = '/'; crumbs.append(sep); }
      const b = document.createElement('button'); b.textContent = title; b.addEventListener('click', () => navigate(folder)); crumbs.append(b);
    };
    addCrumb(trash ? '回收站' : '全部文件', '');
    if (!trash && state.folder) {
      let path = ''; for (const part of state.folder.split('/')) { path = join(path, part); addCrumb(part, path); }
    }
    const query = state.query.toLocaleLowerCase();
    const entries = state.entries;
    for (const field of ['name', 'modified', 'size']) $('sort-' + field).textContent = state.sort === (field === 'modified' && trash ? 'deleted' : field) ? (state.ascending ? '↑' : '↓') : '';
    document.querySelectorAll('.trash-column').forEach(x => x.hidden = !trash);
    $('file-list').closest('table').classList.toggle('trash-table', trash);
    $('date-label').textContent = trash ? '删除时间' : '修改时间'; $('date-sort').dataset.sort = trash ? 'deleted' : 'modified';
    const list = $('file-list'); list.replaceChildren();
    for (const entry of entries) {
      const row = document.createElement('tr');
      row.addEventListener('dblclick', event => {
        if (event.target.closest('.row-actions')) return;
        event.preventDefault();
        if (entry.isDirectory) { if (!trash) navigate(entry.path); }
        else window.MyPanExtras?.open(entry, trash);
      });
      row.dataset.entryId = entry.id; row.dataset.version = entry.version;
      const nameCell = document.createElement('td'), cell = document.createElement('div'); cell.className = 'entry-cell';
      const ext = basename(entry.path).split('.').pop().toLowerCase();
      const badge = document.createElement('span'); badge.className = 'file-icon' + (entry.isDirectory ? ' folder' : ['jpg','jpeg','png','gif','webp','mp4','mp3','svg'].includes(ext) ? ' media' : ['json','js','cs','html','css','xml','sql'].includes(ext) ? ' code' : '');
      badge.append(icon(entry.isDirectory ? 'folder' : 'file'));
      const info = document.createElement('div'); info.className = 'entry-info';
      const title = document.createElement(entry.isDirectory && !trash ? 'button' : 'span'); title.className = 'entry-name' + (entry.isDirectory && !trash ? ' folder-name' : ''); title.textContent = basename(entry.path); title.title = entry.path;
      if (entry.isDirectory && !trash) title.addEventListener('click', () => navigate(entry.path));
      const detail = document.createElement('span'); detail.className = 'entry-detail'; detail.textContent = trash || query ? entry.path : entry.isDirectory ? '文件夹' : (ext !== basename(entry.path).toLowerCase() ? ext.toUpperCase() + ' 文件' : '文件'); detail.title = detail.textContent;
      info.append(title, detail); cell.append(badge, info); nameCell.append(cell);
      const dateCell = document.createElement('td'); dateCell.textContent = new Date(trash && entry.deleted ? entry.deleted.time : entry.modifiedUtc).toLocaleString('zh-CN', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false });
      if (trash && !entry.deleted) dateCell.textContent = '历史记录未留存';
      const sizeCell = document.createElement('td'); sizeCell.textContent = entry.isDirectory ? '—' : size(entry.size);
      const actionCell = document.createElement('td'), actions = document.createElement('div'); actions.className = 'row-actions';
      const action = (label, handler, danger = false) => { const button = document.createElement('button'); button.textContent = label; button.classList.toggle('danger', danger); button.addEventListener('click', () => handler(entry)); actions.append(button); };
      if (trash) { if (!entry.isDirectory) action('下载', item => download(item, true)); if ((entry.access & 2) !== 0) action('恢复', restore); }
      else { if (!entry.isDirectory) action('下载', download); if ((entry.access & 2) !== 0) { action('重命名', rename); action('删除', remove, true); } }
      window.MyPanExtras?.actions(entry, actions, trash);
      actionCell.append(actions); row.append(nameCell, dateCell, sizeCell);
      if (trash) {
        for (const text of [entry.deleted ? entry.deleted.displayName + ' · ' + entry.deleted.username : '历史记录未留存', entry.deleted?.deviceName || (entry.deleted?.source === 'Web' ? '网页浏览器' : '未上报'), entry.deleted ? entry.deleted.source + '\n' + (entry.deleted.remoteAddress || '未留存 IP') : '未留存']) { const td = document.createElement('td'); td.textContent = text; td.title = text; td.className='origin-cell'; row.append(td); }
      }
      row.append(actionCell); list.append(row);
    }
    $('visible-count').textContent = state.total.toLocaleString('zh-CN') + ' 个项目' + (query ? ' · 搜索结果' : '');
    $('empty-state').hidden = entries.length > 0;
    $('empty-title').textContent = query ? '没有找到匹配的文件' : trash ? '回收站是空的' : '这里还是空的';
    $('empty-description').textContent = query ? '试试其他文件名，或清除搜索条件。' : trash ? '删除的文件会保留在这里，方便随时恢复。' : '上传第一个文件，或将文件拖到这里。';
    $('empty-upload').hidden = trash || !!query;
    pager($('files-pager'), { page: state.page, pageSize: state.pageSize, total: state.total }, async (page, count) => { state.page=page; state.pageSize=count; await refresh(); });
  }
  function connection(online) {
    for (const element of document.querySelectorAll('.connection-label,.connected')) {
      const dot = document.createElement('span'); dot.className = 'status-dot';
      element.replaceChildren(dot, document.createTextNode(online ? '已连接' : '连接中断，正在重试'));
      element.classList.toggle('offline', !online);
    }
  }
  async function refresh(background = false) {
    const epoch = state.epoch, load = ++state.load, trash = state.trash;
    state.refreshing = true;
    if (!background) $('loading').hidden = false;
    try {
      const parameters = new URLSearchParams({folder:state.folder,query:state.query,trash:String(trash),sort:state.sort,descending:String(!state.ascending),page:String(state.page),pageSize:String(state.pageSize)});
      const catalog = await json('/api/browser/entries?' + parameters, 'GET');
      if (epoch !== state.epoch || load !== state.load) return;
      connection(true);
      if (background && catalog.revision === state.revision) return;
      state.entries = catalog.items.map(x => ({...x.entry, deleted:x.deleted})); state.revision = catalog.revision; state.page=catalog.page; state.total=catalog.total; state.folderAccess=catalog.folderAccess;

      if (!trash) {
        const files = state.entries.filter(x => !x.isDirectory);
        $('total-files').textContent = catalog.totalFiles.toLocaleString('zh-CN'); $('file-count').textContent = catalog.totalFiles.toLocaleString('zh-CN');
        $('total-size').textContent = size(catalog.totalBytes);
      }
      render();
    } catch (e) {
      if (e.status === 404 && state.folder) { state.folder=''; state.page=1; await refresh(); return; }
      if (epoch === state.epoch && state.authenticated) connection(false);
      throw e;
    } finally { if (load === state.load) { $('loading').hidden = true; state.refreshing = false; } }
  }
  async function autoRefresh() {
    if (state.view !== 'files' || !state.authenticated || $('app-view').hidden || document.hidden || state.refreshing || state.uploading || state.busy || document.querySelector('dialog[open]')) return;
    try { await refresh(true); } catch { /* Connection status is visible; retry without repeating toasts. */ }
  }
  setInterval(autoRefresh, 5000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) autoRefresh(); });
  window.addEventListener('focus', autoRefresh);
  function navigate(folder) { state.folderAccess=folder?(state.entries.find(x=>x.isDirectory&&same(x.path,folder))?.access??0):(state.user?.isAdmin?7:8);state.folder=folder;state.query='';state.page=1;state.entries=[];state.total=0;$('search').value='';render();refresh().catch(e=>notify(e.message)); }
  async function switchView(trash) {
    showPage('files'); state.page=1; state.sort=trash?'deleted':'name'; state.ascending=!trash; state.trash = trash; state.folder = ''; state.query = ''; $('search').value = ''; state.entries = []; render();
    try { await refresh(); } catch (e) { notify(e.message); }
  }
  async function mutate(operation) {
    if (state.busy) return;
    state.busy = true;
    try { await operation(); await refresh(); } catch (e) { notify(e.message); }
    finally { state.busy = false; }
  }
  async function remove(entry) {
    if (!confirm('将“' + basename(entry.path) + '”移入回收站？' + (entry.isDirectory ? '\n其中的所有文件也会一起移入回收站。' : ''))) return;
    await mutate(async () => { await json('/api/entries/' + entry.id, 'DELETE', null, { 'If-Match': '"' + entry.version + '"', 'X-Catalog-Revision': String(state.revision) }); notify('已移入回收站'); });
  }
  async function restore(entry) {
    await mutate(async () => { await json('/api/trash/' + entry.id + '/restore', 'POST'); notify('已恢复到原来的位置'); });
  }
  async function download(entry, trash = false) {
    try {
      let destination = null;
      if (typeof window.showSaveFilePicker === 'function') {
        try { destination = await window.showSaveFilePicker({ suggestedName: basename(entry.path) }); }
        catch (e) { if (e.name === 'AbortError') return; throw e; }
      }
      const response = await request((trash ? '/api/trash/' + entry.id + '/content' : '/api/content/' + entry.id) + '?version=' + encodeURIComponent(entry.version));
      if (destination) { const writable = await destination.createWritable(); await response.body.pipeTo(writable); }
      else {
        const blob = await response.blob(), url = URL.createObjectURL(blob), link = document.createElement('a');
        link.href = url; link.download = basename(entry.path); document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 60000);
      }
      notify('下载完成：' + basename(entry.path));
    } catch (e) { notify(e.message); }
  }
  function rename(entry) { nameEntry = entry; $('name-title').textContent = '重命名'; $('entry-name').value = basename(entry.path); openNameDialog(); }
  function openNameDialog() { $('name-error').hidden = true; $('name-dialog').showModal(); $('entry-name').focus(); $('entry-name').select(); }
  $('new-folder').addEventListener('click', () => { nameEntry = null; $('name-title').textContent = '新建文件夹'; $('entry-name').value = ''; openNameDialog(); });
  $('name-cancel').addEventListener('click', () => $('name-dialog').close());
  $('name-form').addEventListener('submit', async event => {
    event.preventDefault(); const name = $('entry-name').value;
    if (!name.trim() || /[\\/]/.test(name)) { $('name-error').textContent = '请输入名称，且不能包含路径分隔符。'; $('name-error').hidden = false; return; }
    $('name-submit').disabled = true; const epoch = state.epoch;
    try {
      if (nameEntry) await json('/api/entries/' + nameEntry.id, 'PATCH', { path: join(parent(nameEntry.path), name), version: nameEntry.version });
      else await json('/api/folders', 'POST', { path: join(state.folder, name) });
      if (epoch !== state.epoch) return;
      $('name-dialog').close(); notify(nameEntry ? '重命名成功' : '文件夹已创建'); await refresh();
    } catch (e) { $('name-error').textContent = e.message; $('name-error').hidden = false; }
    finally { $('name-submit').disabled = false; }
  });
  function putFile(file, path, existing, progress, message, epoch) {
    return new Promise((resolve, reject) => {
      const xhr = new XMLHttpRequest(); state.xhr = xhr;
      xhr.open('PUT', '/api/content?path=' + encodeURIComponent(path));
      if (state.key) xhr.setRequestHeader('Authorization', 'Bearer ' + state.key);
      xhr.setRequestHeader('X-MyPan-Client', 'web');
      xhr.setRequestHeader(existing ? 'If-Match' : 'If-None-Match', existing ? '"' + existing.version + '"' : '*');
      xhr.setRequestHeader('Content-Type', 'application/octet-stream');
      xhr.upload.onprogress = event => {
        if (epoch !== state.epoch) return;
        progress.value = event.lengthComputable ? event.loaded / event.total * 100 : 0;
        message.textContent = event.lengthComputable ? Math.floor(progress.value) + '% · ' + size(event.loaded) + ' / ' + size(file.size) : '上传中…';
      };
      xhr.onload = () => {
        if (epoch !== state.epoch) { reject(new Error('连接已断开。')); return; }
        if (xhr.status >= 200 && xhr.status < 300) { progress.value = 100; resolve(JSON.parse(xhr.responseText)); }
        else { const text = errorText(xhr.status, xhr.responseText); if (xhr.status === 401) disconnect(text); reject(new Error(text)); }
      };
      xhr.onerror = () => reject(new Error('网络连接中断，请重新上传。'));
      xhr.onabort = () => reject(new Error('已取消'));
      xhr.send(file);
    });
  }
  async function upload(files) {
    if (!files.length || state.trash || !state.authenticated) return;
    if (state.uploading) { notify('当前有上传任务，请等待完成后再添加文件。'); return; }
    state.uploading = true; state.cancelled = false;
    const folder = state.folder, epoch = state.epoch;
    let succeeded = 0, failed = 0, skipped = 0;
    $('upload-panel').hidden = false; $('close-uploads').hidden = true; $('cancel-upload').hidden = false;
    $('upload-title').textContent = '正在准备上传…'; $('upload-list').replaceChildren();
    const jobs = Array.from(files).map(file => {
      const row = document.createElement('li'), title = document.createElement('span'), message = document.createElement('span'), progress = document.createElement('progress');
      title.className = 'upload-name'; title.textContent = file.name; message.className = 'upload-message'; message.textContent = '等待上传 · ' + size(file.size); progress.max = 100; progress.value = 0;
      row.append(title, message, progress); $('upload-list').append(row); return { file, message, progress };
    });
    try {
      // Read fresh versions before resolving collisions; conditional PUT still guards later races.
      state.maxUploadBytes = (await json('/api/capabilities', 'GET')).maxUploadBytes;
      for (let i = 0; i < jobs.length; i++) {
        const { file, message, progress } = jobs[i];
        if (state.cancelled || epoch !== state.epoch) { message.textContent = '已取消'; skipped++; continue; }
        $('upload-title').textContent = '正在上传 ' + (i + 1) + ' / ' + jobs.length;
        if (file.size > state.maxUploadBytes) { message.textContent = '文件超过服务端允许的上传大小（' + size(state.maxUploadBytes) + '）。'; message.classList.add('failed'); failed++; continue; }
        const path = join(folder, file.name);
        try {
          const existing = (await json('/api/browser/entry?path=' + encodeURIComponent(path), 'GET')).entry;
          if (existing && (existing.isDirectory || !confirm('“' + file.name + '”已存在，是否覆盖云端文件？\n取消可跳过此文件。'))) { message.textContent = '已跳过：同名项目已存在'; skipped++; continue; }
          const uploaded = await putFile(file, path, existing, progress, message, epoch);
          message.textContent = '上传完成 · ' + size(file.size); succeeded++;
          // Preserve conditional versions for repeated names in the same batch.
        } catch (e) { message.textContent = e.message; message.classList.add('failed'); failed++; }
      }
    } catch (e) {
      notify(e.message);
      for (const job of jobs) if (job.message.textContent.startsWith('等待')) { job.message.textContent = e.message; job.message.classList.add('failed'); failed++; }
    } finally {
      state.xhr = null; state.uploading = false;
      if (epoch === state.epoch) {
        $('upload-title').textContent = '上传结束 · ' + succeeded + ' 成功' + (failed ? ' · ' + failed + ' 失败' : '') + (skipped ? ' · ' + skipped + ' 跳过' : '');
        $('cancel-upload').hidden = true; $('close-uploads').hidden = false;
        try { await refresh(); } catch (e) { notify(e.message); }
      }
    }
  }
  $('login-form').addEventListener('submit', async event => {
    event.preventDefault(); $('login-button').disabled = true; $('login-error').hidden = true;
    state.key = $('api-key').value.trim(); state.epoch++; state.page=1; showPage('files'); state.trash = false; state.folder = ''; state.query = ''; $('search').value = '';
    try {
      if ($('account-mode').checked) {
        const response = await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username: $('login-username').value.trim(), password: $('login-password').value }) });
        if (!response.ok) throw new Error(errorText(response.status, await response.text()));
        const result = await response.json(); state.key = result.accessToken;
      } else { const result = await json('/api/auth/session', 'POST'); state.key = result.accessToken; }
      await openWorkspace(await json('/api/me', 'GET'), false);
    }
    catch (e) { disconnect(e.message); }
    finally { $('login-button').disabled = false; }
  });
  async function logout() { try { await json('/api/auth/logout', 'POST'); disconnect(); } catch (e) { if(e.status!==401)notify('未能退出登录，请检查网络后重试。'); } }
  $('logout').addEventListener('click', logout);
  $('logout-top').addEventListener('click', logout);
  $('nav-files').addEventListener('click', () => switchView(false)); $('nav-trash').addEventListener('click', () => switchView(true));
  $('refresh').addEventListener('click', async () => { try { await refresh(); notify('文件列表已刷新'); } catch (e) { notify(e.message); } });
  $('copy-directory').addEventListener('click', async () => {
    try { await navigator.clipboard.writeText(state.folder); notify('云端目录名已复制，请粘贴到客户端“云端目录”中'); }
    catch { notify('当前云端目录：' + state.folder); }
  });
  let searchTimer; $('search').addEventListener('input', () => { clearTimeout(searchTimer); searchTimer=setTimeout(()=>{state.query=$('search').value.trim();state.page=1;refresh().catch(e=>notify(e.message));},200); });
  for (const button of document.querySelectorAll('[data-sort]')) button.addEventListener('click', () => { const field = button.dataset.sort; state.ascending = state.sort === field ? !state.ascending : true; state.sort = field; state.page=1; refresh().catch(e=>notify(e.message)); });
  for (const id of ['upload-button', 'empty-upload']) $(id).addEventListener('click', () => $('file-input').click());
  $('file-input').addEventListener('change', () => { const files = Array.from($('file-input').files); $('file-input').value = ''; upload(files); });
  $('cancel-upload').addEventListener('click', () => { state.cancelled = true; if (state.xhr) state.xhr.abort(); });
  $('close-uploads').addEventListener('click', () => { $('upload-panel').hidden = true; });
  let dragCount = 0;
  document.addEventListener('dragover', event => { if (event.dataTransfer.types.includes('Files')) event.preventDefault(); });
  document.addEventListener('drop', event => { if (event.dataTransfer.types.includes('Files')) event.preventDefault(); });
  $('drop-area').addEventListener('dragenter', event => { if (state.trash || !state.authenticated || !event.dataTransfer.types.includes('Files')) return; event.preventDefault(); dragCount++; $('drop-overlay').hidden = false; });
  $('drop-area').addEventListener('dragleave', () => { dragCount = Math.max(0, dragCount - 1); if (!dragCount) $('drop-overlay').hidden = true; });
  $('drop-area').addEventListener('drop', event => {
    event.preventDefault(); dragCount = 0; $('drop-overlay').hidden = true;
    const items = Array.from(event.dataTransfer.items).filter(item => item.kind === 'file');
    const directories = items.filter(item => item.webkitGetAsEntry?.()?.isDirectory);
    if (directories.length) notify('文件夹请先在网页中创建，再上传里面的文件。');
    const files = items.length ? items.filter(item => !item.webkitGetAsEntry?.()?.isDirectory).map(item => item.getAsFile()).filter(Boolean) : Array.from(event.dataTransfer.files);
    upload(files);
  });
  $('account-mode').addEventListener('change', () => {
    const accountMode = $('account-mode').checked; $('account-fields').hidden = !accountMode; $('key-fields').hidden = accountMode;
    $('api-key').required = !accountMode; $('login-username').required = accountMode; $('login-password').required = accountMode;
  });
  $('account-mode').dispatchEvent(new Event('change'));
  function pager(host, value, changed) {
    host.replaceChildren(); const pages=Math.max(1,Math.ceil(value.total/value.pageSize));
    const label=document.createElement('span'); label.textContent=`第 ${value.page} / ${pages} 页`;
    for (const [title,delta] of [['上一页',-1],['下一页',1]]) { const b=document.createElement('button');b.type='button';b.className='secondary';b.textContent=title;b.disabled=delta<0?value.page<=1:value.page>=pages;b.addEventListener('click',()=>changed(value.page+delta,value.pageSize).catch(e=>notify(e.message)));host.append(b); }
    host.insertBefore(label,host.firstChild);
    const select=document.createElement('select');select.setAttribute('aria-label','每页数量');for(const n of [10,20,50,100]){const o=document.createElement('option');o.value=n;o.textContent=n+' 条/页';o.selected=n===value.pageSize;select.append(o);}select.addEventListener('change',()=>changed(1,Number(select.value)).catch(e=>notify(e.message)));host.append(select);
  }
  function showPage(id) {
    state.view=id; $('drop-area').hidden=id!=='files'; $('management-view').hidden=id==='files';
    for (const name of ['admin-dialog','settings-dialog','audit-dialog']) $(name).hidden=name!==id;
    document.querySelectorAll('.nav-item').forEach(x=>{x.classList.remove('selected');x.removeAttribute('aria-current');});
    const nav=id==='files'?(state.trash?'nav-trash':'nav-files'):id==='admin-dialog'?(state.adminMode==='groups'?'nav-groups':'nav-admin'):id==='settings-dialog'?'nav-settings':'nav-audit';
    $(nav).classList.add('selected');$(nav).setAttribute('aria-current','page');document.querySelector('.sidebar').classList.remove('open');
    saveNavigation();
  }
  // Navigation lives in this tab's URL. Passwords and session tokens never enter URL or web storage.
  function saveNavigation() {
    if (!state.authenticated || !state.user || state.restoringView) return;
    const view=state.view==='files'?(state.trash?'trash':'files'):state.view==='admin-dialog'?state.adminMode:state.view==='settings-dialog'?'settings':'audit';
    const values={account:state.user.id,view};
    if(state.view==='files')Object.assign(values,{folder:state.trash?'':state.folder,query:state.query,sort:state.sort,ascending:String(state.ascending),page:String(state.page),pageSize:String(state.pageSize)});
    else Object.assign(values,window.MyPanExtras?.navigation?.()??{});
    history.replaceState(null,'',location.pathname+location.search+'#'+new URLSearchParams(values));
  }
  function savedNavigation(user) {
    const values=new URLSearchParams(location.hash.slice(1));
    if(values.get('account')!==user.id)return null;
    const view=values.get('view');if(!['files','trash','users','groups','settings','audit'].includes(view)||!user.isAdmin&&!['files','trash'].includes(view))return null;
    const integer=(key,fallback,max)=>{const number=Number(values.get(key));return Number.isInteger(number)&&number>=1&&number<=max?number:fallback;};
    return {view,folder:(values.get('folder')??'').slice(0,2000),query:(values.get('query')??'').slice(0,2000),sort:['name','size','modified','deleted'].includes(values.get('sort'))?values.get('sort'):'name',ascending:values.get('ascending')!=='false',page:integer('page',1,1000000),pageSize:[10,20,50,100].includes(Number(values.get('pageSize')))?Number(values.get('pageSize')):20,userFilter:(values.get('userFilter')??'').slice(0,64),kindFilter:['all','trash','accounts','settings'].includes(values.get('kindFilter'))?values.get('kindFilter'):'all'};
  }
  async function openWorkspace(user, restore) {
    const route=restore?savedNavigation(user):null;
    let opened=false;
    state.restoringView=true; state.user=user; state.authenticated=true;
    try {
      $('account-display').textContent=user.displayName; document.querySelector('.avatar').textContent=user.displayName.slice(0,1);
      state.maxUploadBytes=(await json('/api/capabilities','GET')).maxUploadBytes;
      state.trash=route?.view==='trash'; state.folder=route?.view==='files'?route.folder:''; state.query=route?.query??'';state.page=route?.page??1;state.pageSize=route?.pageSize??20;state.sort=route?.sort??'name';state.ascending=route?.ascending??true;$('search').value=state.query;
      window.dispatchEvent(new Event('mypan-login'));
      if(route&&!['files','trash'].includes(route.view))await window.MyPanExtras.restoreNavigation(route);
      else {showPage('files');await refresh();}
      if(!state.authenticated)return;
      $('login-view').hidden=true;$('app-view').hidden=false;$('session-loading').hidden=true;$('api-key').value='';$('login-password').value='';
      opened=true;
    } finally {state.restoringView=false;if(opened)saveNavigation();}
  }
  async function restoreSession() {
    $('session-loading').hidden=false;$('session-retry').hidden=true;$('session-message').textContent='正在恢复你的文件空间…';$('login-view').hidden=true;
    try {
      const response=await fetch('/api/me',{headers:{'X-MyPan-Client':'web'}});
      if(response.status===401){disconnect();return;}
      if(!response.ok)throw new Error('暂时无法恢复会话，请重试。');
      await openWorkspace(await response.json(),true);
    } catch(e) {
      if(e.status===401)return;
      $('app-view').hidden=true;$('session-loading').hidden=false;$('session-message').textContent='无法恢复页面，请检查网络后重试。';$('session-retry').hidden=false;
    }
  }
  $('session-retry').addEventListener('click',restoreSession);
  $('menu-toggle').addEventListener('click',()=>document.querySelector('.sidebar').classList.toggle('open'));
  window.MyPan = { state, request, json, notify, refresh, disconnect, size, pager, showPage, download, saveNavigation };
  document.addEventListener('DOMContentLoaded',restoreSession,{once:true});
})();
