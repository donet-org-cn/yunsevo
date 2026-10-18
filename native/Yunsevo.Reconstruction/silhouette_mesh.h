// Explicit outer-silhouette approximation. Input masks are produced locally;
// no RGB pixel generation and no claim to recover unseen concavities.
static double medianValue(std::vector<double> values){std::sort(values.begin(),values.end());return values[values.size()/2];}
struct HullView{int camera;Mat sdf;cv::Rect bounds;cv::Point2d centroid;};
static double hullOrbitGap(const std::vector<Vec3d>&centers,Vec3d subject){
 Mat directions((int)centers.size(),3,CV_64F);for(int i=0;i<(int)centers.size();i++){Vec3d d=centers[i]-subject;d/=std::max(1e-12,cv::norm(d));for(int a=0;a<3;a++)directions.at<double>(i,a)=d[a];}
 cv::PCA pca(directions,Mat(),cv::PCA::DATA_AS_ROW);std::vector<double> angles;
 for(int i=0;i<directions.rows;i++){double x=0,y=0;for(int a=0;a<3;a++){x+=directions.at<double>(i,a)*pca.eigenvectors.at<double>(0,a);y+=directions.at<double>(i,a)*pca.eigenvectors.at<double>(1,a);}angles.push_back(std::atan2(y,x));}
 std::sort(angles.begin(),angles.end());double gap=angles.front()+2*CV_PI-angles.back();for(size_t i=1;i<angles.size();i++)gap=std::max(gap,angles[i]-angles[i-1]);return gap*180/CV_PI;
}
static float sampleDistance(const Mat&image,double x,double y){
 int ix=(int)std::floor(x),iy=(int)std::floor(y);if(ix<0||iy<0||ix>=image.cols-1||iy>=image.rows-1)return -1000;
 float dx=(float)(x-ix),dy=(float)(y-iy);return (image.at<float>(iy,ix)*(1-dx)+image.at<float>(iy,ix+1)*dx)*(1-dy)+(image.at<float>(iy+1,ix)*(1-dx)+image.at<float>(iy+1,ix+1)*dx)*dy;
}
static void hullNormals(std::vector<Vertex>&mesh,const std::vector<uint32_t>&faces){
 for(auto&v:mesh)v.nx=v.ny=v.nz=0;
 for(size_t i=0;i<faces.size();i+=3){auto&a=mesh[faces[i]];auto&b=mesh[faces[i+1]];auto&c=mesh[faces[i+2]];Vec3d n=Vec3d(b.x-a.x,b.y-a.y,b.z-a.z).cross(Vec3d(c.x-a.x,c.y-a.y,c.z-a.z));for(int k=0;k<3;k++){auto&v=mesh[faces[i+k]];v.nx+=(float)n[0];v.ny+=(float)n[1];v.nz+=(float)n[2];}}
 for(auto&v:mesh){double n=std::sqrt(v.nx*v.nx+v.ny*v.ny+v.nz*v.nz);if(n>1e-12){v.nx/=(float)n;v.ny/=(float)n;v.nz/=(float)n;}}
}
static void hullSurface(const std::vector<float>&field,int N,Vec3d lo,double step,std::vector<Vertex>&mesh,std::vector<uint32_t>&faces,Progress report){
 const int corners[8][3]={{0,0,0},{1,0,0},{1,1,0},{0,1,0},{0,0,1},{1,0,1},{1,1,1},{0,1,1}};
 const int tets[6][4]={{0,5,1,6},{0,1,2,6},{0,2,3,6},{0,3,7,6},{0,7,4,6},{0,4,5,6}};
 std::unordered_map<uint64_t,uint32_t> edges;
 auto position=[&](int id){return lo+Vec3d(id%N,id/N%N,id/(N*N))*step;};
 auto gradient=[&](int id){int x=id%N,y=id/N%N,z=id/(N*N);return Vec3d(x>0&&x<N-1?field[id+1]-field[id-1]:0,y>0&&y<N-1?field[id+N]-field[id-N]:0,z>0&&z<N-1?field[id+N*N]-field[id-N*N]:0);};
 auto vertex=[&](int a,int b){uint64_t key=((uint64_t)std::min(a,b)<<32)|(uint32_t)std::max(a,b);auto found=edges.find(key);if(found!=edges.end())return found->second;double t=std::clamp(field[a]/(double)(field[a]-field[b]),0.,1.);Vec3d p=position(a)*(1-t)+position(b)*t,n=-(gradient(a)*(1-t)+gradient(b)*t);n/=std::max(1e-15,cv::norm(n));auto id=(uint32_t)mesh.size();mesh.push_back({(float)p[0],(float)-p[1],(float)-p[2],(float)n[0],(float)-n[1],(float)-n[2],.3f,.3f,.3f});edges.emplace(key,id);return id;};
 auto tri=[&](uint32_t a,uint32_t b,uint32_t c){const auto&x=mesh[a];const auto&y=mesh[b];const auto&z=mesh[c];Vec3d n=Vec3d(y.x-x.x,y.y-x.y,y.z-x.z).cross(Vec3d(z.x-x.x,z.y-x.y,z.z-x.z));if(a==b||b==c||a==c)return;if(n.dot(Vec3d(x.nx+y.nx+z.nx,x.ny+y.ny+z.ny,x.nz+y.nz+z.nz))<0)std::swap(b,c);faces.insert(faces.end(),{a,b,c});};
 for(int z=0;z<N-1;z++){
  if(z%8==0)report(80+z*4/N,"extracting silhouette surface");
  for(int y=0;y<N-1;y++)for(int x=0;x<N-1;x++){
   int ids[8];for(int i=0;i<8;i++)ids[i]=((z+corners[i][2])*N+y+corners[i][1])*N+x+corners[i][0];
   for(const auto&tet:tets){int in[4],out[4],ni=0,no=0;for(int k:tet){if(field[ids[k]]>0)in[ni++]=ids[k];else out[no++]=ids[k];}if(ni==0||ni==4)continue;
    if(ni==1)tri(vertex(in[0],out[0]),vertex(in[0],out[1]),vertex(in[0],out[2]));else if(ni==3)tri(vertex(out[0],in[0]),vertex(out[0],in[1]),vertex(out[0],in[2]));else{auto a=vertex(in[0],out[0]),b=vertex(in[0],out[1]),c=vertex(in[1],out[0]),d=vertex(in[1],out[1]);tri(a,b,c);tri(b,d,c);}
   }
  }
 }
 if(faces.empty())throw std::runtime_error("Silhouettes do not form a consistent volume; check camera poses and foreground");
 // Largest connected foreground, not a mesh joining disconnected floaters.
 std::vector<uint32_t> parent(mesh.size());std::iota(parent.begin(),parent.end(),0);auto root=[&](uint32_t n){while(parent[n]!=n){parent[n]=parent[parent[n]];n=parent[n];}return n;};
 for(size_t i=0;i<faces.size();i+=3)for(int k=1;k<3;k++)parent[root(faces[i+k])]=root(faces[i]);
 std::vector<size_t> counts(mesh.size());for(auto id:faces)counts[root(id)]++;auto largest=(uint32_t)(std::max_element(counts.begin(),counts.end())-counts.begin());
 std::vector<uint32_t> kept;for(size_t i=0;i<faces.size();i+=3)if(root(faces[i])==largest)kept.insert(kept.end(),{faces[i],faces[i+1],faces[i+2]});faces.swap(kept);
 std::vector<Vertex> compact;std::vector<uint32_t> remap(mesh.size(),UINT32_MAX);for(auto&id:faces){if(remap[id]==UINT32_MAX){remap[id]=(uint32_t)compact.size();compact.push_back(mesh[id]);}id=remap[id];}mesh.swap(compact);
 if(mesh.size()<300||faces.size()>3000000)throw std::runtime_error("Invalid silhouette surface size");
}
static bool hullSupport(std::vector<Vertex>&mesh,const std::vector<uint32_t>&faces,const std::vector<Camera>&cameras,const std::vector<Track>&tracks){
 Vec3d midpoint(0,0,0),up(0,0,0);for(auto&v:mesh)midpoint+=Vec3d(v.x,-v.y,-v.z);midpoint/=(double)mesh.size();for(auto&c:cameras)if(c.registered)up+=c.R.t()*Vec3d(0,-1,0);up/=std::max(1e-12,cv::norm(up));
 double low=1e30,high=-1e30;std::vector<double> radii;for(auto&v:mesh){Vec3d d=Vec3d(v.x,-v.y,-v.z)-midpoint;double h=d.dot(up);low=std::min(low,h);high=std::max(high,h);radii.push_back(cv::norm(d-up*h));}std::sort(radii.begin(),radii.end());double height=high-low,radius=radii[radii.size()*95/100];if(height<=0)return false;
 std::vector<Vec3d> points;for(auto&tr:tracks)if(tr.good){Vec3d p(tr.point.x,tr.point.y,tr.point.z),delta=p-midpoint;double h=delta.dot(up);if(h<low-.05*height||h>low+.3*height||cv::norm(delta-up*h)>radius*3)continue;int observed=0,outside=0;for(auto[i,k]:tr.observations)if(cameras[i].registered&&!cameras[i].foreground.empty()){auto pixel=cameras[i].features[k].pt;int x=(int)pixel.x,y=(int)pixel.y;if(x>=0&&y>=0&&x<1024&&y<1024){observed++;outside+=cameras[i].foreground.at<unsigned char>(y,x)==0;}}if(observed>=3&&outside>observed*.7)points.push_back(p);if(points.size()==30000)break;}
 if(points.size()<40)return false;cv::RNG rng(0);std::vector<int> best;
 for(int pass=0;pass<1000;pass++){auto a=points[rng.uniform(0,(int)points.size())],b=points[rng.uniform(0,(int)points.size())],c=points[rng.uniform(0,(int)points.size())];Vec3d n=(b-a).cross(c-a);double len=cv::norm(n);if(len<1e-12)continue;n/=len;if(std::abs(n.dot(up))<.95)continue;std::vector<int> inliers;for(int i=0;i<(int)points.size();i++)if(std::abs((points[i]-a).dot(n))<height*.005)inliers.push_back(i);if(inliers.size()>best.size())best=std::move(inliers);}
 if(best.size()<40||best.size()<points.size()*.5)return false;Vec3d origin(0,0,0);for(int i:best)origin+=points[i];origin/=(double)best.size();Mat matrix((int)best.size(),3,CV_64F);for(int i=0;i<(int)best.size();i++)for(int a=0;a<3;a++)matrix.at<double>(i,a)=points[best[i]][a]-origin[a];Mat w,u,vt;cv::SVD::compute(matrix,w,u,vt);Vec3d normal(vt.at<double>(2,0),vt.at<double>(2,1),vt.at<double>(2,2));if(normal.dot(up)<0)normal=-normal;
 double error=0;for(int i:best){double d=(points[i]-origin).dot(normal);error+=d*d;}error=std::sqrt(error/best.size())/height;double clipped=((origin-midpoint).dot(normal)/up.dot(normal)-low)/height;
 if(error>.004||clipped<.005||clipped>.15||w.at<double>(1)/std::sqrt((double)best.size())<radius*.5)return false;
 size_t below=0;for(auto&v:mesh)below+=(Vec3d(v.x,-v.y,-v.z)-origin).dot(normal)<0;if(below>mesh.size()*.25)return false;
 for(auto&v:mesh){Vec3d p(v.x,-v.y,-v.z);double d=(p-origin).dot(normal);if(d<0){p-=normal*d;v.x=(float)p[0];v.y=(float)-p[1];v.z=(float)-p[2];}}hullNormals(mesh,faces);return true;
}
static std::vector<std::vector<float>> hullDepths(const std::vector<Camera>&cameras,const std::vector<Vertex>&mesh,const std::vector<uint32_t>&faces,double focal,int side,Progress report){
 std::vector<std::vector<float>> maps(cameras.size());double f=focal*side/1024.;
 for(size_t i=0;i<cameras.size();i++)if(cameras[i].registered){
  report(85+(int)i*8/(int)cameras.size(),"checking silhouette projections");auto&depth=maps[i];depth.resize(side*side);
  for(size_t j=0;j<faces.size();j+=3){Vec3d q[3];cv::Point2d p[3];bool valid=true;for(int k=0;k<3;k++){auto&v=mesh[faces[j+k]];q[k]=cameras[i].R*Vec3d(v.x,-v.y,-v.z)+cameras[i].t;if(q[k][2]<=0){valid=false;break;}p[k]={f*q[k][0]/q[k][2]+side*.5,f*q[k][1]/q[k][2]+side*.5};}if(!valid)continue;
   double area=(p[1].x-p[0].x)*(p[2].y-p[0].y)-(p[1].y-p[0].y)*(p[2].x-p[0].x);if(std::abs(area)<1e-10)continue;
   int x0=std::max(0,(int)std::floor(std::min({p[0].x,p[1].x,p[2].x}))),x1=std::min(side-1,(int)std::ceil(std::max({p[0].x,p[1].x,p[2].x}))),y0=std::max(0,(int)std::floor(std::min({p[0].y,p[1].y,p[2].y}))),y1=std::min(side-1,(int)std::ceil(std::max({p[0].y,p[1].y,p[2].y})));
   for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++){double a=((p[1].x-x)*(p[2].y-y)-(p[1].y-y)*(p[2].x-x))/area,b=((p[2].x-x)*(p[0].y-y)-(p[2].y-y)*(p[0].x-x))/area,c=1-a-b;if(std::min({a,b,c})<-.0001)continue;float z=(float)(1/(a/q[0][2]+b/q[1][2]+c/q[2][2]));auto&d=depth[y*side+x];if(d==0||z<d)d=z;}
  }
 }return maps;
}
struct HullResult{double medianIoU,p10IoU;int views;bool supportClipped;};
static HullResult silhouetteMesh(std::vector<Camera>&cameras,const std::vector<Track>&tracks,double focal,std::vector<Vertex>&mesh,std::vector<uint32_t>&faces,std::vector<std::vector<float>>&depths,int dense,Progress report,int N=192){
 std::vector<HullView> views;
 for(int i=0;i<(int)cameras.size();i++)if(cameras[i].registered){auto&c=cameras[i];if(c.foreground.empty())throw std::runtime_error("Missing local foreground mask");Mat small,inside,outside;cv::resize(c.foreground,small,{800,800},0,0,cv::INTER_NEAREST);cv::distanceTransform(small,inside,cv::DIST_L2,5);cv::distanceTransform(255-small,outside,cv::DIST_L2,5);auto moment=cv::moments(small,true);if(moment.m00<100)throw std::runtime_error("Empty foreground contour");views.push_back({i,inside-outside,cv::boundingRect(small),{moment.m10/moment.m00,moment.m01/moment.m00}});}
 auto locate=[&](){Matx33d A=Matx33d::zeros();Vec3d b(0,0,0);for(auto&v:views){auto&c=cameras[v.camera];Vec3d ray=c.R.t()*Vec3d((v.centroid.x*1024/800.-512)/focal,(v.centroid.y*1024/800.-512)/focal,1);ray/=cv::norm(ray);auto P=Matx33d::eye()-ray*ray.t();A+=P;b+=P*center(c);}return A.inv(cv::DECOMP_SVD)*b;};
 Vec3d origin=locate();size_t original=views.size();
 for(int pass=0;pass<2;pass++){std::vector<HullView> kept;for(auto&v:views){auto&c=cameras[v.camera];Vec3d q=c.R*origin+c.t;double x=(focal*q[0]/q[2]+512)*800/1024.,y=(focal*q[1]/q[2]+512)*800/1024.,margin=std::max(v.bounds.width,v.bounds.height)*.15;if(q[2]>0&&x>=v.bounds.x-margin&&x<=v.bounds.br().x+margin&&y>=v.bounds.y-margin&&y<=v.bounds.br().y+margin)kept.push_back(v);else c.registered=false;}if(kept.size()<6||kept.size()<original*.85)throw std::runtime_error("Camera poses disagree with foreground contours; add overlapping intermediate views");views=std::move(kept);origin=locate();}
 std::vector<Vec3d> centers;for(auto&v:views)centers.push_back(center(cameras[v.camera]));if(hullOrbitGap(centers,origin)>120)throw std::runtime_error("Silhouette volume is unbounded; more than 120 degrees of orbit were not captured");
 std::vector<double> radii;for(auto&v:views){auto&c=cameras[v.camera];radii.push_back(std::max(v.bounds.width,v.bounds.height)*.5*(c.R*origin+c.t)[2]/(focal*800/1024.));}double radius=medianValue(radii)*1.3,step=2*radius/(N-1);if(!std::isfinite(radius)||radius<=0)throw std::runtime_error("Invalid silhouette bounds");Vec3d lo=origin-Vec3d(radius,radius,radius);
 std::vector<float> field((size_t)N*N*N);
 for(int z=0;z<N;z++){
  if(z%2==0)report(44+z*35/N,"intersecting local foreground contours "+std::to_string(z)+"/"+std::to_string(N));
  for(int y=0;y<N;y++)for(int x=0;x<N;x++){Vec3d point=lo+Vec3d(x,y,z)*step;float first=1e20f,second=1e20f;for(auto&v:views){auto&c=cameras[v.camera];Vec3d q=c.R*point+c.t;float distance=q[2]>0?sampleDistance(v.sdf,(focal*q[0]/q[2]+512)*800/1024.,(focal*q[1]/q[2]+512)*800/1024.)*(float)(q[2]/(focal*800/1024.)):(float)-radius;second=std::min(second,std::max(first,distance));first=std::min(first,distance);}field[((size_t)z*N+y)*N+x]=second;}
 }
 // Separable Gaussian sigma .65, matching server contour-volume smoothing.
 std::vector<float> temp(field.size());double weights[5],sum=0;for(int k=-2;k<=2;k++){weights[k+2]=std::exp(-k*k/(2*.65*.65));sum+=weights[k+2];}for(auto&w:weights)w/=sum;
 for(int axis=0;axis<3;axis++){report(79,"smoothing silhouette volume");for(int z=0;z<N;z++)for(int y=0;y<N;y++)for(int x=0;x<N;x++){double value=0;for(int k=-2;k<=2;k++){int xx=x,yy=y,zz=z;if(axis==0)xx=std::clamp(x+k,0,N-1);if(axis==1)yy=std::clamp(y+k,0,N-1);if(axis==2)zz=std::clamp(z+k,0,N-1);value+=weights[k+2]*field[((size_t)zz*N+yy)*N+xx];}temp[((size_t)z*N+y)*N+x]=(float)value;}field.swap(temp);}
 // An occupied grid boundary means the capture does not constrain the exterior.
 for(int z=0;z<N;z++)for(int y=0;y<N;y++)for(int x=0;x<N;x++)if((x==0||y==0||z==0||x==N-1||y==N-1||z==N-1)&&field[((size_t)z*N+y)*N+x]>0)throw std::runtime_error("Silhouette volume is unbounded; capture a complete orbit");
 hullSurface(field,N,lo,step,mesh,faces,report);bool support=hullSupport(mesh,faces,cameras,tracks);depths=hullDepths(cameras,mesh,faces,focal,dense,report);
 std::vector<double> overlaps;for(auto&v:views){Mat mask;cv::resize(cameras[v.camera].foreground,mask,{dense,dense},0,0,cv::INTER_NEAREST);size_t intersection=0,total=0;for(int i=0;i<dense*dense;i++){bool a=mask.data[i]>0,b=depths[v.camera][i]>0;intersection+=a&&b;total+=a||b;}overlaps.push_back(intersection/(double)std::max<size_t>(1,total));}std::sort(overlaps.begin(),overlaps.end());double median=overlaps[overlaps.size()/2],p10=overlaps[overlaps.size()/10];
 report(94,"silhouette agreement median "+std::to_string(median)+" p10 "+std::to_string(p10));if(median<.90||p10<.85)throw std::runtime_error("Silhouette reprojection disagreement; model was not published. Check foreground masks and camera overlap");
 return {median,p10,(int)views.size(),support};
}
