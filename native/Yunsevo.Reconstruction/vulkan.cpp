#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include "compute.h"
#include <opencv2/core.hpp>
#include "shader.h"
#include <algorithm>
#include <cmath>
#include <cstring>
#include <stdexcept>
#ifdef _WIN32
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#else
#include <dlfcn.h>
#endif
#define VK_FUNCTIONS(X) \
 X(vkCreateInstance) X(vkDestroyInstance) X(vkEnumeratePhysicalDevices) X(vkGetPhysicalDeviceProperties) \
 X(vkGetPhysicalDeviceQueueFamilyProperties) X(vkGetPhysicalDeviceMemoryProperties) X(vkCreateDevice) \
 X(vkDestroyDevice) X(vkGetDeviceQueue) X(vkCreateBuffer) X(vkDestroyBuffer) X(vkGetBufferMemoryRequirements) \
 X(vkAllocateMemory) X(vkFreeMemory) X(vkBindBufferMemory) X(vkMapMemory) X(vkUnmapMemory) \
 X(vkCreateShaderModule) X(vkDestroyShaderModule) X(vkCreateDescriptorSetLayout) X(vkDestroyDescriptorSetLayout) \
 X(vkCreatePipelineLayout) X(vkDestroyPipelineLayout) X(vkCreateComputePipelines) X(vkDestroyPipeline) \
 X(vkCreateDescriptorPool) X(vkDestroyDescriptorPool) X(vkAllocateDescriptorSets) X(vkUpdateDescriptorSets) \
 X(vkCreateCommandPool) X(vkDestroyCommandPool) X(vkAllocateCommandBuffers) X(vkBeginCommandBuffer) \
 X(vkEndCommandBuffer) X(vkCmdBindPipeline) X(vkCmdBindDescriptorSets) X(vkCmdDispatch) \
 X(vkQueueSubmit) X(vkCreateFence) X(vkWaitForFences) X(vkDestroyFence) X(vkDeviceWaitIdle)
