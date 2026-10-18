#include "engine.h"
#include "compute.h"
#include <opencv2/opencv.hpp>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <numeric>
#include <set>
#include <unordered_map>
#include <sstream>
#include <stdexcept>
namespace fs=std::filesystem;
using cv::Mat;using cv::Matx33d;using cv::Matx34d;using cv::Vec3d;using cv::Point2f;using cv::Point3d;
static thread_local std::string errorText,capabilities;
// Read only bounded TIFF IFD entries from JPEG EXIF. No MakerNote or untrusted offsets are followed.
static bool boundedImage(const std::vector<unsigned char>&b){
 uint32_t w=0,h=0;
 if(b.size()>=24&&b[0]==137&&std::memcmp(b.data()+1,"PNG\r\n\032\n",7)==0){for(int i=0;i<4;i++){w=(w<<8)|b[16+i];h=(h<<8)|b[20+i];}}
 else if(b.size()>4&&b[0]==255&&b[1]==216){for(size_t p=2;p+9<b.size();){if(b[p]!=255)break;unsigned m=b[p+1];size_t len=((size_t)b[p+2]<<8)|b[p+3];if(len<2||p+2+len>b.size())break;if((m>=0xc0&&m<=0xcf)&&m!=0xc4&&m!=0xc8&&m!=0xcc){h=(b[p+5]<<8)|b[p+6];w=(b[p+7]<<8)|b[p+8];break;}if(m==0xda||m==0xd9)break;p+=2+len;}}
 return w>0&&h>0&&w<=16000&&h<=16000&&(uint64_t)w*h<=80000000;
}
static double focal35mm(const std::vector<unsigned char>& bytes){
 for(size_t pos=2;pos+4<bytes.size()&&pos<1024*1024;){
  if(bytes[pos]!=255)break;unsigned marker=bytes[pos+1];if(marker==0xda||marker==0xd9)break;
  size_t len=((size_t)bytes[pos+2]<<8)|bytes[pos+3];if(len<2||pos+2+len>bytes.size())break;
  if(marker==0xe1&&len>=16&&std::memcmp(bytes.data()+pos+4,"Exif\0\0",6)==0){
   size_t start=pos+10,end=pos+2+len;bool le=bytes[start]=='I'&&bytes[start+1]=='I';if(!le&&!(bytes[start]=='M'&&bytes[start+1]=='M'))return 0;
   auto u16=[&](size_t off)->uint16_t{if(off+2>end-start)return 0;const auto*p=bytes.data()+start+off;return le?(p[0]|p[1]<<8):(p[0]<<8|p[1]);};
   auto u32=[&](size_t off)->uint32_t{if(off+4>end-start)return 0;return le?((uint32_t)u16(off)|((uint32_t)u16(off+2)<<16)):(((uint32_t)u16(off)<<16)|u16(off+2));};
   uint32_t ifd=u32(4),exif=0;
   for(int pass=0;pass<2;pass++){
    if(ifd>end-start-2)return 0;uint16_t count=u16(ifd);if(count>512||ifd+2+(size_t)count*12>end-start)return 0;
    for(unsigned n=0;n<count;n++){size_t at=ifd+2+n*12;auto tag=u16(at),type=u16(at+2);auto countValue=u32(at+4);
     if(tag==0x8769&&type==4&&countValue==1)exif=u32(at+8);
     if(tag==0xa405&&type==3&&countValue==1){unsigned f=u16(at+8);return f>=5&&f<=500?f:0;}
    }
    if(!exif)break;ifd=exif;exif=0;
   }
  }
  pos+=2+len;
 }
 return 0;
}

