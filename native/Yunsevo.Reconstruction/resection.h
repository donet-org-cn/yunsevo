// Recover weakly connected views directly against measured 3D landmark descriptors.
// Keep multiple appearances of a landmark, but use a different landmark for the ratio test.
static void recoverViews(std::vector<Camera>&cs,std::vector<Track>&tracks,double focal,Progress report){
 const double mid=cs[0].gray.cols*.5;Mat K=(cv::Mat_<double>(3,3)<<focal,0,mid,0,focal,mid,0,0,1);
 for(int pass=0;pass<2;pass++){
  Mat descriptors;std::vector<size_t> landmark;
  for(size_t p=0;p<tracks.size();p++)if(tracks[p].good){
   std::vector<std::pair<int,int>> seen;for(auto[i,k]:tracks[p].observations)if(cs[i].registered&&reprojection(cs[i],tracks[p].point,cs[i].features[k].pt,focal,mid)<3)seen.push_back({i,k});
   if(seen.size()<2)continue;
   for(size_t j=0;j<std::min<size_t>(3,seen.size());j++){auto[i,k]=seen[j*(seen.size()-1)/std::max<size_t>(1,std::min<size_t>(3,seen.size())-1)];descriptors.push_back(cs[i].desc.row(k));landmark.push_back(p);}
  }
  if(descriptors.rows<100)return;int added=0;
  auto matcher=cv::FlannBasedMatcher(cv::makePtr<cv::flann::KDTreeIndexParams>(4),cv::makePtr<cv::flann::SearchParams>(128));matcher.add(std::vector<Mat>{descriptors});matcher.train();
  for(size_t i=0;i<cs.size();i++)if(!cs[i].registered&&!cs[i].desc.empty()){
   report(38,"recovering view "+std::to_string(i+1)+" from global landmarks");
   std::vector<std::vector<cv::DMatch>> nearest;matcher.knnMatch(cs[i].desc,nearest,5);
   std::vector<cv::DMatch> candidates;
   for(auto&list:nearest)if(list.size()>=2){auto a=list.front();for(size_t k=1;k<list.size();k++)if(landmark[list[k].trainIdx]!=landmark[a.trainIdx]){if(a.distance<.8*list[k].distance)candidates.push_back(a);break;}}
   std::sort(candidates.begin(),candidates.end(),[](const auto&a,const auto&b){return a.distance<b.distance;});
   std::set<size_t> unique;std::vector<Point2f> pixels;std::vector<cv::Point3f> points;std::vector<cv::DMatch> matches;
   for(auto&m:candidates)if(unique.insert(landmark[m.trainIdx]).second){points.emplace_back(tracks[landmark[m.trainIdx]].point);pixels.push_back(cs[i].features[m.queryIdx].pt);matches.push_back(m);}
   if(points.size()<24)continue;Mat r,t,inliers;
   if(!cv::solvePnPRansac(points,pixels,K,cv::noArray(),r,t,false,1500,3,.999,inliers,cv::SOLVEPNP_EPNP)||inliers.rows<std::max(24,(int)(points.size()*.2)))continue;
   std::vector<cv::Point3f> op;std::vector<Point2f> ip;for(int k=0;k<inliers.rows;k++){int at=inliers.at<int>(k);op.push_back(points[at]);ip.push_back(pixels[at]);}
   auto box=cv::boundingRect(ip);if(box.width<cs[i].photoRect.width*.15||box.height<cs[i].photoRect.height*.15)continue;
   cv::solvePnPRefineLM(op,ip,K,cv::noArray(),r,t);Mat R;cv::Rodrigues(r,R);Camera candidate=cs[i];for(int k=0;k<3;k++){candidate.t[k]=t.at<double>(k);for(int j=0;j<3;j++)candidate.R(k,j)=R.at<double>(k,j);}
   double error=0;for(size_t k=0;k<op.size();k++)error+=reprojection(candidate,Point3d(op[k]),ip[k],focal,mid);
   if(error/op.size()>2)continue;
   cs[i].R=candidate.R;cs[i].t=candidate.t;cs[i].registered=true;added++;
   for(int k=0;k<inliers.rows;k++){auto&m=matches[inliers.at<int>(k)];tracks[landmark[m.trainIdx]].observations[(int)i]=m.queryIdx;}
   report(38,"recovered view "+std::to_string(i+1)+" inliers "+std::to_string(inliers.rows));
  }
  if(!added)return;
  for(auto&tr:tracks)if(!tr.good)triangulate(tr,cs,focal,mid);
 }
}
