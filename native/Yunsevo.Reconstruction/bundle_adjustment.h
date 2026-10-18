// Bounded joint camera/point Levenberg-Marquardt with a point Schur complement.
// Fix the first pose and one second-camera translation coordinate to remove gauge freedom.
static void bundleAdjust(std::vector<Camera>& cameras,std::vector<Track>& tracks,double& focal,
 int anchor,int baseline,bool estimateFocal,Progress report,int iterations=12){
 struct Observation{int camera;Point2f pixel;};
 struct Landmark{size_t track;std::vector<Observation> observations;};
 std::vector<Landmark> landmarks;
 std::vector<size_t> order;for(size_t i=0;i<tracks.size();i++)if(tracks[i].good)order.push_back(i);
 std::stable_sort(order.begin(),order.end(),[&](size_t a,size_t b){return tracks[a].observations.size()>tracks[b].observations.size();});
 const double mid=cameras[0].gray.cols*.5,initialFocal=focal;
 for(auto id:order){
  Landmark point{id,{}};auto&tr=tracks[id];
  for(auto [camera,key]:tr.observations)if(cameras[camera].registered&&reprojection(cameras[camera],tr.point,cameras[camera].features[key].pt,focal,mid)<6)
   point.observations.push_back({camera,cameras[camera].features[key].pt});
  if(point.observations.size()>12){auto all=point.observations;point.observations.clear();for(int k=0;k<12;k++)point.observations.push_back(all[k*(all.size()-1)/11]);}
  if(point.observations.size()>=2)landmarks.push_back(std::move(point));if(landmarks.size()>=6000)break;
 }
 if(landmarks.size()<30)return;
 std::vector<std::array<int,6>> map(cameras.size());int size=0,fixed=0;
 for(int k=1;k<3;k++)if(std::abs(cameras[baseline].t[k])>std::abs(cameras[baseline].t[fixed]))fixed=k;
 for(size_t i=0;i<cameras.size();i++)for(int k=0;k<6;k++)map[i][k]=cameras[i].registered&&(int)i!=anchor&& !((int)i==baseline&&k==3+fixed)?size++:-1;
 const int fi=estimateFocal?size++:-1;
 auto objective=[&](const std::vector<Camera>& cs,const std::vector<Point3d>& points,double f){
  double cost=0;
  for(size_t p=0;p<landmarks.size();p++)for(const auto&o:landmarks[p].observations){double e=reprojection(cs[o.camera],points[p],o.pixel,f,mid);if(!std::isfinite(e)||e>1e5)return 1e30;cost+=e<=2?e*e:4*(e-1);}
  if(fi>=0)cost+=std::pow(std::log(f/initialFocal)/.3,2);return cost;
 };
 std::vector<Point3d> points;for(auto&p:landmarks)points.push_back(tracks[p.track].point);
 double cost=objective(cameras,points,focal),initialCost=cost,lambda=.001;
 struct PointBlock{cv::Matx33d inverse;Vec3d gradient;std::vector<int> ids;std::vector<Vec3d> cross;};
 for(int iteration=0;iteration<iterations;iteration++){
  report(38,"bundle adjustment "+std::to_string(iteration+1)+" cost "+std::to_string(cost));
  Mat system=Mat::zeros(size,size,CV_64F),rhs=Mat::zeros(size,1,CV_64F);std::vector<PointBlock> blocks;blocks.reserve(points.size());
  for(size_t p=0;p<points.size();p++){
   cv::Matx33d V=cv::Matx33d::zeros();Vec3d gp(0,0,0);std::map<int,Vec3d> cross;
   for(const auto&o:landmarks[p].observations){
    const auto&c=cameras[o.camera];Vec3d local=c.R*Vec3d(points[p].x,points[p].y,points[p].z),q=local+c.t;
    if(q[2]<=0)continue;double iz=1/q[2],x=focal*q[0]*iz,y=focal*q[1]*iz;
    cv::Vec2d residual(x+mid-o.pixel.x,y+mid-o.pixel.y);double length=cv::norm(residual),weight=length>2?2/length:1;
    cv::Matx<double,2,3> D(focal*iz,0,-x*iz,0,focal*iz,-y*iz);
    cv::Matx<double,3,6> motion{0,local[2],-local[1],1,0,0,-local[2],0,local[0],0,1,0,local[1],-local[0],0,0,0,1};
    auto pose=D*motion;auto point=D*c.R;V+=point.t()*point*weight;gp+=point.t()*residual*weight;
    std::vector<std::pair<int,cv::Vec2d>> jac;
    for(int k=0;k<6;k++)if(map[o.camera][k]>=0)jac.push_back({map[o.camera][k],{pose(0,k),pose(1,k)}});
    if(fi>=0)jac.push_back({fi,{x,y}}); // log-focal parameter remains positive.
    for(auto&[i,a]:jac){rhs.at<double>(i)-=a.dot(residual)*weight;auto it=cross.emplace(i,Vec3d(0,0,0)).first;it->second+=point.t()*a*weight;for(auto&[j,b]:jac)system.at<double>(i,j)+=a.dot(b)*weight;}
   }
   for(int k=0;k<3;k++)V(k,k)+=lambda*std::max(V(k,k),1e-8);
   PointBlock block{V.inv(cv::DECOMP_SVD),gp,{},{}};
   for(auto&[i,w]:cross){block.ids.push_back(i);block.cross.push_back(w);rhs.at<double>(i)+=w.dot(block.inverse*gp);}
   for(size_t i=0;i<block.ids.size();i++){auto a=block.inverse*block.cross[i];for(size_t j=0;j<block.ids.size();j++)system.at<double>(block.ids[i],block.ids[j])-=a.dot(block.cross[j]);}
   blocks.push_back(std::move(block));
  }
  if(fi>=0){double prior=std::log(focal/initialFocal);system.at<double>(fi,fi)+=1/.09;rhs.at<double>(fi)-=prior/.09;}
  for(int i=0;i<size;i++)system.at<double>(i,i)+=lambda*std::max(std::abs(system.at<double>(i,i)),1e-6);
  Mat delta;if(!cv::solve(system,rhs,delta,cv::DECOMP_CHOLESKY)||!cv::checkRange(delta)){lambda*=10;continue;}
  auto next=cameras;auto nextPoints=points;double nextFocal=focal*(fi>=0?std::exp(std::clamp(delta.at<double>(fi),-.15,.15)):1.);
  if(nextFocal<mid*.6||nextFocal>mid*5){lambda*=10;continue;}
  for(size_t i=0;i<cameras.size();i++)if(cameras[i].registered){Vec3d rotation(0,0,0);for(int k=0;k<3;k++){if(map[i][k]>=0)rotation[k]=delta.at<double>(map[i][k]);if(map[i][k+3]>=0)next[i].t[k]+=delta.at<double>(map[i][k+3]);}Mat R;cv::Rodrigues(rotation,R);Matx33d update;std::memcpy(update.val,R.ptr<double>(),9*sizeof(double));next[i].R=update*cameras[i].R;}
  for(size_t p=0;p<points.size();p++){auto&b=blocks[p];Vec3d g=b.gradient;for(size_t k=0;k<b.ids.size();k++)g+=b.cross[k]*delta.at<double>(b.ids[k]);Vec3d d=-b.inverse*g;nextPoints[p]+=Point3d(d);}
  double nextCost=objective(next,nextPoints,nextFocal);
  if(nextCost<cost){double improvement=cost-nextCost;cameras=std::move(next);points=std::move(nextPoints);focal=nextFocal;cost=nextCost;lambda=std::max(1e-7,lambda*.3);if(improvement<std::max(1e-5,cost*1e-5))break;}
  else{lambda=std::min(1e8,lambda*10);if(lambda>=1e8)break;}
 }
 for(size_t p=0;p<points.size();p++)tracks[landmarks[p].track].point=points[p];
 report(38,"bundle adjustment cost "+std::to_string(initialCost)+" -> "+std::to_string(cost)+" focal "+std::to_string(focal));
}
