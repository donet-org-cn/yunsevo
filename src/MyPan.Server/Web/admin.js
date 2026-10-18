'use strict';
(() => {
  const { state, request, json, notify, refresh, size, pager, showPage, disconnect, download, saveNavigation } = window.MyPan;
  const $ = id => document.getElementById(id);
  let users = [], groups = [], editedUser = null, editedGroup = null, editedFolder = null, eventSource = null;
  const error = (id, message) => { $(id).textContent = message; $(id).hidden = !message; };
  const button = (text, callback, className = 'text-button') => { const b = document.createElement('button'); b.type = 'button'; b.className = className; b.textContent = text; b.addEventListener('click', async () => { try { await callback(); } catch (e) { notify(e.message); } }); return b; };
  function option(select, value, label) { const o = document.createElement('option'); o.value = value; o.textContent = label; select.append(o); return o; }
  async function principals() { [users, groups] = await Promise.all([json('/api/principals/users', 'GET'), json('/api/principals/groups', 'GET')]); }
  function newUser() { editedUser = null; $('user-form').reset(); $('user-name').disabled = false; $('user-password').required = true; $('user-form-title').textContent = '新建用户'; }
  function newGroup() { editedGroup = null; $('group-form').reset(); }
  let adminMode='users';
  const pages={users:{page:1,size:20},groups:{page:1,size:20},audit:{page:1,size:20},versions:{page:1,size:20}};
  let resetUser=null;
  async function loadAdmin() {
    const current=pages[adminMode], params=new URLSearchParams({query:$(adminMode+'-query').value.trim(),page:current.page,pageSize:current.size});
    const result=await json('/api/admin/'+adminMode+'/page?'+params,'GET'), host=$(adminMode+'-list');host.replaceChildren();current.page=result.page;
    for(const item of result.items){
      const row=document.createElement('div');row.className='management-row';const name=document.createElement('span');
      if(adminMode==='users'){
        name.textContent=item.displayName+' · '+item.username+(item.isAdmin?' · 管理员':' · 成员')+(item.disabled?' · 已停用':'');
        row.append(name,button('编辑',()=>{editedUser=item;$('user-name').value=item.username;$('user-name').disabled=true;$('user-display').value=item.displayName;$('user-password').value='';$('user-password').required=false;$('user-admin').checked=item.isAdmin;$('user-disabled').checked=item.disabled;$('user-form-title').textContent='编辑用户';}),
          button('重置密码',()=>{resetUser=item;$('reset-form').reset();$('reset-form').hidden=false;$('reset-result').hidden=true;$('reset-target').textContent='正在重置 '+item.displayName+'（'+item.username+'）的登录密码';error('reset-error','');$('reset-dialog').showModal();}),
          button('SMB 密码',async()=>{if(!confirm('重置此用户的 SMB 专用密码？旧连接密码会失效。'))return;const result=await json('/api/admin/users/'+item.id+'/smb-password','POST');$('smb-password-user').textContent=result.username;$('smb-password-value').value=result.password;$('smb-password-dialog').showModal();}));
      }else{
        name.textContent=item.name+' · '+item.members.length+' 位成员';
        row.append(name,button('编辑',()=>{editedGroup=item;$('group-name').value=item.name;for(const o of $('group-members').options)o.selected=item.members.includes(o.value);}),button('删除',async()=>{if(!confirm('删除用户组及相关授权？'))return;await json('/api/admin/groups/'+item.id,'DELETE');await loadAdmin();newGroup();}));
      }
      host.append(row);
    }
    if(!result.items.length){const empty=document.createElement('p');empty.className='muted';empty.textContent='没有匹配的记录';host.append(empty);}
    pager($(adminMode+'-pager'),result,async(page,size)=>{current.page=page;current.size=size;await loadAdmin();});
    saveNavigation();
  }
  async function administration(mode){try{adminMode=mode;state.adminMode=mode;await loadAdmin();newUser();newGroup();error('admin-error','');$('admin-title').textContent=mode==='users'?'用户管理':'用户组管理';$('users-section').hidden=mode!=='users';$('groups-section').hidden=mode!=='groups';if(mode==='groups'){const principals=await json('/api/principals/users','GET');$('group-members').replaceChildren();for(const user of principals)option($('group-members'),user.id,user.displayName+' ('+user.username+')');}showPage('admin-dialog');return true;}catch(e){notify(e.message);return false;}}
  $('nav-admin').addEventListener('click',()=>administration('users'));$('nav-groups').addEventListener('click',()=>administration('groups'));
  $('create-user').addEventListener('click',newUser);$('create-group').addEventListener('click',newGroup);
  for(const mode of ['users','groups']){let timer;$(mode+'-query').addEventListener('input',()=>{clearTimeout(timer);timer=setTimeout(()=>{pages[mode].page=1;loadAdmin().catch(e=>notify(e.message));},200);});}
  $('reset-form').addEventListener('submit',async event=>{event.preventDefault();const submit=event.submitter??event.currentTarget.querySelector('button[type="submit"]');submit.disabled=true;try{const result=await json('/api/admin/users/'+resetUser.id+'/reset-password','POST',{password:$('reset-password').value||null});$('reset-password').value='';$('reset-form').hidden=true;$('reset-result').hidden=false;$('reset-result-value').value=result.password;notify('密码已重置，原有会话已注销');}catch(e){error('reset-error',e.message);}finally{submit.disabled=false;}});
  $('reset-dialog').addEventListener('close',()=>{$('reset-result-value').value='';$('reset-password').value='';if(resetUser?.id===state.user?.id&&$('reset-form').hidden)disconnect('密码已重置，请使用新密码登录。');});
  $('account-security').addEventListener('click',()=>{$('password-form').reset();error('password-error','');$('password-dialog').showModal();});
  $('password-form').addEventListener('submit',async event=>{event.preventDefault();const submit=event.submitter??event.currentTarget.querySelector('button[type="submit"]');submit.disabled=true;try{if($('password-new').value!==$('password-confirm').value)throw new Error('两次新密码不一致。');await json('/api/auth/password','POST',{currentPassword:$('password-current').value,newPassword:$('password-new').value});$('password-form').reset();$('password-dialog').close();disconnect('密码已修改，请重新登录。');}catch(e){error('password-error',e.message);}finally{submit.disabled=false;}});
  async function loadSettings() {
    const environment = await json('/api/admin/environment-settings', 'GET');
    $('environment-enabled').checked = environment.enabled;
    for(const [id,key] of [['address','addressProvider'],['weather','weatherProvider'],['photon','photonServer'],['contact','contact'],['cache','cacheMinutes']]) $('environment-'+id).value = environment[key];
    $('environment-key').value = ''; $('environment-clear-key').checked = false;
    $('environment-key-status').textContent = environment.hasAmapKey ? '已配置 · Key 不回传到浏览器' : '尚未配置';
    const result = await json('/api/admin/settings', 'GET'), config = result.settings;
    for (const [id, key] of [['setting-webdav','webDav'],['setting-ftp','ftp'],['setting-smb','smb']]) $(id).checked = config[key];
    $('setting-address').value = config.listenAddress; $('setting-ftp-port').value = config.ftpPort; $('setting-smb-port').value = config.smbPort;
    $('protocol-status').textContent = 'WebDAV：' + (config.webDav ? '已启用' : '关闭') + ' · FTP：' + (result.ftpRunning ? '运行中' : '关闭') + ' · SMB：' + (result.smbRunning ? '运行中' : '关闭') + (result.error ? ' · ' + result.error : '');
  }
  $('nav-settings').addEventListener('click', async () => { try { await loadSettings(); error('settings-error',''); showPage('settings-dialog'); } catch(e) { notify(e.message); } });
  $('environment-form').addEventListener('submit', async event => {
    event.preventDefault(); const submit=event.submitter ?? event.currentTarget.querySelector('button[type="submit"]'); submit.disabled=true; error('environment-error','');
    try {
      await json('/api/admin/environment-settings','PUT',{enabled:$('environment-enabled').checked,addressProvider:$('environment-address').value,weatherProvider:$('environment-weather').value,
        photonServer:$('environment-photon').value.trim(),contact:$('environment-contact').value.trim(),cacheMinutes:Number($('environment-cache').value),
        amapKey:$('environment-key').value.trim()||null,clearAmapKey:$('environment-clear-key').checked});
      await loadSettings(); notify('地址与天气配置已保存');
    }catch(e){error('environment-error',e.message);}finally{submit.disabled=false;}
  });
  $('settings-form').addEventListener('submit', async event => { event.preventDefault(); const submit = event.submitter ?? event.currentTarget.querySelector('button[type="submit"]'); submit.disabled = true; error('settings-error',''); try { await json('/api/admin/settings', 'PUT', { webDav: $('setting-webdav').checked, ftp: $('setting-ftp').checked, smb: $('setting-smb').checked, listenAddress: $('setting-address').value.trim(), ftpPort: Number($('setting-ftp-port').value), smbPort: Number($('setting-smb-port').value) }); await loadSettings(); notify('协议配置已保存并生效'); } catch(e) { error('settings-error',e.message); } finally { submit.disabled = false; } });
  async function loadAudit() {
    const result=await json('/api/admin/audit/page?'+new URLSearchParams({user:$('audit-user').value.trim(),kind:$('audit-kind').value,page:pages.audit.page,pageSize:pages.audit.size}),'GET'), selected=result.items; pages.audit.page=result.page;
    $('audit-list').replaceChildren();
    function describe(item) {
      const path = item.target, method = item.action;
      if (path.includes('/reset-password')) return '重置登录密码';
      if (path.includes('/auth/password')) return '修改登录密码';
      if (path.includes('/smb-password')) return '重置 SMB 连接密码';
      if (path.includes('/auth/login') || method === 'LOGIN') return '登录';
      if (path.includes('/auth/logout')) return '退出登录';
      if (path.includes('/admin/users')) return method === 'POST' ? '创建用户' : '更新用户';
      if (path.includes('/admin/groups')) return method === 'DELETE' ? '删除用户组' : method === 'POST' ? '创建用户组' : '更新用户组';
      if (path.includes('/settings')) return '修改服务设置';
      if (path.includes('/policy')) return method === 'DELETE' ? '恢复目录继承' : '修改权限 / 容量 / 保留策略';
      if (path.includes('/versions/') && path.endsWith('/restore')) return '恢复历史版本';
      if (path.includes('/trash/')) return method === 'DELETE' ? '永久删除' : '从回收站恢复';
      if (method === 'DELETE' || ['DELE','RMD'].includes(method)) return '移入回收站';
      if (method === 'PATCH' || method === 'MOVE') return '移动 / 重命名';
      if (path === '/api/folders' || method === 'MKD' || method === 'MKCOL') return '新建文件夹';
      if (method === 'PUT' || method === 'STOR') return '上传 / 更新文件';
      return method;
    }
    for (const item of selected) { const row = document.createElement('tr'), object = item.detail || (item.target.includes(' · ') ? item.target.split(' · ').slice(1).join(' · ') : item.target), result = item.status >= 200 && item.status < 300 ? '成功' : item.status === 499 ? '已取消' : '失败（' + item.status + '）';
      for(const text of [new Date(item.time).toLocaleString('zh-CN') + ' · ' + item.user, describe(item) + ' · ' + object, result + ' · ' + item.source + (item.deviceName ? ' · '+item.deviceName : '')]) { const cell = document.createElement('td'); cell.textContent = text; cell.title = item.action + ' ' + item.target; row.append(cell); } $('audit-list').append(row); }
    $('audit-count').textContent='共 '+result.total+' 条记录';pager($('audit-pager'),result,async(page,size)=>{pages.audit.page=page;pages.audit.size=size;await loadAudit();});
    saveNavigation();
  }
  $('nav-audit').addEventListener('click', async () => { try { await loadAudit(); showPage('audit-dialog'); } catch(e) { notify(e.message); } });
  $('audit-refresh').addEventListener('click', () => loadAudit().catch(e=>notify(e.message))); $('audit-kind').addEventListener('change',()=>{pages.audit.page=1;loadAudit().catch(e=>notify(e.message));});
  $('smb-password-dialog').addEventListener('close', () => { $('smb-password-value').value = ''; });
  $('new-user').addEventListener('click', newUser); $('new-group').addEventListener('click', newGroup);
  $('user-form').addEventListener('submit', async event => {
    event.preventDefault(); error('admin-error', '');
    try {
      await json('/api/admin/users' + (editedUser ? '/' + editedUser.id : ''), editedUser ? 'PUT' : 'POST', { username: $('user-name').value.trim(), displayName: $('user-display').value.trim(), password: $('user-password').value || null, isAdmin: $('user-admin').checked, disabled: $('user-disabled').checked });
      await loadAdmin(); newUser(); notify('用户已保存；密码修改或停用会注销其现有会话');
    } catch (e) { error('admin-error', e.message); }
  });
  $('group-form').addEventListener('submit', async event => {
    event.preventDefault(); error('admin-error', '');
    try { await json('/api/admin/groups' + (editedGroup ? '/' + editedGroup.id : ''), editedGroup ? 'PUT' : 'POST', { name: $('group-name').value.trim(), members: Array.from($('group-members').selectedOptions).map(x => x.value) }); await loadAdmin(); newGroup(); notify('用户组已保存，授权立即生效'); }
    catch (e) { error('admin-error', e.message); }
  });
  function addGrant(grant = null) {
    const row = document.createElement('div'); row.className = 'grant-row';
    const who = document.createElement('select'); who.className = 'grant-subject'; who.required = true;
    for (const user of users.filter(x => !x.disabled)) option(who, 'user:' + user.id, '用户 · ' + user.displayName + ' (' + user.username + ')');
    for (const group of groups) option(who, 'group:' + group.id, '用户组 · ' + group.name);
    if (grant) who.value = grant.kind + ':' + grant.id;
    const rights = document.createElement('select'); rights.className = 'grant-rights'; option(rights, '1', '只读'); option(rights, '3', '读写'); option(rights, '7', '读写及管理'); rights.value = String(grant?.access ?? 1);
    row.append(who, rights, button('移除', () => row.remove())); $('grant-list').append(row);
  }
  $('add-grant').addEventListener('click', () => addGrant());
  async function policy(entry) {
    await principals(); editedFolder = entry;
    const info = await json('/api/folders/' + entry.id + '/policy', 'GET'), rule = info.policy;
    $('policy-title').textContent = '文件夹设置 · ' + entry.path; $('policy-owner').replaceChildren();
    for (const user of users) option($('policy-owner'), user.id, user.displayName + ' (' + user.username + ')');
    $('policy-owner').value = rule.ownerId; $('policy-owner').disabled = !state.user.isAdmin;
    $('policy-visibility').value = rule.visibility; $('policy-public').value = String(rule.publicAccess || 1);
    const personal = /^个人\/[^/]+$/i.test(entry.path);
    $('policy-owner').disabled ||= personal; $('policy-visibility').disabled = personal; $('policy-public').disabled = personal; $('add-grant').disabled = personal; $('inherit-policy').disabled = personal;
    $('policy-quota').value = rule.quotaBytes == null ? '' : String(rule.quotaBytes / 1073741824); $('policy-versions').value = rule.keepVersions ?? '';
    $('policy-quota').disabled = !state.user.isAdmin; $('policy-versions').disabled = !state.user.isAdmin;
    $('policy-usage').textContent = '已用 ' + size(info.usedBytes) + '（包含历史版本和回收站） · 当前保留 ' + info.effectiveKeepVersions + ' 个版本';
    $('grant-list').replaceChildren(); for (const grant of rule.grants) addGrant(grant);
    error('policy-error', ''); $('policy-dialog').showModal();
  }
  $('policy-form').addEventListener('submit', async event => {
    event.preventDefault(); error('policy-error', ''); $('policy-save').disabled = true;
    try {
      const quota = $('policy-quota').value === '' ? null : Math.round(Number($('policy-quota').value) * 1073741824);
      if (quota !== null && (!Number.isSafeInteger(quota) || quota <= 0)) throw new Error('容量必须是有效的正数。');
      const grants = Array.from($('grant-list').children).map(row => { const [kind, id] = row.querySelector('.grant-subject').value.split(':'); return { kind, id, access: Number(row.querySelector('.grant-rights').value) }; });
      await json('/api/folders/' + editedFolder.id + '/policy', 'PUT', { folderId: editedFolder.id, ownerId: $('policy-owner').value, visibility: $('policy-visibility').value, publicAccess: /^个人\/[^/]+$/i.test(editedFolder.path) ? 0 : Number($('policy-public').value), grants, quotaBytes: quota, keepVersions: $('policy-versions').value === '' ? null : Number($('policy-versions').value) });
      $('policy-dialog').close(); await refresh(); notify('权限、配额和版本策略已保存');
    } catch (e) { error('policy-error', e.message); } finally { $('policy-save').disabled = false; }
  });
  $('inherit-policy').addEventListener('click', async () => { try { if (!confirm('移除此目录的独立权限、配额和保留设置，恢复继承上级？')) return; await json('/api/folders/' + editedFolder.id + '/policy', 'DELETE'); $('policy-dialog').close(); await refresh(); } catch (e) { error('policy-error', e.message); } });
  async function downloadVersion(entry, version) {
    const response = await request('/api/entries/' + entry.id + '/versions/' + version.version), blob = await response.blob();
    const url = URL.createObjectURL(blob), link = document.createElement('a'); link.href = url; link.download = entry.path.split('/').pop(); link.click(); setTimeout(() => URL.revokeObjectURL(url), 60000);
  }
  async function versions(entry) {
    const result = await json('/api/browser/versions/'+entry.id+'?'+new URLSearchParams({page:pages.versions.page,pageSize:pages.versions.size}),'GET'), list=result.items; $('versions-title').textContent = '版本历史 · ' + entry.path; $('version-list').replaceChildren();
    for (const version of list) {
      const row = document.createElement('div'); row.className = 'management-row'; const text = document.createElement('span');
      text.textContent = new Date(version.modifiedUtc).toLocaleString('zh-CN') + ' · ' + size(version.size) + (version.current ? ' · 当前版本' : ''); row.append(text, button('下载', () => downloadVersion(entry, version)));
      if (!version.current && (entry.access & 2) !== 0) row.append(button('恢复', async () => { if (!confirm('将此版本恢复为当前内容？当前内容会按保留策略进入历史版本。')) return; await json('/api/entries/' + entry.id + '/versions/' + version.version + '/restore', 'POST', null, { 'If-Match': '"' + entry.version + '"' }); $('versions-dialog').close(); await refresh(); notify('版本已恢复，客户端将收到更新'); }));
      $('version-list').append(row);
    }
    pager($('versions-pager'),result,async(page,size)=>{pages.versions.page=page;pages.versions.size=size;await versions(entry);});
    if(!$('versions-dialog').open)$('versions-dialog').showModal();
  }
  const previewExtensions = new Set(['png','jpg','jpeg','gif','webp','bmp','avif','mp4','webm','ogv','mp3','wav','ogg','txt','log','md','csv','json','cs','xml','yaml','yml']);
  const canPreview = entry => previewExtensions.has(entry.path.split('.').pop().toLowerCase());
  $('download-backup').addEventListener('click', async () => {
    try {
      await json('/api/me', 'GET');
      const link = document.createElement('a'); link.href = '/api/admin/backup'; link.download = 'yunsevo-backup.zip'; document.body.append(link); link.click(); link.remove();
      $('backup-status').textContent = '正在生成备份，完成后开始传输；请在浏览器的下载列表查看进度。';
    } catch (e) { notify(e.message); }
  });
  async function preview(entry, trash = false) {
    if (!canPreview(entry)) return download(entry, trash);
    $('preview-title').textContent = entry.path; $('preview-content').replaceChildren(); $('preview-dialog').showModal();
    showOrigin(entry);
    const ext = entry.path.split('.').pop().toLowerCase(), src = (trash ? '/api/trash/' + entry.id + '/preview' : '/api/preview/' + entry.id) + '?version=' + encodeURIComponent(entry.version);
    if (['png','jpg','jpeg','gif','webp','bmp','avif'].includes(ext)) {
      const img = document.createElement('img'); img.src = src; img.alt = entry.path; img.addEventListener('error', () => notify('图片无法预览，请检查格式或下载查看。')); $('preview-content').append(img);
    } else if (['mp4','webm','ogv','mp3','wav','ogg'].includes(ext)) {
      const media = document.createElement(['mp3','wav','ogg'].includes(ext) ? 'audio' : 'video'); media.controls = true; media.preload = 'metadata'; media.src = src; $('preview-content').append(media);
      media.addEventListener('error', () => { $('preview-dialog').close(); notify('此媒体无法预览，已转为下载。'); download(entry, trash); });
    } else {
      try { const response = await request(src, { headers: { Range: 'bytes=0-1048575' } }); const text = document.createElement('pre'); text.textContent = await response.text(); $('preview-content').append(text); if (entry.size > 1048576) { const note = document.createElement('p'); note.textContent = '仅显示前 1 MiB 内容。'; $('preview-content').append(note); } }
      catch (e) { $('preview-content').textContent = e.message; }
    }
  }
  function showOrigin(entry) {
    const origin = entry.origin, panel = $('preview-origin');
    panel.textContent = origin ? [
      origin.photographer ? '拍摄人：' + origin.photographer.displayName + '（' + origin.photographer.username + '）' : '',
      origin.project ? '工程：' + origin.project : '',
      origin.capturedUtc ? '拍摄时间：' + new Date(origin.capturedUtc).toLocaleString('zh-CN') + '（设备上报）' : '',
      origin.place ? '部位：' + origin.place : '',
      origin.note ? '备注：' + origin.note : '',
      origin.location ? '位置：' + origin.location : '',
      origin.environment?.weather ? '天气：' + origin.environment.weather.description + ' ' + origin.environment.weather.temperatureC + '°C · ' + origin.environment.weather.source + (origin.environment.weather.forecast ? '（预报）' : '') + ' · ' + new Date(origin.environment.weather.dataUtc).toLocaleString('zh-CN') : '',
      '上传人：' + origin.uploadedBy.displayName + '（' + origin.uploadedBy.username + '）',
      '上传时间：' + new Date(origin.uploadedUtc).toLocaleString('zh-CN'),
      origin.deviceName ? '设备：' + origin.deviceName : ''
    ].filter(Boolean).join(' · ') : '历史文件，未记录拍摄或上传来源。';
  }
  $('preview-dialog').addEventListener('close', () => { for (const media of $('preview-content').querySelectorAll('video,audio')) { media.pause(); media.removeAttribute('src'); media.load(); } $('preview-content').replaceChildren(); });
  for (const closer of document.querySelectorAll('[data-close]')) closer.addEventListener('click', () => $(closer.dataset.close).close());
  window.MyPanExtras = { open: preview, navigation() {
    if(state.view==='admin-dialog')return {query:$(adminMode+'-query').value.trim(),page:String(pages[adminMode].page),pageSize:String(pages[adminMode].size)};
    if(state.view==='audit-dialog')return {userFilter:$('audit-user').value.trim(),kindFilter:$('audit-kind').value,page:String(pages.audit.page),pageSize:String(pages.audit.size)};
    return {};
  }, async restoreNavigation(route) {
    if(route.view==='users'||route.view==='groups'){
      pages[route.view]={page:route.page,size:route.pageSize};$(route.view+'-query').value=route.query;
      if(!await administration(route.view)&&state.authenticated)throw new Error('管理页面恢复失败。');
    }else if(route.view==='settings'){await loadSettings();showPage('settings-dialog');}
    else if(route.view==='audit'){pages.audit={page:route.page,size:route.pageSize};$('audit-user').value=route.userFilter;$('audit-kind').value=route.kindFilter;await loadAudit();showPage('audit-dialog');}
  }, actions(entry, actions, trash) {
    if (!entry.isDirectory) actions.append(button('详情', () => {
      $('preview-title').textContent = entry.path; $('preview-content').replaceChildren(); showOrigin(entry); $('preview-dialog').showModal();
    }));
    if (trash) { if (!entry.isDirectory && canPreview(entry)) actions.append(button('预览', () => preview(entry, true))); if ((entry.access & 2) !== 0) actions.append(button('永久删除', async () => { if (!confirm('永久删除“' + entry.path + '”及其历史版本？此操作不可恢复。')) return; await json('/api/trash/' + entry.id, 'DELETE'); await refresh(); notify('已永久删除并释放配额'); })); return; }
    if (entry.isDirectory) { if ((entry.access & 4) !== 0) actions.append(button('设置', () => policy(entry))); }
    else { if (canPreview(entry)) actions.append(button('预览', () => preview(entry))); actions.append(button('版本', () => versions(entry))); }
  } };
  window.addEventListener('mypan-login', () => {
    for (const id of ['admin-nav-label','nav-admin','nav-groups','nav-settings','nav-audit']) $(id).hidden = !state.user.isAdmin; eventSource?.close(); eventSource = new EventSource('/api/events?after=' + state.revision);
    eventSource.addEventListener('change', async () => { if (state.authenticated && !state.restoringView && state.view === 'files' && !document.querySelector('dialog[open]') && !state.uploading && !state.busy) { try { await refresh(true); } catch { } } });
  });
  window.addEventListener('mypan-logout', () => { eventSource?.close(); eventSource = null; });
})();