static void check(VkResult r){if(r!=VK_SUCCESS)throw std::runtime_error("Vulkan error "+std::to_string(r));}
struct VulkanDepth::Impl {
 void* lib=nullptr;VkInstance instance=nullptr;VkPhysicalDevice physical=nullptr;VkDevice device=nullptr;
 VkQueue queue=nullptr;uint32_t family=0;VkPhysicalDeviceMemoryProperties memory{};std::string gpu;
 PFN_vkGetInstanceProcAddr get=nullptr;
#define DECL(n) PFN_##n n=nullptr;
 VK_FUNCTIONS(DECL)
#undef DECL
 Impl(){try{init();}catch(...){cleanup();throw;}}
 void init(){
#ifdef _WIN32
  lib=LoadLibraryW(L"vulkan-1.dll");if(lib)get=(PFN_vkGetInstanceProcAddr)GetProcAddress((HMODULE)lib,"vkGetInstanceProcAddr");
#else
  lib=dlopen("libvulkan.so",RTLD_NOW|RTLD_LOCAL);if(!lib)lib=dlopen("libvulkan.so.1",RTLD_NOW|RTLD_LOCAL);
  if(lib)get=(PFN_vkGetInstanceProcAddr)dlsym(lib,"vkGetInstanceProcAddr");
#endif
  if(!get)throw std::runtime_error("Vulkan loader is not available");
  vkCreateInstance=(PFN_vkCreateInstance)get(nullptr,"vkCreateInstance");
  VkApplicationInfo app{VK_STRUCTURE_TYPE_APPLICATION_INFO};app.pApplicationName="Yunsevo Reconstruction";app.apiVersion=VK_API_VERSION_1_0;
  VkInstanceCreateInfo create{VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO};create.pApplicationInfo=&app;check(vkCreateInstance(&create,nullptr,&instance));
#define LOAD(n) n=(PFN_##n)get(instance,#n);if(!n)n=(PFN_##n)get(nullptr,#n);if(!n)throw std::runtime_error("Missing Vulkan function " #n);
  VK_FUNCTIONS(LOAD)
#undef LOAD
  uint32_t count=0;check(vkEnumeratePhysicalDevices(instance,&count,nullptr));std::vector<VkPhysicalDevice> devices(count);check(vkEnumeratePhysicalDevices(instance,&count,devices.data()));
  for(auto candidate:devices){
   VkPhysicalDeviceProperties props{};vkGetPhysicalDeviceProperties(candidate,&props);
   // Software Vulkan drivers are CPU backends, not evidence of GPU acceleration.
   if(props.deviceType==VK_PHYSICAL_DEVICE_TYPE_CPU)continue;
   uint32_t n=0;vkGetPhysicalDeviceQueueFamilyProperties(candidate,&n,nullptr);std::vector<VkQueueFamilyProperties> queues(n);vkGetPhysicalDeviceQueueFamilyProperties(candidate,&n,queues.data());
   for(uint32_t i=0;i<n;i++)if(queues[i].queueFlags&VK_QUEUE_COMPUTE_BIT){physical=candidate;family=i;gpu=props.deviceName;break;}
   if(physical)break;
  }
  if(!physical)throw std::runtime_error("No hardware Vulkan compute queue");
  float priority=1;VkDeviceQueueCreateInfo q{VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO};q.queueFamilyIndex=family;q.queueCount=1;q.pQueuePriorities=&priority;
  VkDeviceCreateInfo d{VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO};d.queueCreateInfoCount=1;d.pQueueCreateInfos=&q;check(vkCreateDevice(physical,&d,nullptr,&device));
  vkGetDeviceQueue(device,family,0,&queue);vkGetPhysicalDeviceMemoryProperties(physical,&memory);
 }
 void cleanup(){if(device){if(vkDeviceWaitIdle)vkDeviceWaitIdle(device);if(vkDestroyDevice)vkDestroyDevice(device,nullptr);}if(instance&&vkDestroyInstance)vkDestroyInstance(instance,nullptr);
#ifdef _WIN32
 if(lib)FreeLibrary((HMODULE)lib);
#else
 if(lib)dlclose(lib);
#endif
 }
 ~Impl(){cleanup();}
 struct Buffer {
  Impl& v;VkBuffer buffer=nullptr;VkDeviceMemory mem=nullptr;void* mapped=nullptr;size_t size;
  Buffer(Impl& v,size_t bytes,const void* src=nullptr):v(v),size(bytes){try{
   VkBufferCreateInfo info{VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};info.size=bytes;info.usage=VK_BUFFER_USAGE_STORAGE_BUFFER_BIT;check(v.vkCreateBuffer(v.device,&info,nullptr,&buffer));
   VkMemoryRequirements req{};v.vkGetBufferMemoryRequirements(v.device,buffer,&req);uint32_t type=UINT32_MAX;
   for(uint32_t i=0;i<v.memory.memoryTypeCount;i++)if((req.memoryTypeBits&(1u<<i))&&(v.memory.memoryTypes[i].propertyFlags&(VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT|VK_MEMORY_PROPERTY_HOST_COHERENT_BIT))==(VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT|VK_MEMORY_PROPERTY_HOST_COHERENT_BIT)){type=i;break;}
   if(type==UINT32_MAX)throw std::runtime_error("GPU lacks coherent host-visible storage");
   VkMemoryAllocateInfo alloc{VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};alloc.allocationSize=req.size;alloc.memoryTypeIndex=type;check(v.vkAllocateMemory(v.device,&alloc,nullptr,&mem));check(v.vkBindBufferMemory(v.device,buffer,mem,0));check(v.vkMapMemory(v.device,mem,0,bytes,0,&mapped));if(src)std::memcpy(mapped,src,bytes);
  }catch(...){release();throw;}}
  void release(){if(mapped)v.vkUnmapMemory(v.device,mem);if(buffer)v.vkDestroyBuffer(v.device,buffer,nullptr);if(mem)v.vkFreeMemory(v.device,mem,nullptr);}
  ~Buffer(){release();}
 };
 std::vector<float> run(const DepthJob& job){
  std::vector<float> p={(float)job.width,(float)job.height,(float)job.planes,(float)job.sources,job.nearDepth,job.farDepth,job.focal,job.cx,job.cy,0,0,0};
  for(const auto& t:job.transforms)p.insert(p.end(),t.begin(),t.begin()+12);
  Buffer images(*this,job.pixels.size()*4,job.pixels.data()),params(*this,p.size()*4,p.data()),output(*this,(size_t)job.width*job.height*4);
  VkShaderModule shader=nullptr;VkDescriptorSetLayout layout=nullptr;VkPipelineLayout pipelineLayout=nullptr;VkPipeline pipeline=nullptr;VkDescriptorPool pool=nullptr;VkCommandPool commands=nullptr;VkFence fence=nullptr;
  auto cleanup=[&](){if(fence)vkDestroyFence(device,fence,nullptr);if(commands)vkDestroyCommandPool(device,commands,nullptr);if(pool)vkDestroyDescriptorPool(device,pool,nullptr);if(pipeline)vkDestroyPipeline(device,pipeline,nullptr);if(pipelineLayout)vkDestroyPipelineLayout(device,pipelineLayout,nullptr);if(layout)vkDestroyDescriptorSetLayout(device,layout,nullptr);if(shader)vkDestroyShaderModule(device,shader,nullptr);};
  try{
   VkShaderModuleCreateInfo sci{VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO};sci.codeSize=sizeof(depthShader);sci.pCode=depthShader;check(vkCreateShaderModule(device,&sci,nullptr,&shader));
   VkDescriptorSetLayoutBinding bindings[3]{};for(int i=0;i<3;i++){bindings[i].binding=i;bindings[i].descriptorType=VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;bindings[i].descriptorCount=1;bindings[i].stageFlags=VK_SHADER_STAGE_COMPUTE_BIT;}
   VkDescriptorSetLayoutCreateInfo lci{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO};lci.bindingCount=3;lci.pBindings=bindings;check(vkCreateDescriptorSetLayout(device,&lci,nullptr,&layout));
   VkPipelineLayoutCreateInfo pci{VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO};pci.setLayoutCount=1;pci.pSetLayouts=&layout;check(vkCreatePipelineLayout(device,&pci,nullptr,&pipelineLayout));
   VkComputePipelineCreateInfo cpi{VK_STRUCTURE_TYPE_COMPUTE_PIPELINE_CREATE_INFO};cpi.layout=pipelineLayout;cpi.stage.sType=VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;cpi.stage.stage=VK_SHADER_STAGE_COMPUTE_BIT;cpi.stage.module=shader;cpi.stage.pName="main";check(vkCreateComputePipelines(device,nullptr,1,&cpi,nullptr,&pipeline));
   VkDescriptorPoolSize ps{VK_DESCRIPTOR_TYPE_STORAGE_BUFFER,3};VkDescriptorPoolCreateInfo dpi{VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO};dpi.maxSets=1;dpi.poolSizeCount=1;dpi.pPoolSizes=&ps;check(vkCreateDescriptorPool(device,&dpi,nullptr,&pool));
   VkDescriptorSetAllocateInfo dai{VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO};dai.descriptorPool=pool;dai.descriptorSetCount=1;dai.pSetLayouts=&layout;VkDescriptorSet set=nullptr;check(vkAllocateDescriptorSets(device,&dai,&set));
   VkDescriptorBufferInfo bi[]={{images.buffer,0,images.size},{params.buffer,0,params.size},{output.buffer,0,output.size}};VkWriteDescriptorSet writes[3]{};
   for(int i=0;i<3;i++){writes[i].sType=VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET;writes[i].dstSet=set;writes[i].dstBinding=i;writes[i].descriptorCount=1;writes[i].descriptorType=VK_DESCRIPTOR_TYPE_STORAGE_BUFFER;writes[i].pBufferInfo=&bi[i];}vkUpdateDescriptorSets(device,3,writes,0,nullptr);
   VkCommandPoolCreateInfo cpool{VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO};cpool.queueFamilyIndex=family;check(vkCreateCommandPool(device,&cpool,nullptr,&commands));
   VkCommandBufferAllocateInfo cai{VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO};cai.commandPool=commands;cai.level=VK_COMMAND_BUFFER_LEVEL_PRIMARY;cai.commandBufferCount=1;VkCommandBuffer cmd=nullptr;check(vkAllocateCommandBuffers(device,&cai,&cmd));
   VkCommandBufferBeginInfo begin{VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO};begin.flags=VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;check(vkBeginCommandBuffer(cmd,&begin));vkCmdBindPipeline(cmd,VK_PIPELINE_BIND_POINT_COMPUTE,pipeline);vkCmdBindDescriptorSets(cmd,VK_PIPELINE_BIND_POINT_COMPUTE,pipelineLayout,0,1,&set,0,nullptr);vkCmdDispatch(cmd,(job.width*job.height+63)/64,1,1);check(vkEndCommandBuffer(cmd));
   VkFenceCreateInfo fi{VK_STRUCTURE_TYPE_FENCE_CREATE_INFO};check(vkCreateFence(device,&fi,nullptr,&fence));VkSubmitInfo submit{VK_STRUCTURE_TYPE_SUBMIT_INFO};submit.commandBufferCount=1;submit.pCommandBuffers=&cmd;check(vkQueueSubmit(queue,1,&submit,fence));
   VkResult result=vkWaitForFences(device,1,&fence,VK_TRUE,120000000000ull);if(result!=VK_SUCCESS){vkDeviceWaitIdle(device);check(result);}
   std::vector<float> depths((size_t)job.width*job.height);std::memcpy(depths.data(),output.mapped,output.size);cleanup();return depths;
  }catch(...){vkDeviceWaitIdle(device);cleanup();throw;}
 }
};
VulkanDepth::VulkanDepth():impl(std::make_unique<Impl>()){}
VulkanDepth::~VulkanDepth()=default;
std::string VulkanDepth::name()const{return impl->gpu;}
std::vector<float> VulkanDepth::run(const DepthJob& job){return impl->run(job);}
std::vector<float> cpuDepth(const DepthJob& j){
 const int w=j.width,h=j.height;std::vector<float> out(w*h);
 auto sample=[&](int source,float x,float y){if(x<0||y<0||x>=w-1||y>=h-1)return -1.f;int ix=(int)x,iy=(int)y,b=source*w*h+iy*w+ix;float ax=x-ix,ay=y-iy;return (j.pixels[b]*(1-ax)+j.pixels[b+1]*ax)*(1-ay)+(j.pixels[b+w]*(1-ax)+j.pixels[b+w+1]*ax)*ay;};
 cv::parallel_for_(cv::Range(3,h-3),[&](const cv::Range& rows){for(int y=rows.start;y<rows.end;y++)for(int x=3;x<w-3;x++){
  float a3=0,aa3=0,a5=0,aa5=0;for(int dy=-2;dy<=2;dy++)for(int dx=-2;dx<=2;dx++){float v=j.pixels[(y+dy)*w+x+dx];a5+=v;aa5+=v*v;if(std::abs(dx)<=1&&std::abs(dy)<=1){a3+=v;aa3+=v*v;}}
  float va3=aa3-a3*a3/9,va5=aa5-a5*a5/25;if(va3<.00015f||va5<.00015f)continue;
  auto cost=[&](float z,int radius,float a,float va){float first=2,second=2;int valid=0,n=(radius*2+1)*(radius*2+1);
   for(int s=0;s<j.sources;s++){const auto&t=j.transforms[s];float b=0,bb=0,ab=0;bool ok=true;
    for(int dy=-radius;dy<=radius;dy++)for(int dx=-radius;dx<=radius;dx++){
     float px=(x+dx-j.cx)*z/j.focal,py=(y+dy-j.cy)*z/j.focal;
     float qx=t[0]*px+t[1]*py+t[2]*z+t[3],qy=t[4]*px+t[5]*py+t[6]*z+t[7],qz=t[8]*px+t[9]*py+t[10]*z+t[11];
     float u=qz>0?sample(s+1,j.focal*qx/qz+j.cx,j.focal*qy/qz+j.cy):-1,v=j.pixels[(y+dy)*w+x+dx];if(u<0)ok=false;b+=u;bb+=u*u;ab+=u*v;
    }
    float vb=bb-b*b/n;if(ok&&va>.00015f&&vb>.00015f){float c=1-std::clamp((ab-a*b/n)/std::sqrt(va*vb),-1.f,1.f);if(c<first){second=first;first=c;}else second=std::min(second,c);valid++;}
   }return valid>=2?(first+second)*.5f:2.f;
  };
  float nearInv=1/j.nearDepth,stepInv=(1/j.farDepth-nearInv)/(j.planes-1),best=2,bp=0;
  for(int plane=0;plane<j.planes;plane++){float c=cost(1/(nearInv+plane*stepInv),1,a3,va3);if(c<best){best=c;bp=(float)plane;}}
  if(best>.4f)continue;float refined=2,result=0;
  for(int k=-4;k<=4;k++){float plane=std::clamp(bp+k*.2f,0.f,(float)(j.planes-1)),z=1/(nearInv+plane*stepInv),c=cost(z,2,a5,va5);if(c<refined){refined=c;result=z;}}
  if(refined<.25f)out[y*w+x]=result;
 }});return out;
}