static std::string safe(std::string x){for(auto&c:x)if(c=='"'||c=='\\'||(unsigned char)c<32)c=' ';return x;}
struct Camera{fs::path source;cv::Rect photoRect;int sourceSide=0;Mat color,gray,desc;std::vector<cv::KeyPoint> features;Matx33d R=Matx33d::eye();Vec3d t{0,0,0};bool registered=false;int offset=0;};
struct Track{std::map<int,int> observations;Point3d point;bool good=false;};
struct Pair{int a,b;std::vector<cv::DMatch> matches;Mat R,t;double score=0;};
struct Progress{YvProgress callback;void*user;std::shared_ptr<int> maximum=std::make_shared<int>(0);void operator()(int n,const std::string&s){n=std::max(n,*maximum);*maximum=n;if(callback&&callback(user,n,s.c_str()))throw std::runtime_error("CANCELLED");}};
static Matx34d projection(const Camera&c){return {c.R(0,0),c.R(0,1),c.R(0,2),c.t[0],c.R(1,0),c.R(1,1),c.R(1,2),c.t[1],c.R(2,0),c.R(2,1),c.R(2,2),c.t[2]};}
static Vec3d center(const Camera&c){return -c.R.t()*c.t;}
static double reprojection(const Camera&c,Point3d p,Point2f pixel,double f,double mid){Vec3d q=c.R*Vec3d(p.x,p.y,p.z)+c.t;if(q[2]<=0)return 1e9;return std::hypot(f*q[0]/q[2]+mid-pixel.x,f*q[1]/q[2]+mid-pixel.y);}
static bool triangulate(Track&track,const std::vector<Camera>&cs,double f,double mid){
 int a=-1,b=-1;double separation=0;
 for(auto[x,fx]:track.observations)if(cs[x].registered)for(auto[y,fy]:track.observations)if(y>x&&cs[y].registered){double d=cv::norm(center(cs[x])-center(cs[y]));if(d>separation){separation=d;a=x;b=y;}}
 if(a<0||separation<1e-4)return false;
 auto pa=cs[a].features[track.observations.at(a)].pt,pb=cs[b].features[track.observations.at(b)].pt;
 std::vector<Point2f> aa{{(float)((pa.x-mid)/f),(float)((pa.y-mid)/f)}},bb{{(float)((pb.x-mid)/f),(float)((pb.y-mid)/f)}};Mat point;
 cv::triangulatePoints(projection(cs[a]),projection(cs[b]),aa,bb,point);point.convertTo(point,CV_64F);
 double w=point.at<double>(3);if(std::abs(w)<1e-9)return false;Point3d p(point.at<double>(0)/w,point.at<double>(1)/w,point.at<double>(2)/w);
 if(!std::isfinite(p.x+p.y+p.z)||cv::norm(p)>1e5)return false;
 int observed=0,inliers=0;for(auto[i,k]:track.observations)if(cs[i].registered){observed++;if(reprojection(cs[i],p,cs[i].features[k].pt,f,mid)<4)inliers++;}
 if(inliers<2||inliers<observed*.7)return false;
 Vec3d r1=Vec3d(p.x,p.y,p.z)-center(cs[a]),r2=Vec3d(p.x,p.y,p.z)-center(cs[b]);double cosine=r1.dot(r2)/(cv::norm(r1)*cv::norm(r2));if(cosine>.99985)return false;
 track.point=p;track.good=true;return true;
}
#include "bundle_adjustment.h"
#include "resection.h"
static std::vector<Track> sfm(std::vector<Camera>&cs,double& f,bool estimateFocal,bool object,Progress report){
 int count=(int)cs.size(),total=0;const double mid=cs[0].gray.cols*.5;
 auto sift=cv::SIFT::create(6000);cv::setNumThreads(2);
 for(int i=0;i<count;i++){cs[i].offset=total;Mat mask=Mat::zeros(cs[i].gray.size(),CV_8U);auto rect=cs[i].photoRect;if(object){int dx=rect.width/12,dy=rect.height/40;rect=cv::Rect(rect.x+dx,rect.y+dy,rect.width-2*dx,rect.height-2*dy);}mask(rect).setTo(255);sift->detectAndCompute(cs[i].gray,mask,cs[i].features,cs[i].desc);total+=(int)cs[i].features.size();report(3+10*i/count,"features "+std::to_string(i+1)+"/"+std::to_string(count));}
 std::vector<int> parent(total);std::iota(parent.begin(),parent.end(),0);
 std::vector<std::array<uint64_t,2>> membership(total,std::array<uint64_t,2>{0,0});
 for(int i=0;i<count;i++)for(size_t k=0;k<cs[i].features.size();k++)membership[cs[i].offset+k][i/64]|=1ull<<(i%64);
 auto root=[&](int n){while(parent[n]!=n){parent[n]=parent[parent[n]];n=parent[n];}return n;};
 std::vector<Pair> pairs;cv::FlannBasedMatcher matcher(cv::makePtr<cv::flann::KDTreeIndexParams>(4),cv::makePtr<cv::flann::SearchParams>(96));int compared=0;
 for(int a=0;a<count;a++)for(int b=a+1;b<count;b++){
  if(b-a>6&&!(a<3&&b>=count-3))continue;if(cs[a].desc.empty()||cs[b].desc.empty())continue;
  std::vector<std::vector<cv::DMatch>> matches;matcher.knnMatch(cs[a].desc,cs[b].desc,matches,2);Pair pair{a,b};std::vector<Point2f> ap,bp;
  for(auto&m:matches)if(m.size()==2&&m[0].distance<.78*m[1].distance){pair.matches.push_back(m[0]);ap.push_back(cs[a].features[m[0].queryIdx].pt);bp.push_back(cs[b].features[m[0].trainIdx].pt);}
  if(ap.size()<18)continue;
  Mat mask;Mat E=cv::findEssentialMat(ap,bp,f,cv::Point2d(mid,mid),cv::RANSAC,.999,1.5,mask);if(E.empty()||E.rows!=3)continue;
  int good=cv::recoverPose(E,ap,bp,pair.R,pair.t,f,cv::Point2d(mid,mid),mask);Mat trackMask;cv::findFundamentalMat(ap,bp,cv::USAC_MAGSAC,1.5,.999,trackMask);if(trackMask.empty())continue;
  std::vector<cv::DMatch> keep;std::vector<double> angles;
  Matx33d R;
  for(int y=0;y<3;y++)for(int x=0;x<3;x++)R(y,x)=pair.R.at<double>(y,x);
  for(size_t i=0;i<pair.matches.size();i++)if(trackMask.at<unsigned char>((int)i)){
   auto m=pair.matches[i];keep.push_back(m);int ra=root(cs[a].offset+m.queryIdx),rb=root(cs[b].offset+m.trainIdx);if(ra!=rb&&!(membership[ra][0]&membership[rb][0])&&!(membership[ra][1]&membership[rb][1])){parent[rb]=ra;membership[ra][0]|=membership[rb][0];membership[ra][1]|=membership[rb][1];}
   Vec3d u((ap[i].x-mid)/f,(ap[i].y-mid)/f,1),v((bp[i].x-mid)/f,(bp[i].y-mid)/f,1);v=R.t()*v;angles.push_back(std::acos(std::clamp(u.dot(v)/(cv::norm(u)*cv::norm(v)),-1.,1.))*180/CV_PI);
  }
  std::sort(angles.begin(),angles.end());double angle=angles.empty()?0:angles[angles.size()/2];

  pair.matches=std::move(keep);pair.score=angle>1&&angle<75&&good>=80?good*std::min(angle,6.)*std::pow(good/(double)ap.size(),3):0;pairs.push_back(std::move(pair));
  report(14+std::min(13,++compared*13/std::max(1,count*4)),"matching overlapping views");
 }
 if(pairs.empty())throw std::runtime_error("No overlapping views. Use sharp, textured photos with 70% overlap.");
 auto initial=std::max_element(pairs.begin(),pairs.end(),[](const Pair&a,const Pair&b){return a.score<b.score;});if(initial->score<=0)throw std::runtime_error("Insufficient camera movement / parallax");
 std::map<int,Track> grouped;std::set<int> bad;
 for(int i=0;i<count;i++)for(int k=0;k<(int)cs[i].features.size();k++){int r=root(cs[i].offset+k);auto&tr=grouped[r];if(tr.observations.count(i))bad.insert(r);tr.observations[i]=k;}
 std::vector<Track> baseTracks;for(auto&[r,tr]:grouped)if(!bad.count(r)&&tr.observations.size()>=2)baseTracks.push_back(std::move(tr));
 std::vector<Pair*> seeds{&*initial};
 for(int region=0;region<3;region++){Pair*best=nullptr;for(auto&p:pairs)if(p.score>0&&p.a*3/count==region&&(!best||p.score>best->score))best=&p;if(best&&std::find(seeds.begin(),seeds.end(),best)==seeds.end())seeds.push_back(best);}
 std::vector<Camera> bestCameras;std::vector<Track> bestTracks;int bestRegistered=0;size_t bestPoints=0;double bestFocal=f,initialFocal=f;
 std::vector<double> guesses{f};if(estimateFocal){guesses.push_back(cs[0].gray.cols*.75);guesses.push_back(cs[0].gray.cols*.55);}
 int attempts=0;
 for(double guess:guesses){for(auto*seed:seeds){
  if(++attempts>9)break;f=guess;for(auto&c:cs){c.registered=false;c.R=Matx33d::eye();c.t=Vec3d(0,0,0);}
  std::vector<Point2f> ap,bp;for(auto&m:seed->matches){ap.push_back(cs[seed->a].features[m.queryIdx].pt);bp.push_back(cs[seed->b].features[m.trainIdx].pt);}
  Mat mask,E=cv::findEssentialMat(ap,bp,f,cv::Point2d(mid,mid),cv::RANSAC,.999,1.5,mask),R,t;if(E.empty()||E.rows!=3||cv::recoverPose(E,ap,bp,R,t,f,cv::Point2d(mid,mid),mask)<30)continue;
  report(27,"initial pair "+std::to_string(seed->a)+" / "+std::to_string(seed->b)+" focal "+std::to_string(f)+" attempt "+std::to_string(attempts));
  cs[seed->a].registered=cs[seed->b].registered=true;for(int y=0;y<3;y++){cs[seed->b].t[y]=t.at<double>(y);for(int x=0;x<3;x++)cs[seed->b].R(y,x)=R.at<double>(y,x);}
  auto tracks=baseTracks;for(auto&tr:tracks)triangulate(tr,cs,f,mid);
  Mat K=(cv::Mat_<double>(3,3)<<f,0,mid,0,f,mid,0,0,1);
 std::set<int> rejected;bool rescued=false;
 for(int step=0;step<count*2;step++){
  int best=-1;std::vector<cv::Point3f> objects;std::vector<Point2f> pixels;
  for(int i=0;i<count;i++)if(!cs[i].registered&&!rejected.count(i)){std::vector<cv::Point3f> op;std::vector<Point2f> ip;for(auto&tr:tracks)if(tr.good&&tr.observations.count(i)){op.emplace_back(tr.point);ip.push_back(cs[i].features[tr.observations.at(i)].pt);}if(op.size()>objects.size()){best=i;objects=std::move(op);pixels=std::move(ip);}}
  if(best<0||objects.size()<18){if(!rescued){bundleAdjust(cs,tracks,f,seed->a,seed->b,estimateFocal,report,10);K.at<double>(0,0)=K.at<double>(1,1)=f;for(auto&tr:tracks)if(!tr.good)triangulate(tr,cs,f,mid);rejected.clear();rescued=true;continue;}break;}report(28,"pnp view "+std::to_string(best)+" points "+std::to_string(objects.size()));
  Mat r,t,inliers;if(!cv::solvePnPRansac(objects,pixels,K,cv::noArray(),r,t,false,1000,3,.999,inliers,cv::SOLVEPNP_EPNP)||inliers.rows<18){report(28,"pnp rejected "+std::to_string(best)+" inliers "+std::to_string(inliers.rows));rejected.insert(best);continue;}
  std::vector<cv::Point3f> op;std::vector<Point2f> ip;for(int k=0;k<inliers.rows;k++){int n=inliers.at<int>(k);op.push_back(objects[n]);ip.push_back(pixels[n]);}cv::solvePnPRefineLM(op,ip,K,cv::noArray(),r,t);
  Mat R;cv::Rodrigues(r,R);for(int y=0;y<3;y++){cs[best].t[y]=t.at<double>(y);for(int x=0;x<3;x++)cs[best].R(y,x)=R.at<double>(y,x);}cs[best].registered=true;rejected.clear();
  for(auto&tr:tracks)if(!tr.good)triangulate(tr,cs,f,mid);
  report(28+step*10/count,"camera registration "+std::to_string(step+3)+"/"+std::to_string(count));
 }

  int registered=0;for(auto&c:cs)registered+=c.registered;
  if(registered>=4&&registered<count*.94){recoverViews(cs,tracks,f,report);registered=0;for(auto&c:cs)registered+=c.registered;}
  size_t good=std::count_if(tracks.begin(),tracks.end(),[](const auto&t){return t.good;});
  report(38,"registration candidate "+std::to_string(registered)+"/"+std::to_string(count)+" points "+std::to_string(good)+" focal "+std::to_string(f));
  if(registered>bestRegistered||(registered==bestRegistered&&good>bestPoints)){bestCameras=cs;bestTracks=std::move(tracks);bestFocal=f;bestRegistered=registered;bestPoints=good;}
  if(bestRegistered>=std::ceil(count*.94))break;
 }if(bestRegistered>=std::ceil(count*.94))break;}
 if(bestRegistered<std::max(4,(int)std::ceil(count*.7)))throw std::runtime_error("Only "+std::to_string(bestRegistered)+"/"+std::to_string(count)+" cameras registered after multiple initialization attempts. Add intermediate overlapping views.");
 cs=std::move(bestCameras);auto tracks=std::move(bestTracks);f=bestFocal;
 // Anchor the strongest baseline in the selected reconstruction, then refine all poses jointly.
 int anchor=-1,baseline=-1;for(int i=0;i<count;i++)if(cs[i].registered){if(anchor<0)anchor=i;else if(baseline<0)baseline=i;}
 bundleAdjust(cs,tracks,f,anchor,baseline,estimateFocal,report,15);
 for(auto&tr:tracks)if(tr.good){int support=0;for(auto it=tr.observations.begin();it!=tr.observations.end();){auto[i,k]=*it;if(cs[i].registered){if(reprojection(cs[i],tr.point,cs[i].features[k].pt,f,mid)>4){it=tr.observations.erase(it);continue;}support++;}++it;}tr.good=support>=2;}
 return tracks;
}
struct Vertex{float x,y,z,nx,ny,nz,r,g,b;};

