(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  let code = '', busy = false;
  function error(message) { $('setup-error').textContent = message; $('setup-error').hidden = !message; }
  async function request(url, options = {}) {
    const response = await fetch(url, { ...options, headers: { 'Content-Type': 'application/json', 'X-Yunsevo-Setup-Code': code } });
    const result = await response.json();
    if (!response.ok) throw new Error(result.message || '无法完成安装，请稍后重试。');
    return result;
  }
  async function submit(form, operation) {
    if (busy) return; busy = true; error(''); const button = form.querySelector('button'); button.disabled = true;
    try { await operation(); } catch (e) { error(e.message || '无法连接服务端，请检查网络后重试。'); }
    finally { busy = false; button.disabled = false; }
  }
  $('setup-verify').addEventListener('submit', event => {
    event.preventDefault(); submit(event.target, async () => {
      code = $('setup-code').value.trim();
      const defaults = await request('/api/setup/defaults');
      $('setup-path').value = defaults.dataPath;
      $('setup-verify').hidden = true; $('setup-configure').hidden = false;
      $('step-verify').classList.remove('current'); $('step-configure').classList.add('current'); $('setup-username').focus();
    });
  });
  $('setup-configure').addEventListener('submit', event => {
    event.preventDefault(); submit(event.target, async () => {
      if ($('setup-password').value !== $('setup-confirm').value) throw new Error('两次输入的密码不一致。');
      try {
        await request('/api/setup/complete', { method: 'POST', body: JSON.stringify({ username: $('setup-username').value.trim(), password: $('setup-password').value, confirmPassword: $('setup-confirm').value, dataPath: $('setup-path').value.trim() }) });
      } catch (e) {
        // A server transition can interrupt the final response after the installation was committed.
        let installed = false;
        for (let attempt = 0; attempt < 10; attempt++) {
          try { const status = await fetch('/api/setup/status', { cache: 'no-store' }); if (status.ok && (await status.json()).required === false) { installed = true; break; } } catch { }
          if (!(e instanceof TypeError || e instanceof SyntaxError)) break;
          await new Promise(resolve => setTimeout(resolve, 300));
        }
        if (!installed) throw e;
      }
      code = ''; for (const name of ['setup-code', 'setup-password', 'setup-confirm']) $(name).value = '';
      $('setup-configure').hidden = true; $('setup-finish').hidden = false;
      $('step-configure').classList.remove('current'); $('step-finish').classList.add('current');
      for (let attempt = 0; attempt < 100; attempt++) {
        try { const response = await fetch('/health', { cache: 'no-store' }); if (response.ok && await response.text() === 'MyPan OK') { location.replace('/'); return; } } catch { }
        await new Promise(resolve => setTimeout(resolve, 300));
      }
      $('setup-ready').textContent = '安装配置已保存。服务启动较慢，请稍后进入登录页；如无法访问，请查看服务端启动日志。'; $('setup-login').hidden = false;
    });
  });
})();
