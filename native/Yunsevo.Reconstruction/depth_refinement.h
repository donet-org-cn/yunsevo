// Shared CPU/Vulkan post-processing: all checks use measured depth from independent
// views. Interpolation is bounded to a few pixels and never iterates on filled data.
struct DepthRefinementStats { size_t retained=0,rejected=0,filled=0; };
static int depthSupport(int camera,int x,int y,float z,const std::vector<Camera>& cameras,
 const std::vector<std::vector<float>>& depths,const std::vector<std::vector<int>>& neighbors,int side,double focal){
 if(!(z>0)||!std::isfinite(z))return 0;
 double mid=side*.5;const auto&ref=cameras[camera];
 Vec3d world=ref.R.t()*(Vec3d((x-mid)*z/focal,(y-mid)*z/focal,z)-ref.t);int supported=0;
 for(int j:neighbors[camera])if(!depths[j].empty()){
  const auto&other=cameras[j];Vec3d q=other.R*world+other.t;if(q[2]<=0)continue;
  double u=focal*q[0]/q[2]+mid,v=focal*q[1]/q[2]+mid;
  int ix=(int)std::round(u),iy=(int)std::round(v);if(ix<1||iy<1||ix>=side-1||iy>=side-1)continue;
  float d=depths[j][iy*side+ix];if(!(d>0)||!std::isfinite(d)||std::abs(d-q[2])>q[2]*.025)continue;
  Vec3d back=ref.R*(other.R.t()*(Vec3d((ix-mid)*d/focal,(iy-mid)*d/focal,d)-other.t))+ref.t;
  if(back[2]<=0||std::abs(back[2]-z)>z*.025)continue;
  if(std::hypot(focal*back[0]/back[2]+mid-x,focal*back[1]/back[2]+mid-y)<=1.5)supported++;
 }
 return supported;
}
// Inverse depth of a plane is affine in image coordinates. Require support in all
// quadrants, a narrow depth range and a small residual to avoid bridging depth edges.
static float smallPlanarGap(const std::vector<float>& depth,int side,int x,int y){
 if(x<4||y<4||x>=side-4||y>=side-4||depth[y*side+x]>0)return 0;
 Matx33d A=Matx33d::zeros();Vec3d b(0,0,0);int count=0,quadrants=0;
 double low=1e30,high=0;std::vector<std::pair<Vec3d,double>> samples;
 for(int dy=-3;dy<=3;dy++)for(int dx=-3;dx<=3;dx++){
  float z=depth[(y+dy)*side+x+dx];if(!(z>0)||!std::isfinite(z))continue;
  Vec3d p(dx,dy,1);double inverse=1./z;A+=p*p.t();b+=p*inverse;
  samples.emplace_back(p,inverse);count++;low=std::min(low,(double)z);high=std::max(high,(double)z);
  if(dx&&dy)quadrants|=1<<((dx>0?1:0)+(dy>0?2:0));
 }
 if(count<20||quadrants!=15||high>low*1.06)return 0;
 Vec3d plane;if(!cv::solve(A,b,plane,cv::DECOMP_CHOLESKY)||plane[2]<=0)return 0;
 double residual=0;for(const auto&sample:samples){double e=sample.first.dot(plane)-sample.second;residual+=e*e;}
 if(std::sqrt(residual/count)>plane[2]*.006)return 0;
 double z=1./plane[2];return z>=low*.995&&z<=high*1.005?(float)z:0;
}
static DepthRefinementStats refineDepths(const std::vector<Camera>& cameras,std::vector<std::vector<float>>& depths,
 const std::vector<std::vector<int>>& neighbors,int side,double focal,Progress report){
 DepthRefinementStats stats;auto measured=depths;
 for(size_t i=0;i<depths.size();i++)if(!depths[i].empty()){
  report(83,"fine depth consistency "+std::to_string(i+1)+"/"+std::to_string(depths.size()));
  for(int y=0;y<side;y++)for(int x=0;x<side;x++){
   float&z=depths[i][y*side+x];if(!(z>0)||!std::isfinite(z)){z=0;continue;}
   if(depthSupport((int)i,x,y,z,cameras,measured,neighbors,side,focal)<1){z=0;stats.rejected++;}else stats.retained++;
  }
 }
 // Read only the filtered measurements; synthesized depths cannot validate each other.
 measured=depths;
 for(size_t i=0;i<depths.size();i++)if(!depths[i].empty()){
  report(84,"fine supported small gaps "+std::to_string(i+1)+"/"+std::to_string(depths.size()));
  for(int y=4;y<side-4;y++)for(int x=4;x<side-4;x++)if(depths[i][y*side+x]==0){
   float z=smallPlanarGap(measured[i],side,x,y);
   if(z>0&&depthSupport((int)i,x,y,z,cameras,measured,neighbors,side,focal)>=2){depths[i][y*side+x]=z;stats.filled++;}
  }
 }
 report(84,"fine depth retained "+std::to_string(stats.retained)+" rejected "+std::to_string(stats.rejected)+" supported gaps "+std::to_string(stats.filled));
 return stats;
}
