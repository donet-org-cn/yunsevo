'use strict';
(()=>{
 const {state,json,notify,pager,size}=window.MyPan,$=id=>document.getElementById(id);let sources=[],folder=null,page=1,pageSize=10,refreshing=false,session=0;
 const button=(text,action)=>{const b=document.createElement('button');b.type='button';b.className='text-button';b.textContent=text;b.onclick=()=>Promise.resolve().then(action).catch(e=>notify(e.message));return b;};
 async function refresh(){
  if(refreshing||!$('reconstruction-dialog').open)return;refreshing=true;const epoch=state.epoch;
  try{const all=await json('/api/reconstruction/jobs','GET');if(epoch!==state.epoch)return;const host=$('reconstruction-jobs');host.replaceChildren();page=Math.min(page,Math.max(1,Math.ceil(all.length/pageSize)));
   for(const job of all.slice((page-1)*pageSize,page*pageSize)){
    const row=document.createElement('div');row.className='management-row';const text=document.createElement('div');const title=document.createElement('strong');title.textContent=job.name;const detail=document.createElement('p');detail.className='field-help';detail.textContent=job.username+' · '+new Date(job.createdUtc).toLocaleString('zh-CN')+' · '+job.photoCount+' 张 · '+job.message;text.append(title,detail);row.append(text);
    const progress=document.createElement('progress');progress.max=100;progress.value=job.progress;row.append(progress);
    if(job.model)row.append(button('旋转查看',()=>window.MyPanExtras.open(job.model)),button('下载',()=>window.MyPan.download(job.model,false)));
    if(['queued','running'].includes(job.state))row.append(button('取消',async()=>{await json('/api/reconstruction/jobs/'+job.id,'DELETE');await refresh();}));else row.append(button('移除记录',async()=>{await json('/api/reconstruction/jobs/'+job.id+'?purge=true','DELETE');refreshing=false;await refresh();}));host.append(row);
   }
   $('reconstruction-count').textContent=all.length+' 个任务';pager($('reconstruction-pager'),{page,pageSize,total:all.length},async (p,n)=>{page=p;pageSize=n;await refresh();});
  }catch(e){notify(e.message);}finally{refreshing=false;}
 }
 $('open-reconstruction').onclick=async()=>{
  const opening=++session;try{
   $('reconstruction-dialog').showModal();$('reconstruction-photos').replaceChildren();$('reconstruction-submit').disabled=true;$('reconstruction-capabilities').textContent='正在检测重建服务…';
   const caps=await json('/api/reconstruction/capabilities','GET');if(opening!==session)return;
   $('reconstruction-capabilities').textContent=caps.cpu?(caps.vulkan?'服务端 Vulkan：'+caps.device+' · CPU 可用':'服务端 CPU 可用 · Vulkan 不可用'):caps.error;
   $('reconstruction-backend').querySelector('[value="vulkan"]').disabled=!caps.vulkan;
   folder=null;sources=[];
   if(!state.trash&&state.folder){folder=(await json('/api/browser/entry?path='+encodeURIComponent(state.folder),'GET')).entry;}
   // EntryLookup differs from the direct entry response across older servers; validate explicitly.
   if(!folder&&state.folder&&!state.trash){const value=await json('/api/browser/entry?path='+encodeURIComponent(state.folder),'GET');folder=value.entry||value;}
   if(!folder?.isDirectory||(folder.access&2)===0){$('reconstruction-folder').textContent='请先进入有写入权限的原片文件夹，再打开三维扫描。';await refresh();return;}
   $('reconstruction-folder').textContent='原片与模型目录：/'+folder.path;
   for(let p=1;p<=5&&sources.length<120;p++){const result=await json('/api/browser/entries?folder='+encodeURIComponent(folder.path)+'&pageSize=100&page='+p,'GET');sources.push(...result.items.map(x=>x.entry).filter(x=>x&&!x.isDirectory&&/\.(jpe?g|png)$/i.test(x.path)));if(p*result.pageSize>=result.total)break;}
   if(opening!==session)return;sources=sources.slice(0,120);
   for(const entry of sources){const label=document.createElement('label'),check=document.createElement('input');check.type='checkbox';check.checked=true;check.value=entry.id;label.append(check,document.createTextNode(entry.path.split('/').pop()+' · '+size(entry.size)));$('reconstruction-photos').append(label);}
   $('reconstruction-submit').disabled=!caps.cpu||sources.length<6;await refresh();
  }catch(e){notify(e.message);$('reconstruction-capabilities').textContent=e.message;}
 };
 $('close-reconstruction').onclick=()=>{$('reconstruction-dialog').close();session++;};
 $('reconstruction-refresh').onclick=refresh;
 $('reconstruction-form').onsubmit=async e=>{e.preventDefault();const submit=$('reconstruction-submit');submit.disabled=true;try{
  const ids=new Set([...$('reconstruction-photos').querySelectorAll('input:checked')].map(x=>x.value));const photos=sources.filter(x=>ids.has(x.id)).map(x=>({id:x.id,version:x.version}));
  if(photos.length<6||photos.length>120)throw new Error('请选择 6–120 张原片。');
  await json('/api/reconstruction/jobs','POST',{folderId:folder.id,name:$('reconstruction-name').value.trim(),photos,backend:$('reconstruction-backend').value,quality:Number($('reconstruction-quality').value),subject:'object'});notify('已加入服务端重建队列。');page=1;await refresh();
 }catch(e){notify(e.message);}finally{submit.disabled=false;}};
 setInterval(()=>{if($('reconstruction-dialog').open&&state.authenticated)refresh();},5000);
})();
