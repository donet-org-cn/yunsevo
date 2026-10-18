// Project original photographs onto visible reconstructed triangles. Geometry resolution is
// independent of texture resolution; never average dense-map colors into photograph detail.
struct TextureGroup { int camera; std::vector<uint32_t> indices; size_t offset=0; };
static void glb(const fs::path&path,const std::vector<Vertex>&mesh,const std::vector<uint32_t>&faces,
 const std::vector<Camera>&cameras,const std::vector<std::vector<float>>&depths,double focal,int dense,int textureSide,
 const std::string&backend,int registered,Progress report){
 if(mesh.empty()||faces.size()<300)throw std::runtime_error("Insufficient consistent surface geometry; model was not published.");
 std::map<int,std::vector<uint32_t>> assigned;size_t textured=0;
 const double mid=cameras[0].color.cols*.5,df=focal*dense/cameras[0].color.cols;
 std::vector<Vec3d> smooth(mesh.size());for(size_t i=0;i<mesh.size();i++)smooth[i]=Vec3d(mesh[i].nx,-mesh[i].ny,-mesh[i].nz);
 // Stabilize view selection across voxel-sized surface noise without smoothing image pixels.
 for(int pass=0;pass<5;pass++){
  auto sum=smooth;std::vector<int> counts(mesh.size(),1);
  for(size_t j=0;j<faces.size();j+=3)for(int k=0;k<3;k++){auto a=faces[j+k],b=faces[j+(k+1)%3];if(smooth[a].dot(smooth[b])>.75){sum[a]+=smooth[b];sum[b]+=smooth[a];counts[a]++;counts[b]++;}}
  for(size_t i=0;i<smooth.size();i++){double length=cv::norm(sum[i]);if(length>1e-10)smooth[i]=sum[i]/length;}
 }
 const bool fine=dense>256;
 const size_t total=faces.size()/3;std::vector<Vec3d> points(total),normals(total),geometric(total);std::vector<int> labels(total,-1);std::vector<double> scores(total),areas(total);
 Vec3d boundsLo(1e30,1e30,1e30),boundsHi(-1e30,-1e30,-1e30);
 for(size_t t=0;t<total;t++){
  Vec3d n(0,0,0),p(0,0,0);for(int k=0;k<3;k++){auto id=faces[t*3+k];auto&v=mesh[id];p+=Vec3d(v.x,-v.y,-v.z);n+=smooth[id];}
  points[t]=p/3.;double length=cv::norm(n);normals[t]=length>1e-10?n/length:Vec3d(0,1,0);
  auto position=[&](int k){const auto&v=mesh[faces[t*3+k]];return Vec3d(v.x,-v.y,-v.z);};
  Vec3d face=(position(1)-position(0)).cross(position(2)-position(0));areas[t]=cv::norm(face)*.5;geometric[t]=areas[t]>1e-15?face/(areas[t]*2):normals[t];
  for(int k=0;k<3;k++){boundsLo[k]=std::min(boundsLo[k],points[t][k]);boundsHi[k]=std::max(boundsHi[k],points[t][k]);}
 }
 auto viewScore=[&](size_t tri,int i){
  const auto&c=cameras[i];if(!c.registered||depths[i].empty())return 0.;
  auto p=points[tri];Vec3d q=c.R*p+c.t;if(q[2]<=0)return 0.;
  int u=(int)std::round(df*q[0]/q[2]+dense*.5),v=(int)std::round(df*q[1]/q[2]+dense*.5);if(u<1||v<1||u>=dense-1||v>=dense-1)return 0.;
  float d=depths[i][v*dense+u];if(d<=0||std::abs(d-q[2])>q[2]*.055)return 0.;
  for(int k=0;k<3;k++){auto&v=mesh[faces[tri*3+k]];Vec3d a=c.R*Vec3d(v.x,-v.y,-v.z)+c.t;if(a[2]<=0)return 0.;double x=focal*a[0]/a[2]+mid,y=focal*a[1]/a[2]+mid;
   if(x<c.photoRect.x+1||y<c.photoRect.y+1||x>=c.photoRect.x+c.photoRect.width-1||y>=c.photoRect.y+c.photoRect.height-1)return 0.;
   if(fine){int dx=(int)std::round(df*a[0]/a[2]+dense*.5),dy=(int)std::round(df*a[1]/a[2]+dense*.5);if(dx<0||dy<0||dx>=dense||dy>=dense)return 0.;float measured=depths[i][dy*dense+dx];if(measured>0&&std::abs(measured-a[2])>a[2]*.04)return 0.;}}
  Vec3d view=center(c)-p;view/=std::max(1e-10,cv::norm(view));
  double cosine=normals[tri].dot(view);if(fine){if(geometric[tri].dot(view)<.02)return 0.;}else cosine=std::abs(cosine);if(cosine<.12)return 0.;
  double resolution=std::min(textureSide,c.sourceSide)/(double)std::max(c.photoRect.width,c.photoRect.height);
  return cosine*cosine*resolution*resolution/(q[2]*q[2]);
 };
 for(size_t tri=0;tri<total;tri++){
  if(tri%10000==0)report(97,"texturing visible surfaces "+std::to_string(tri)+"/"+std::to_string(total));
  for(int i=0;i<(int)cameras.size();i++){double value=viewScore(tri,i);if(value>scores[tri]){scores[tri]=value;labels[tri]=i;}}
 }
 // Connected, approximately planar patches prefer one source view. Recheck visibility on
 // every reassigned triangle; do not stretch a photograph over hidden or unrelated surfaces.
 std::vector<std::array<int,3>> adjacent(total,std::array<int,3>{-1,-1,-1});std::unordered_map<uint64_t,std::pair<int,int>> edges;
 for(size_t t=0;t<total;t++)for(int k=0;k<3;k++){uint32_t a=faces[t*3+k],b=faces[t*3+(k+1)%3];uint64_t key=((uint64_t)std::min(a,b)<<32)|std::max(a,b);auto [at,inserted]=edges.emplace(key,std::pair<int,int>((int)t,k));if(!inserted){adjacent[t][k]=at->second.first;adjacent[at->second.first][at->second.second]=(int)t;}}
 edges.clear();std::vector<unsigned char> seen(total);double planeTolerance=cv::norm(boundsHi-boundsLo)*.018;
 for(size_t seed=0;seed<total;seed++)if(!seen[seed]){
  seen[seed]=1;std::vector<size_t> patch{seed};
  for(size_t head=0;head<patch.size();head++)for(int other:adjacent[patch[head]])if(other>=0&&!seen[other]&&normals[seed].dot(normals[other])>.93&&std::abs(normals[seed].dot(points[other]-points[seed]))<planeTolerance){seen[other]=1;patch.push_back((size_t)other);}
  if(patch.size()<24)continue;std::vector<double> votes(cameras.size());for(auto t:patch)if(labels[t]>=0)votes[labels[t]]+=areas[t];
  int camera=(int)(std::max_element(votes.begin(),votes.end())-votes.begin());if(!votes[camera])continue;
  for(auto t:patch)if(viewScore(t,camera)>=scores[t]*.35&&viewScore(t,camera)>0)labels[t]=camera;
 }
 for(size_t t=0;t<total;t++){auto&group=assigned[labels[t]];group.insert(group.end(),{faces[t*3],faces[t*3+1],faces[t*3+2]});if(labels[t]>=0)textured++;}
 if(!textured)throw std::runtime_error("No visible photograph projections. Improve overlapping views and camera calibration.");
 std::vector<Vertex> vertices;std::vector<Point2f> uv;std::vector<TextureGroup> groups;
 for(auto&[camera,ids]:assigned){
  TextureGroup group{camera};std::unordered_map<uint32_t,uint32_t> remap;
  for(auto id:ids){auto [at,inserted]=remap.emplace(id,(uint32_t)vertices.size());if(inserted){auto v=mesh[id];vertices.push_back(v);Point2f t(0,0);
    if(camera>=0){const auto&c=cameras[camera];Vec3d q=c.R*Vec3d(v.x,-v.y,-v.z)+c.t;
     t={(float)((focal*q[0]/q[2]+mid-c.photoRect.x)/c.photoRect.width),(float)((focal*q[1]/q[2]+mid-c.photoRect.y)/c.photoRect.height)};}uv.push_back(t);}
   group.indices.push_back(at->second);}
  groups.push_back(std::move(group));
 }
 std::vector<unsigned char> bin;const size_t limit=240ull*1024*1024;
 auto append=[&](const void*data,size_t length){while(bin.size()%4)bin.push_back(0);size_t offset=bin.size();if(length>limit||offset>limit-length)throw std::runtime_error("Textured model exceeds 240 MB. Reduce photos or use preview quality.");auto*p=(const unsigned char*)data;bin.insert(bin.end(),p,p+length);return offset;};
 size_t vb=vertices.size()*sizeof(Vertex),uvb=uv.size()*sizeof(Point2f);append(vertices.data(),vb);size_t uvo=append(uv.data(),uvb);
 std::vector<uint32_t> indices;for(auto&g:groups){g.offset=indices.size()*4;indices.insert(indices.end(),g.indices.begin(),g.indices.end());std::vector<uint32_t>().swap(g.indices);}size_t ib=indices.size()*4,io=append(indices.data(),ib);
 const size_t imagePixelBudget=(textureSide>2048?128ull:64ull)*1024*1024;
 const size_t imageCount=std::count_if(groups.begin(),groups.end(),[](const auto&g){return g.camera>=0;});
 struct Image {int camera;size_t offset,length;};std::vector<Image> images;
 for(auto&g:groups)if(g.camera>=0){
  report(97,"texturing original photograph "+std::to_string(images.size()+1)+"/"+std::to_string(groups.size()));
  const auto&c=cameras[g.camera];std::ifstream stream(c.source,std::ios::binary);std::vector<unsigned char> bytes((std::istreambuf_iterator<char>(stream)),{});
  if(bytes.size()>40*1024*1024||!boundedImage(bytes))throw std::runtime_error("Texture photograph changed or is invalid");
  Mat image=cv::imdecode(bytes,cv::IMREAD_COLOR);if(image.empty())throw std::runtime_error("Cannot decode texture photograph");
  double scale=std::min({1.,textureSide/(double)std::max(image.cols,image.rows),std::sqrt((imagePixelBudget/imageCount)/(double)image.total())});if(scale<1){Mat resized;cv::resize(image,resized,{std::max(1,(int)(image.cols*scale)),std::max(1,(int)(image.rows*scale))},0,0,cv::INTER_AREA);image=std::move(resized);}
  bytes.clear();cv::imencode(".jpg",image,bytes,{cv::IMWRITE_JPEG_QUALITY,95});size_t offset=append(bytes.data(),bytes.size());images.push_back({g.camera,offset,bytes.size()});
 }
 while(bin.size()%4)bin.push_back(0);
 auto textureIndex=[&](int camera){for(size_t i=0;i<images.size();i++)if(images[i].camera==camera)return (int)i;return -1;};
 float lo[3]={1e30f,1e30f,1e30f},hi[3]={-1e30f,-1e30f,-1e30f};for(auto&v:vertices){float a[]={v.x,v.y,v.z};for(int i=0;i<3;i++){lo[i]=std::min(lo[i],a[i]);hi[i]=std::max(hi[i],a[i]);}}
 std::ostringstream js;js.imbue(std::locale::classic());
 js<<"{\"asset\":{\"version\":\"2.0\",\"generator\":\"Yunsevo reconstruction experimental 0.3\"},\"extensionsUsed\":[\"KHR_materials_unlit\"],\"extras\":{\"backend\":\""<<safe(backend)<<"\",\"photos\":"<<cameras.size()<<",\"registeredCameras\":"<<registered<<",\"appearance\":\"original-photo projection\",\"textureMaxSide\":"<<textureSide<<",\"texturedTriangles\":"<<textured<<",\"quality\":\""<<(fine?"fine":"quick")<<"\",\"depthSide\":"<<dense<<",\"depthValidation\":\""<<(fine?"round-trip and independently supported planar gaps":"pairwise")<<"\",\"scale\":\"relative; not a survey measurement\"},\"scene\":0,\"scenes\":[{\"nodes\":[0]}],\"nodes\":[{\"mesh\":0}],\"meshes\":[{\"primitives\":[";
 for(size_t i=0;i<groups.size();i++){if(i)js<<",";js<<"{\"attributes\":{\"POSITION\":0,\"NORMAL\":1,"<<(groups[i].camera<0?"\"COLOR_0\":2":"\"TEXCOORD_0\":3")<<"},\"indices\":"<<4+i<<",\"material\":"<<i<<"}";}js<<"]}],\"materials\":[";
 for(size_t i=0;i<groups.size();i++){if(i)js<<",";js<<"{\"doubleSided\":true,\"extensions\":{\"KHR_materials_unlit\":{}},\"pbrMetallicRoughness\":{\"metallicFactor\":0,\"roughnessFactor\":1";int t=textureIndex(groups[i].camera);if(t>=0)js<<",\"baseColorTexture\":{\"index\":"<<t<<"}";js<<"}}";}
 js<<"],\"samplers\":[{\"magFilter\":9729,\"minFilter\":9987,\"wrapS\":33071,\"wrapT\":33071}],\"textures\":[";
 for(size_t i=0;i<images.size();i++){if(i)js<<",";js<<"{\"sampler\":0,\"source\":"<<i<<"}";}js<<"],\"images\":[";
 for(size_t i=0;i<images.size();i++){if(i)js<<",";js<<"{\"bufferView\":"<<3+i<<",\"mimeType\":\"image/jpeg\"}";}js<<"],\"buffers\":[{\"byteLength\":"<<bin.size()<<"}],\"bufferViews\":[{\"buffer\":0,\"byteOffset\":0,\"byteLength\":"<<vb<<",\"byteStride\":36,\"target\":34962},{\"buffer\":0,\"byteOffset\":"<<uvo<<",\"byteLength\":"<<uvb<<",\"byteStride\":8,\"target\":34962},{\"buffer\":0,\"byteOffset\":"<<io<<",\"byteLength\":"<<ib<<",\"target\":34963}";
 for(auto&image:images)js<<",{\"buffer\":0,\"byteOffset\":"<<image.offset<<",\"byteLength\":"<<image.length<<"}";js<<"],\"accessors\":[{\"bufferView\":0,\"byteOffset\":0,\"componentType\":5126,\"count\":"<<vertices.size()<<",\"type\":\"VEC3\",\"min\":["<<lo[0]<<","<<lo[1]<<","<<lo[2]<<"],\"max\":["<<hi[0]<<","<<hi[1]<<","<<hi[2]<<"]},{\"bufferView\":0,\"byteOffset\":12,\"componentType\":5126,\"count\":"<<vertices.size()<<",\"type\":\"VEC3\"},{\"bufferView\":0,\"byteOffset\":24,\"componentType\":5126,\"count\":"<<vertices.size()<<",\"type\":\"VEC3\"},{\"bufferView\":1,\"componentType\":5126,\"count\":"<<uv.size()<<",\"type\":\"VEC2\"}";
 for(size_t i=0;i<groups.size();i++){size_t size=i+1<groups.size()?groups[i+1].offset-groups[i].offset:ib-groups[i].offset;js<<",{\"bufferView\":2,\"byteOffset\":"<<groups[i].offset<<",\"componentType\":5125,\"count\":"<<size/4<<",\"type\":\"SCALAR\"}";}js<<"]}";
 std::string json=js.str();while(json.size()%4)json+=' ';uint32_t header[]={0x46546c67,2,(uint32_t)(12+8+json.size()+8+bin.size()),(uint32_t)json.size(),0x4e4f534a};
 auto temp=path;temp+=".partial";std::ofstream out(temp,std::ios::binary);if(!out)throw std::runtime_error("Cannot create output");out.write((char*)header,sizeof(header));out.write(json.data(),json.size());uint32_t chunk[]={(uint32_t)bin.size(),0x004e4942};out.write((char*)chunk,8);out.write((char*)bin.data(),bin.size());out.close();if(!out)throw std::runtime_error("Output write failed");fs::rename(temp,path);
}
