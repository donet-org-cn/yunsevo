// Numerical regression, using measured projections with withheld and perturbed poses.
#include "../native/Yunsevo.Reconstruction/engine.cpp"
#include <iostream>
int main(){
 try{
  std::vector<Camera> cameras(8);std::vector<Track> tracks(180);std::vector<Point3d> truth;
  const double focal=800,mid=512;cv::RNG random(813);
  for(int i=0;i<8;i++){cameras[i].gray=Mat::zeros(1024,1024,CV_8U);cameras[i].registered=true;Vec3d center((i-3)*.2,std::sin(i*.7)*.15,std::cos(i*.6)*.2),z=Vec3d(0,0,4.5)-center;z/=cv::norm(z);Vec3d x=Vec3d(0,1,0).cross(z);x/=cv::norm(x);Vec3d y=z.cross(x);cameras[i].R=Matx33d(x[0],x[1],x[2],y[0],y[1],y[2],z[0],z[1],z[2]);cameras[i].t=-cameras[i].R*center;}
  for(size_t p=0;p<tracks.size();p++){
   Point3d point(random.uniform(-1.,1.),random.uniform(-.8,.8),random.uniform(3.5,5.5));truth.push_back(point);auto&tr=tracks[p];tr.good=true;
   tr.point=point+Point3d(random.uniform(-.008,.008),random.uniform(-.008,.008),random.uniform(-.015,.015));
   for(int i=0;i<8;i++){Vec3d q=cameras[i].R*Vec3d(point.x,point.y,point.z)+cameras[i].t;Point2f pixel((float)(focal*q[0]/q[2]+mid),(float)(focal*q[1]/q[2]+mid));cameras[i].features.emplace_back(pixel,1.f);tr.observations[i]=(int)p;}
  }
  auto anchor=cameras[0];double scale=cameras[1].t[0];
  for(int i=1;i<8;i++){Vec3d angle(random.uniform(-.002,.002),random.uniform(-.002,.002),random.uniform(-.002,.002));Mat R;cv::Rodrigues(angle,R);Matx33d update;std::memcpy(update.val,R.ptr<double>(),9*sizeof(double));cameras[i].R=update*cameras[i].R;if(i!=1)cameras[i].t[0]+=random.uniform(-.008,.008);cameras[i].t[1]+=random.uniform(-.008,.008);cameras[i].t[2]+=random.uniform(-.008,.008);}
  auto rms=[&](double f){double sum=0;int count=0;for(auto&tr:tracks)for(auto[i,k]:tr.observations){double e=reprojection(cameras[i],tr.point,cameras[i].features[k].pt,f,mid);sum+=e*e;count++;}return std::sqrt(sum/count);};
  double f=790,before=rms(f);bundleAdjust(cameras,tracks,f,0,1,true,Progress{nullptr,nullptr},25);double after=rms(f);
  std::cout<<"RMS "<<before<<" -> "<<after<<" focal "<<f<<"\n";
  if(!(after<before*.1&&after<.15&&std::abs(f-800)<5))throw std::runtime_error("Joint optimization failed to recover synthetic camera geometry");
  if(cv::norm(cameras[0].t-anchor.t)>1e-12||cv::norm(Mat(cameras[0].R),Mat(anchor.R))>1e-12||cameras[1].t[0]!=scale)throw std::runtime_error("Gauge anchors moved");
  bool cancelled=false;try{bundleAdjust(cameras,tracks,f,0,1,true,Progress{[](void*,int,const char*){return 1;},nullptr});}catch(const std::runtime_error&e){cancelled=std::string(e.what())=="CANCELLED";}if(!cancelled)throw std::runtime_error("Bundle adjustment ignored cancellation");
  std::cout<<"PASS joint camera/point/focal recovery; RMS "<<before<<" -> "<<after<<" focal "<<f<<"\nPASS pose/scale gauge anchors\nPASS numerical optimization cancellation\n";
  DepthJob job{64,64,64,3,2,4,70,32,32};const float depth=3.2f;
  Mat reference(64,64,CV_32F);random.fill(reference,cv::RNG::UNIFORM,.1,.9);cv::GaussianBlur(reference,reference,cv::Size(3,3),.5);
  job.pixels.insert(job.pixels.end(),reference.ptr<float>(),reference.ptr<float>()+reference.total());
  for(int source=0;source<3;source++){
   float tx=source==0?.16f:source==1?-.13f:0,ty=source==2?.15f:0;std::array<float,16> transform{1,0,0,tx,0,1,0,ty,0,0,1,0,0,0,0,1};job.transforms.push_back(transform);
   Mat image;Mat warp=(cv::Mat_<double>(2,3)<<1,0,job.focal*tx/depth,0,1,job.focal*ty/depth);cv::warpAffine(reference,image,warp,reference.size(),cv::INTER_LINEAR,cv::BORDER_REFLECT);if(source==1)image=image*.8+.1;
   job.pixels.insert(job.pixels.end(),image.ptr<float>(),image.ptr<float>()+image.total());
  }
  auto cpu=cpuDepth(job);std::vector<double> errors;
  for(int y=12;y<52;y++)for(int x=12;x<52;x++)if(cpu[y*64+x]>0)errors.push_back(std::abs(cpu[y*64+x]-depth));
  if(errors.size()<1200)throw std::runtime_error("Insufficient measured depth coverage on a textured plane");std::sort(errors.begin(),errors.end());if(errors[errors.size()/2]>.04)throw std::runtime_error("Refined CPU depth differs from the known plane");
  std::cout<<"PASS CPU subpixel depth and exposure variation; median error "<<errors[errors.size()/2]<<"\n";
  try{VulkanDepth gpu;auto result=gpu.run(job);std::vector<double> differences;for(int y=12;y<52;y++)for(int x=12;x<52;x++)if(cpu[y*64+x]>0&&result[y*64+x]>0)differences.push_back(std::abs(cpu[y*64+x]-result[y*64+x]));std::sort(differences.begin(),differences.end());if(differences.size()<1200||differences[differences.size()/2]>.01)throw std::runtime_error("CPU/Vulkan refined depth mismatch");std::cout<<"PASS actual Vulkan/CPU depth parity on "<<gpu.name()<<"\n";}catch(const std::runtime_error&e){if(std::string(e.what()).find("No hardware")!=std::string::npos)std::cout<<"SKIP GPU: no hardware queue\n";else throw;}
  {
   const int side=32;std::vector<Camera> views(3);std::vector<std::vector<float>> depths(3,std::vector<float>(side*side,3.f));
   std::vector<std::vector<int>> adjacent{{1,2},{0,2},{0,1}};
   for(int i=0;i<3;i++){views[i].registered=true;views[i].t=Vec3d((i-1)*.1,0,0);}
   depths[0][16*side+16]=0; // A small missing measurement, independently observed in two views.
   depths[0][12*side+12]=8; // A false match must not enter the fusion volume.
   auto refined=refineDepths(views,depths,adjacent,side,30,Progress{nullptr,nullptr});
   if(std::abs(depths[0][16*side+16]-3)>.001||refined.filled==0||refined.rejected==0||depths[0][12*side+12]>3.01)throw std::runtime_error("Fine depth consistency / supported planar recovery failed");
   std::vector<float> boundary(side*side,3);for(int y=0;y<side;y++)for(int x=17;x<side;x++)boundary[y*side+x]=6;boundary[16*side+16]=0;
   if(smallPlanarGap(boundary,side,16,16)!=0)throw std::runtime_error("Planar completion crossed a depth discontinuity");
   depths.assign(3,std::vector<float>(side*side,3.f));
   for(auto&map:depths)for(int y=10;y<22;y++)for(int x=9;x<23;x++)map[y*side+x]=0;
   refineDepths(views,depths,adjacent,side,30,Progress{nullptr,nullptr});
   if(depths[0][16*side+16]!=0)throw std::runtime_error("Fine completion invented an unobserved surface");
   bool stopped=false;try{refineDepths(views,depths,adjacent,side,30,Progress{[](void*,int,const char*){return 1;},nullptr});}catch(const std::runtime_error&e){stopped=std::string(e.what())=="CANCELLED";}
   if(!stopped)throw std::runtime_error("Fine depth stage ignored cancellation");
   std::cout<<"PASS fine multi-view outlier filtering and independently supported small gaps\nPASS depth-edge and unobserved-opening preservation\nPASS fine-stage cancellation\n";
  }
  return 0;
 }catch(const std::exception&e){std::cerr<<e.what()<<"\n";return 1;}
}
