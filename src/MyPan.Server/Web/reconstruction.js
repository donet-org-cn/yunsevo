'use strict';
(()=>{
 const {state,json,notify,pager,size}=window.MyPan,$=id=>document.getElementById(id);let sources=[],videos=[],folder=null,page=1,pageSize=10,refreshing=false,session=0,canVideo=false,canPhotos=false;
 const modeName=q=>['快速草稿','本机算法对照','精细多视角深度','透明物体轮廓外形'][q]??'历史模式';
 function qualityChanged(){
  const quality=Number($('reconstruction-quality').value),backend=$('reconstruction-backend');
  backend.disabled=quality>=2;if(quality>=2)backend.value='cpu';
  $('reconstruction-quality-help').textContent=quality===3?'适合矿泉水瓶等透明、光滑物体：自动提取主体轮廓、标定相机、融合外形并投影原片。不能恢复内凹或未拍到的底面。CPU 计算，最多 90 分钟。':quality===2?'适合有纹理的设备和普通实物：分离主体、校正畸变、计算多视角深度并保留真实开口。透明瓶身请选择上一项轮廓模式。CPU 计算，最多 90 分钟。':'该路径用于检查手机 CPU / Vulkan 算法；透明、反光或弱纹理物体可能严重缺面。';
 }
 $('reconstruction-quality').onchange=qualityChanged;
 $('reconstruction-video').onchange=()=>{const video=!!$('reconstruction-video').value;$('reconstruction-photos').hidden=video;if(video&&Number($('reconstruction-quality').value)<2)$('reconstruction-quality').value='2';qualityChanged();$('reconstruction-submit').disabled=video?!canVideo:!canPhotos;};
 const button=(text,action)=>{const b=document.createElement('button');b.type='button';b.className='text-button';b.textContent=text;b.onclick=()=>Promise.resolve().then(action).catch(e=>notify(e.message));return b;};
 async function refresh(){
  if(refreshing||!$('reconstruction-dialog').open)return;refreshing=true;const epoch=state.epoch;
  try{const all=await json('/api/reconstruction/jobs','GET');if(epoch!==state.epoch)return;const host=$('reconstruction-jobs');host.replaceChildren();page=Math.min(page,Math.max(1,Math.ceil(all.length/pageSize)));
   for(const job of all.slice((page-1)*pageSize,page*pageSize)){
    const row=document.createElement('div');row.className='management-row';const text=document.createElement('div');const title=document.createElement('strong');title.textContent=job.name;const detail=document.createElement('p');detail.className='field-help';detail.textContent=job.username+' · '+new Date(job.createdUtc).toLocaleString('zh-CN')+' · '+modeName(job.quality)+' · '+(job.photoCount?job.photoCount+' 张':'视频自动选帧')+' · '+job.message;text.append(title,detail);row.append(text);
    const progress=document.createElement('progress');progress.max=100;progress.value=job.progress;row.append(progress);
    if(job.model)row.append(button('旋转查看',()=>window.MyPanExtras.open(job.model)),button('下载',()=>window.MyPan.download(job.model,false)));
    if(job.model&&job.quality>=2)row.append(button('质量报告',()=>{const link=document.createElement('a');link.href='/api/reconstruction/jobs/'+job.id+'/quality';link.download='reconstruction-quality.json';link.click();}));
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
   for(const value of ['2','3'])$('reconstruction-quality').querySelector('[value="'+value+'"]').disabled=!caps.highQuality;
   // Opening an old tab must not silently retain a fallback to the draft engine.
   $('reconstruction-quality').value=caps.highQuality?'2':'';
   if(!caps.highQuality)$('reconstruction-capabilities').textContent+=' · 精细工作进程未就绪；请检查配置，或明确选择算法对照／草稿。';
   qualityChanged();
   folder=null;sources=[];videos=[];canVideo=!!caps.highQualityVideo;canPhotos=false;$('reconstruction-video').replaceChildren(new Option('使用下方勾选的原片',''));$('reconstruction-photos').hidden=false;
   if(!state.trash&&state.folder){folder=(await json('/api/browser/entry?path='+encodeURIComponent(state.folder),'GET')).entry;}
   // EntryLookup differs from the direct entry response across older servers; validate explicitly.
   if(!folder&&state.folder&&!state.trash){const value=await json('/api/browser/entry?path='+encodeURIComponent(state.folder),'GET');folder=value.entry||value;}
   if(!folder?.isDirectory||(folder.access&2)===0){$('reconstruction-folder').textContent='请先进入有写入权限的原片文件夹，再打开三维扫描。';await refresh();return;}
   $('reconstruction-folder').textContent='原片与模型目录：/'+folder.path;
   for(let p=1;p<=5&&sources.length<120;p++){const result=await json('/api/browser/entries?folder='+encodeURIComponent(folder.path)+'&pageSize=100&page='+p,'GET');const entries=result.items.map(x=>x.entry).filter(x=>x&&!x.isDirectory);sources.push(...entries.filter(x=>/\.(jpe?g|png)$/i.test(x.path)));videos.push(...entries.filter(x=>/\.(mp4|mov|m4v|webm)$/i.test(x.path)));if(p*result.pageSize>=result.total)break;}
   if(opening!==session)return;sources=sources.slice(0,120);
   for(const entry of sources){const label=document.createElement('label'),check=document.createElement('input');check.type='checkbox';check.checked=true;check.value=entry.id;label.append(check,document.createTextNode(entry.path.split('/').pop()+' · '+size(entry.size)));$('reconstruction-photos').append(label);}
   for(const entry of videos){const option=new Option('原视频自动选帧 · '+entry.path.split('/').pop(),entry.id);option.disabled=!canVideo;$('reconstruction-video').append(option);}
   canPhotos=!!caps.cpu&&sources.length>=6;$('reconstruction-submit').disabled=!canPhotos;await refresh();
  }catch(e){notify(e.message);$('reconstruction-capabilities').textContent=e.message;}
 };
 $('close-reconstruction').onclick=()=>{$('reconstruction-dialog').close();session++;};
 $('reconstruction-refresh').onclick=refresh;
 $('reconstruction-form').onsubmit=async e=>{e.preventDefault();const submit=$('reconstruction-submit');submit.disabled=true;try{
  const ids=new Set([...$('reconstruction-photos').querySelectorAll('input:checked')].map(x=>x.value));const photos=sources.filter(x=>ids.has(x.id)).map(x=>({id:x.id,version:x.version}));
  const video=videos.find(x=>x.id===$('reconstruction-video').value);
  if(!video&&(photos.length<6||photos.length>120))throw new Error('请选择 6–120 张原片，或一个原视频。');
  if(video&&Number($('reconstruction-quality').value)<2)throw new Error('原视频请选择高精度或轮廓模式。');
  if(!$('reconstruction-quality').value)throw new Error('精细工作进程未就绪，请检查配置或明确选择其他模式。');
  const quality=Number($('reconstruction-quality').value);const job=await json('/api/reconstruction/jobs','POST',{folderId:folder.id,name:$('reconstruction-name').value.trim(),photos:video?[]:photos,video:video?{id:video.id,version:video.version}:null,backend:$('reconstruction-backend').value,quality,subject:'object'});notify('已提交：'+modeName(job.quality??quality));page=1;await refresh();
 }catch(e){notify(e.message);}finally{submit.disabled=false;}};
 setInterval(()=>{if($('reconstruction-dialog').open&&state.authenticated)refresh();},5000);
})();
