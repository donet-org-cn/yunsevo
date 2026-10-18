(() => {
  'use strict';
  const canvas=document.getElementById('panorama'),image=document.getElementById('source'),status=document.getElementById('status');
  const gl=canvas.getContext('webgl2',{alpha:false,antialias:false});
  if(!gl){status.textContent='此浏览器不支持全景显示，请下载图片查看。';return;}
  let yaw=0,pitch=0,fov=75,ready=false,frame=0;
  function shader(type,source){const value=gl.createShader(type);gl.shaderSource(value,source);gl.compileShader(value);if(!gl.getShaderParameter(value,gl.COMPILE_STATUS))throw new Error('全景着色器不可用');return value;}
  try {
    const program=gl.createProgram();
    gl.attachShader(program,shader(gl.VERTEX_SHADER,`#version 300 es
      void main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}`));
    gl.attachShader(program,shader(gl.FRAGMENT_SHADER,`#version 300 es
      precision highp float;uniform sampler2D photo;uniform vec2 resolution;uniform vec3 view;out vec4 color;
      void main(){vec2 p=(gl_FragCoord.xy*2.0-resolution)/resolution.y*tan(view.z*0.5);vec3 d=normalize(vec3(p,-1));
        d.yz=mat2(cos(view.y),sin(view.y),-sin(view.y),cos(view.y))*d.yz;
        d.xz=mat2(cos(view.x),sin(view.x),-sin(view.x),cos(view.x))*d.xz;
        vec2 uv=vec2(0.5+atan(d.x,-d.z)/6.28318530718,0.5-asin(clamp(d.y,-1.0,1.0))/3.14159265359);color=texture(photo,uv);}`));
    gl.linkProgram(program);if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw new Error('全景渲染初始化失败');gl.useProgram(program);
    const resolution=gl.getUniformLocation(program,'resolution'),view=gl.getUniformLocation(program,'view');
    const texture=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,texture);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.REPEAT);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
    function render(){frame=0;if(!ready)return;const ratio=Math.min(devicePixelRatio||1,2);canvas.width=Math.max(1,Math.round(canvas.clientWidth*ratio));canvas.height=Math.max(1,Math.round(canvas.clientHeight*ratio));gl.viewport(0,0,canvas.width,canvas.height);gl.uniform2f(resolution,canvas.width,canvas.height);gl.uniform3f(view,yaw,pitch,fov*Math.PI/180);gl.drawArrays(gl.TRIANGLES,0,3);}
    function draw(){if(!frame)frame=requestAnimationFrame(render);}
    function loaded(){try{if(Math.abs(image.naturalWidth/image.naturalHeight-2)>.02)throw new Error('需要宽高比 2:1 的等距柱状全景图');if(image.naturalWidth>gl.getParameter(gl.MAX_TEXTURE_SIZE))throw new Error('图片超出此设备的纹理尺寸上限');gl.texImage2D(gl.TEXTURE_2D,0,gl.RGB,gl.RGB,gl.UNSIGNED_BYTE,image);if(gl.getError()!==gl.NO_ERROR)throw new Error('图片无法载入显存');ready=true;canvas.dataset.ready='true';status.textContent='';draw();}catch(error){status.textContent=error.message;}}
    image.onload=loaded;image.onerror=()=>{status.textContent='全景加载失败，请检查登录状态、文件权限或下载查看。';};
    const q=new URLSearchParams(location.search),id=q.get('id'),version=q.get('version');
    if(/^[0-9a-f-]{36}$/i.test(id||'')&&version&&version.length<200)image.src=(q.get('trash')==='1'?'/api/trash/'+id+'/preview':'/api/preview/'+id)+'?version='+encodeURIComponent(version);
    else if(image.getAttribute('src')){if(image.complete&&image.naturalWidth)loaded();}else status.textContent='全景地址无效。';
    const pointers=new Map();let distance=0;
    canvas.onpointerdown=e=>{canvas.setPointerCapture(e.pointerId);pointers.set(e.pointerId,[e.clientX,e.clientY]);distance=0;};
    canvas.onpointermove=e=>{const old=pointers.get(e.pointerId);if(!old)return;pointers.set(e.pointerId,[e.clientX,e.clientY]);if(pointers.size===1){yaw-=(e.clientX-old[0])*fov*Math.PI/180/canvas.clientHeight;pitch=Math.max(-1.2,Math.min(1.2,pitch+(e.clientY-old[1])*fov*Math.PI/180/canvas.clientHeight));}else{const [a,b]=[...pointers.values()];const next=Math.hypot(a[0]-b[0],a[1]-b[1]);if(distance>0&&next>0)fov=Math.max(35,Math.min(100,fov*distance/next));distance=next;}draw();};
    canvas.onpointerup=canvas.onpointercancel=e=>{pointers.delete(e.pointerId);distance=0;};
    canvas.addEventListener('wheel',e=>{e.preventDefault();fov=Math.max(35,Math.min(100,fov+e.deltaY*.04));draw();},{passive:false});
    canvas.onkeydown=e=>{if(!['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(e.key))return;e.preventDefault();if(e.key==='ArrowLeft')yaw-=.08;if(e.key==='ArrowRight')yaw+=.08;if(e.key==='ArrowUp')pitch=Math.min(1.2,pitch+.08);if(e.key==='ArrowDown')pitch=Math.max(-1.2,pitch-.08);draw();};
    document.getElementById('reset').onclick=()=>{yaw=pitch=0;fov=75;draw();};
    document.getElementById('zoom-in').onclick=()=>{fov=Math.max(35,fov-10);draw();};document.getElementById('zoom-out').onclick=()=>{fov=Math.min(100,fov+10);draw();};
    document.getElementById('fullscreen').onclick=async()=>{try{if(document.fullscreenElement)await document.exitFullscreen();else await document.documentElement.requestFullscreen();}catch{status.textContent='浏览器未允许全屏。';}};
    window.addEventListener('resize',draw);canvas.addEventListener('webglcontextlost',e=>{e.preventDefault();ready=false;status.textContent='显示已中断，请重新打开预览。';});
    window.addEventListener('pagehide',()=>{cancelAnimationFrame(frame);gl.deleteTexture(texture);gl.deleteProgram(program);});
  }catch(error){status.textContent=error.message;}
})();