static void fuse(const Vec3d* objectCenter,double objectRadius,const std::vector<Camera>& cameras,const std::vector<Mat>& colors,const std::vector<std::vector<float>>& depths,int side,float f,int quality,std::vector<Vertex>& vertices,std::vector<uint32_t>& indices,Progress report){
 if(vertices.size()<1000)throw std::runtime_error("Too few consistent depth samples");
 float lo[3],hi[3];std::vector<float> values;values.reserve(vertices.size());
 for(int axis=0;axis<3;axis++){
  values.clear();for(auto&v:vertices)values.push_back(axis==0?v.x:axis==1?v.y:v.z);
  size_t a=values.size()/100,b=values.size()*99/100;std::nth_element(values.begin(),values.begin()+a,values.end());lo[axis]=values[a];std::nth_element(values.begin(),values.begin()+b,values.end());hi[axis]=values[b];
 }
 if(objectCenter)for(int a=0;a<3;a++){double sign=a? -1:1;lo[a]=(float)(sign*(*objectCenter)[a]-objectRadius);hi[a]=(float)(sign*(*objectCenter)[a]+objectRadius);}
 const int N=quality?192:144;float step=std::max({hi[0]-lo[0],hi[1]-lo[1],hi[2]-lo[2]})/(N-10),trunc=step*3.5f;
 if(!std::isfinite(step)||step<=0)throw std::runtime_error("Invalid reconstruction bounds");
 for(int a=0;a<3;a++)lo[a]-=step*5;
 struct Voxel{float sdf=1,r=0,g=0,b=0;unsigned short weight=0;};std::vector<Voxel> grid((size_t)N*N*N);
 auto index=[&](int x,int y,int z){return (z*N+y)*N+x;};float mid=side*.5f;
 // Signed distance fusion averages independent camera depth measurements; no hull or invented back faces.
 for(int z=0;z<N;z++){
  if(z%8==0)report(90+z*6/N,"fusing depth volume");
  for(int y=0;y<N;y++)for(int x=0;x<N;x++){
   Vec3d world(lo[0]+x*step,-(lo[1]+y*step),-(lo[2]+z*step));float sum=0;auto&v=grid[index(x,y,z)];
   for(int c=0;c<(int)cameras.size();c++)if(!depths[c].empty()){
    Vec3d q=cameras[c].R*world+cameras[c].t;if(q[2]<=0)continue;int u=(int)std::round(f*q[0]/q[2]+mid),vv=(int)std::round(f*q[1]/q[2]+mid);if(u<3||vv<3||u>=side-3||vv>=side-3)continue;
    float depth=depths[c][vv*side+u];if(depth<=0)continue;float delta=(depth-(float)q[2])/trunc;if(delta < -1||delta>3)continue;
    sum+=std::min(1.f,delta);auto color=colors[c].at<cv::Vec3b>(vv,u);v.r+=color[2]/255.f;v.g+=color[1]/255.f;v.b+=color[0]/255.f;v.weight++;
   }
   if(v.weight){v.sdf=sum/v.weight;v.r/=v.weight;v.g/=v.weight;v.b/=v.weight;}
  }
 }
 vertices.clear();indices.clear();std::unordered_map<uint64_t,uint32_t> edges;
 const int corners[8][3]={{0,0,0},{1,0,0},{1,1,0},{0,1,0},{0,0,1},{1,0,1},{1,1,1},{0,1,1}};
 const int tets[6][4]={{0,5,1,6},{0,1,2,6},{0,2,3,6},{0,3,7,6},{0,7,4,6},{0,4,5,6}};
 auto point=[&](int id){int z=id/(N*N),y=id/N%N,x=id%N;return Vec3d(lo[0]+x*step,lo[1]+y*step,lo[2]+z*step);};
 auto gradient=[&](int id){int z=id/(N*N),y=id/N%N,x=id%N;Vec3d g(0,0,0);if(x>0&&x<N-1)g[0]=grid[id+1].sdf-grid[id-1].sdf;if(y>0&&y<N-1)g[1]=grid[id+N].sdf-grid[id-N].sdf;if(z>0&&z<N-1)g[2]=grid[id+N*N].sdf-grid[id-N*N].sdf;double len=cv::norm(g);return len>1e-8?g/len:Vec3d(0,1,0);};
 auto vertex=[&](int a,int b){
  uint64_t key=((uint64_t)std::min(a,b)<<32)|(uint32_t)std::max(a,b);auto found=edges.find(key);if(found!=edges.end())return found->second;
  auto&va=grid[a];auto&vb=grid[b];float t=std::clamp(va.sdf/(va.sdf-vb.sdf),0.f,1.f);Vec3d p=point(a)*(1-t)+point(b)*t,n=gradient(a)*(1-t)+gradient(b)*t;double len=cv::norm(n);if(len>0)n/=len;
  uint32_t id=(uint32_t)vertices.size();vertices.push_back({(float)p[0],(float)p[1],(float)p[2],(float)n[0],(float)n[1],(float)n[2],va.r*(1-t)+vb.r*t,va.g*(1-t)+vb.g*t,va.b*(1-t)+vb.b*t});edges[key]=id;return id;
 };
 auto tri=[&](uint32_t a,uint32_t b,uint32_t c){const auto&x=vertices[a];const auto&y=vertices[b];const auto&z=vertices[c];Vec3d n=Vec3d(y.x-x.x,y.y-x.y,y.z-x.z).cross(Vec3d(z.x-x.x,z.y-x.y,z.z-x.z));if(cv::norm(n)<step*step*1e-5)return;if(n.dot(Vec3d(x.nx+y.nx+z.nx,x.ny+y.ny+z.ny,x.nz+y.nz+z.nz))<0)std::swap(b,c);indices.insert(indices.end(),{a,b,c});};
 for(int z=0;z<N-1;z++)for(int y=0;y<N-1;y++)for(int x=0;x<N-1;x++){
  int ids[8];bool valid=true;for(int c=0;c<8;c++){ids[c]=index(x+corners[c][0],y+corners[c][1],z+corners[c][2]);if(grid[ids[c]].weight<2){valid=false;break;}}if(!valid)continue;
  for(const auto&tet:tets){int in[4],out[4],ni=0,no=0;for(int c:tet){if(grid[ids[c]].sdf<0)in[ni++]=ids[c];else out[no++]=ids[c];}if(ni==0||ni==4)continue;
   if(ni==1)tri(vertex(in[0],out[0]),vertex(in[0],out[1]),vertex(in[0],out[2]));
   else if(ni==3)tri(vertex(out[0],in[0]),vertex(out[0],in[1]),vertex(out[0],in[2]));
   else{auto a=vertex(in[0],out[0]),b=vertex(in[0],out[1]),c=vertex(in[1],out[0]),d=vertex(in[1],out[1]);tri(a,b,c);tri(b,d,c);}
  }
 }
 // Drop tiny disconnected artifacts. Preserve multiple substantial components for equipment / room scans.
 std::vector<uint32_t> parents(vertices.size());std::iota(parents.begin(),parents.end(),0);
 auto root=[&](uint32_t n){while(parents[n]!=n){parents[n]=parents[parents[n]];n=parents[n];}return n;};
 for(size_t i=0;i<indices.size();i+=3){auto a=root(indices[i]);for(int k=1;k<3;k++){auto b=root(indices[i+k]);if(a!=b)parents[b]=a;}}
 std::vector<size_t> counts(vertices.size());for(size_t i=0;i<indices.size();i+=3)counts[root(indices[i])]++;
 std::vector<uint32_t> kept;for(size_t i=0;i<indices.size();i+=3)if(counts[root(indices[i])]>=std::max<size_t>(40,indices.size()/3000))kept.insert(kept.end(),{indices[i],indices[i+1],indices[i+2]});indices.swap(kept);
 std::vector<Vertex> compact;std::vector<uint32_t> remap(vertices.size(),UINT32_MAX);for(auto&id:indices){if(remap[id]==UINT32_MAX){remap[id]=(uint32_t)compact.size();compact.push_back(vertices[id]);}id=remap[id];}vertices.swap(compact);
}
#include "texture_mesh.h"
const char* yv_last_error(){return errorText.c_str();}
const char* yv_capabilities(){try{VulkanDepth vk;capabilities="{\"cpu\":true,\"vulkan\":true,\"device\":\""+safe(vk.name())+"\",\"experimental\":true}";}catch(const std::exception&e){capabilities="{\"cpu\":true,\"vulkan\":false,\"reason\":\""+safe(e.what())+"\",\"experimental\":true}";}return capabilities.c_str();}
int yv_reconstruct(const char*input,const char*output,const char*requested,int quality,YvProgress callback,void*user){return yv_reconstruct_v2(input,output,requested,quality,"object",callback,user);}
int yv_reconstruct_v2(const char*input,const char*output,const char*requested,int quality,const char*subject,YvProgress callback,void*user){
 errorText.clear();try{
  const bool object=subject&&std::string(subject)=="object";if(!subject||(!object&&std::string(subject)!="scene"))throw std::runtime_error("Unsupported scan subject");
  Progress report{callback,user};std::string backend=requested?requested:"auto";if(backend!="auto"&&backend!="cpu"&&backend!="vulkan")throw std::runtime_error("Unsupported compute backend");
  std::unique_ptr<VulkanDepth> gpu;if(backend!="cpu")try{gpu=std::make_unique<VulkanDepth>();}catch(const std::exception&e){if(backend=="vulkan")throw;report(0,std::string("Vulkan unavailable; CPU fallback: ")+e.what());}
  backend=gpu?"vulkan: "+gpu->name():"cpu";report(1,backend);
  std::vector<fs::path> paths;for(auto&it:fs::directory_iterator(fs::u8path(input))){if(!it.is_regular_file())continue;std::string ext=it.path().extension().string();std::transform(ext.begin(),ext.end(),ext.begin(),::tolower);if(ext==".jpg"||ext==".jpeg"||ext==".png")paths.push_back(it.path());}
  std::sort(paths.begin(),paths.end());if(paths.size()<6||paths.size()>120)throw std::runtime_error("Use 6 to 120 photos (24 to 72 recommended)");
  const int side=1024,dense=quality?384:256;double focal=side*1.1;
  std::vector<double> measuredFocal;
  std::vector<Camera> cameras;for(auto&p:paths){
   if(fs::file_size(p)>40*1024*1024)throw std::runtime_error("Photo exceeds 40 MB");
   // imdecode supports UTF-8 filenames on Windows; OpenCV applies JPEG EXIF orientation.
   std::ifstream stream(p,std::ios::binary);std::vector<unsigned char> bytes((std::istreambuf_iterator<char>(stream)),{});if(!boundedImage(bytes))throw std::runtime_error("Unsupported image or image exceeds 80 megapixels / 16000 pixels per side");Mat image=cv::imdecode(bytes,cv::IMREAD_COLOR);if(image.empty()||image.total()>80000000)throw std::runtime_error("Invalid / oversized photo");
   double scale=side/(double)std::max(image.cols,image.rows);double f35=focal35mm(bytes);if(f35>0)measuredFocal.push_back(f35*std::hypot(image.cols,image.rows)/43.2666153*scale);Mat resized;cv::resize(image,resized,cv::Size(),scale,scale,cv::INTER_AREA);Camera c;c.source=p;c.sourceSide=std::max(image.cols,image.rows);c.photoRect=cv::Rect((side-resized.cols)/2,(side-resized.rows)/2,resized.cols,resized.rows);c.color=Mat::zeros(side,side,CV_8UC3);resized.copyTo(c.color(cv::Rect((side-resized.cols)/2,(side-resized.rows)/2,resized.cols,resized.rows)));cv::cvtColor(c.color,c.gray,cv::COLOR_BGR2GRAY);cameras.push_back(std::move(c));
  }
  if(!measuredFocal.empty()){std::sort(measuredFocal.begin(),measuredFocal.end());focal=measuredFocal[measuredFocal.size()/2];if(measuredFocal.back()/measuredFocal.front()>1.12)throw std::runtime_error("Mixed focal lengths: use one camera and fixed zoom for the whole scan");report(2,"EXIF focal calibration "+std::to_string(focal));}
  else report(2,"No EXIF focal calibration; using estimated focal length. If reconstruction fails, recapture with guided camera.");
  auto tracks=sfm(cameras,focal,measuredFocal.empty(),object,report);int registered=0;for(auto&c:cameras)registered+=c.registered;
  Vec3d objectCenter(0,0,0);double objectRadius=0;
  if(object){Matx33d A=Matx33d::zeros();Vec3d b(0,0,0);for(const auto&c:cameras)if(c.registered){Vec3d d=c.R.t()*Vec3d(0,0,1);auto P=Matx33d::eye()-d*d.t();A+=P;b+=P*center(c);}objectCenter=A.inv(cv::DECOMP_SVD)*b;
   std::vector<double> radii;for(const auto&c:cameras)if(c.registered){Vec3d q=c.R*objectCenter+c.t;if(q[2]>0)radii.push_back(q[2]*std::max(c.photoRect.width,c.photoRect.height)*.48/focal);}
   if(radii.size()<4)throw std::runtime_error("Cannot locate centered subject. Keep a stationary object centered while walking around it.");std::sort(radii.begin(),radii.end());objectRadius=radii[radii.size()/2];report(42,"focused object bounds radius "+std::to_string(objectRadius));
  }
  std::vector<Mat> colors;std::vector<std::vector<float>> depths(cameras.size());
  std::vector<std::vector<int>> neighbors(cameras.size());float f=(float)(focal*dense/side),mid=dense*.5f;
  for(auto&c:cameras){Mat small;cv::resize(c.color,small,{dense,dense},0,0,cv::INTER_AREA);colors.push_back(small);}
  for(int i=0;i<(int)cameras.size();i++)if(cameras[i].registered){
   auto&ref=cameras[i];std::vector<std::pair<double,int>> scores;
   for(int j=0;j<(int)cameras.size();j++)if(j!=i&&cameras[j].registered){int common=0;std::vector<double> angle;
    for(auto&tr:tracks)if(tr.good&&tr.observations.count(i)&&tr.observations.count(j)){common++;Vec3d p(tr.point.x,tr.point.y,tr.point.z),a=p-center(ref),b=p-center(cameras[j]);angle.push_back(std::acos(std::clamp(a.dot(b)/(cv::norm(a)*cv::norm(b)),-1.,1.))*180/CV_PI);}
    if(common<15)continue;std::nth_element(angle.begin(),angle.begin()+angle.size()/2,angle.end());double degrees=angle[angle.size()/2];if(degrees<1||degrees>35)continue;
    scores.emplace_back(common*std::min(1.,degrees/8)*std::exp(-std::pow((degrees-12)/18,2)),j);
   }
   std::sort(scores.rbegin(),scores.rend());if(scores.size()<2)continue;for(size_t n=0;n<std::min<size_t>(3,scores.size());n++)neighbors[i].push_back(scores[n].second);
   std::vector<double> zs;for(auto&tr:tracks)if(tr.good&&tr.observations.count(i)){Vec3d q=ref.R*Vec3d(tr.point.x,tr.point.y,tr.point.z)+ref.t;if(q[2]>0)zs.push_back(q[2]);}if(zs.size()<20)continue;std::sort(zs.begin(),zs.end());
   if(object){Vec3d q=ref.R*objectCenter+ref.t;zs={std::max(.01,q[2]-objectRadius*1.2),q[2]+objectRadius*1.2};}
   DepthJob job{dense,dense,quality?96:64,(int)neighbors[i].size(),(float)std::max(.001,zs[zs.size()/20]*(object?1.:.75)),(float)(zs[zs.size()*19/20]*(object?1.:1.25)),f,mid,mid};
   auto append=[&](int n){Mat gray,values;cv::cvtColor(colors[n],gray,cv::COLOR_BGR2GRAY);gray.convertTo(values,CV_32F,1/255.);job.pixels.insert(job.pixels.end(),values.ptr<float>(),values.ptr<float>()+values.total());};append(i);
   for(int j:neighbors[i]){append(j);Matx33d R=cameras[j].R*ref.R.t();Vec3d t=cameras[j].t-R*ref.t;std::array<float,16> tf{};for(int y=0;y<3;y++){for(int x=0;x<3;x++)tf[y*4+x]=(float)R(y,x);tf[y*4+3]=(float)t[y];}job.transforms.push_back(tf);}
   report(42+i*40/(int)cameras.size(),"dense depth "+std::to_string(i+1)+"/"+std::to_string(cameras.size())+" "+backend);
   if(gpu)try{depths[i]=gpu->run(job);}catch(const std::exception&e){if(std::string(requested)=="vulkan")throw;report(42+i*40/(int)cameras.size(),std::string("Vulkan failed; CPU fallback: ")+e.what());gpu.reset();backend="cpu (Vulkan fallback)";depths[i]=cpuDepth(job);}else depths[i]=cpuDepth(job);
  }
  if(object)for(size_t i=0;i<depths.size();i++)if(!depths[i].empty())for(int y=0;y<dense;y++)for(int x=0;x<dense;x++){auto&z=depths[i][y*dense+x];if(z<=0)continue;Vec3d p=cameras[i].R.t()*(Vec3d((x-mid)*z/f,(y-mid)*z/f,z)-cameras[i].t);if(std::abs(p[0]-objectCenter[0])>objectRadius||std::abs(p[1]-objectCenter[1])>objectRadius||std::abs(p[2]-objectCenter[2])>objectRadius)z=0;}
  std::vector<Vertex> vertices;std::vector<uint32_t> indices;
  for(int i=0;i<(int)cameras.size();i++)if(!depths[i].empty()){
   report(83+i*6/(int)cameras.size(),"fusing consistent surfaces");const auto&c=cameras[i];auto&dep=depths[i];std::vector<int> map(dense*dense,-1);
   for(int y=3;y<dense-3;y++)for(int x=3;x<dense-3;x++){
    float z=dep[y*dense+x];if(z<=0||!std::isfinite(z))continue;Vec3d world=c.R.t()*(Vec3d((x-mid)*z/f,(y-mid)*z/f,z)-c.t);bool consistent=false;
    for(int j:neighbors[i])if(!depths[j].empty()){Vec3d q=cameras[j].R*world+cameras[j].t;if(q[2]<=0)continue;int u=(int)std::round(f*q[0]/q[2]+mid),v=(int)std::round(f*q[1]/q[2]+mid);if(u<0||v<0||u>=dense||v>=dense)continue;float d=depths[j][v*dense+u];if(d>0&&std::abs(d-q[2])<q[2]*.035){consistent=true;break;}}
    if(!consistent)continue;auto color=colors[i].at<cv::Vec3b>(y,x);map[y*dense+x]=(int)vertices.size();vertices.push_back({(float)world[0],(float)-world[1],(float)-world[2],0,0,0,color[2]/255.f,color[1]/255.f,color[0]/255.f});
   }
   auto triangle=[&](int a,int b,int cc){int ia=map[a],ib=map[b],ic=map[cc];if(ia<0||ib<0||ic<0)return;float low=std::min({dep[a],dep[b],dep[cc]}),high=std::max({dep[a],dep[b],dep[cc]});if(high-low>low*.04f)return;auto&va=vertices[ia];auto&vb=vertices[ib];auto&vc=vertices[ic];Vec3d normal=Vec3d(vb.x-va.x,vb.y-va.y,vb.z-va.z).cross(Vec3d(vc.x-va.x,vc.y-va.y,vc.z-va.z));double length=cv::norm(normal);if(length<1e-12)return;normal/=length;for(auto idx:{ia,ib,ic}){vertices[idx].nx+=(float)normal[0];vertices[idx].ny+=(float)normal[1];vertices[idx].nz+=(float)normal[2];}indices.insert(indices.end(),{(uint32_t)ia,(uint32_t)ib,(uint32_t)ic});};
   for(int y=3;y<dense-4;y++)for(int x=3;x<dense-4;x++){int a=y*dense+x;triangle(a,a+1,a+dense);triangle(a+1,a+dense+1,a+dense);}
  }
  report(90,"fusing depth measurements");fuse(object?&objectCenter:nullptr,objectRadius,cameras,colors,depths,dense,f,quality,vertices,indices,report);
  if(indices.size()>3000000)throw std::runtime_error("Surface exceeds mobile mesh budget");for(auto&v:vertices){auto linear=[](float c){return c<=.04045f?c/12.92f:std::pow((c+.055f)/1.055f,2.4f);};v.r=linear(v.r);v.g=linear(v.g);v.b=linear(v.b);float n=std::sqrt(v.nx*v.nx+v.ny*v.ny+v.nz*v.nz);if(n>0){v.nx/=n;v.ny/=n;v.nz/=n;}}
  report(98,"writing GLB mesh");glb(fs::u8path(output),vertices,indices,cameras,depths,focal,dense,quality?4096:2048,backend,registered,report);report(100,"completed");return 0;
 }catch(const std::exception&e){errorText=e.what();return errorText=="CANCELLED"?2:1;}catch(...){errorText="Native reconstruction failed";return 1;}
}
