// Shared Android / desktop preprocessing. EXIF is applied exactly once; masks
// use the same upright, centred 1024px coordinates as native camera calibration.
YV_EXPORT int yv_mask_input(const char* path,float* tensor,int length,int* size){
 errorText.clear();try{
  if(!tensor||!size||length!=3*1024*1024)throw std::runtime_error("Invalid mask input buffer");
  auto source=fs::u8path(path);if(fs::file_size(source)>40*1024*1024)throw std::runtime_error("Photo exceeds 40 MB");
  std::ifstream file(source,std::ios::binary);std::vector<unsigned char> bytes((std::istreambuf_iterator<char>(file)),{});
  if(!boundedImage(bytes))throw std::runtime_error("Invalid segmentation image");
  Mat image=cv::imdecode(bytes,cv::IMREAD_COLOR);if(image.empty())throw std::runtime_error("Cannot decode segmentation image");
  double scale=1024./std::max(image.cols,image.rows);size[0]=(int)(image.cols*scale+.5);size[1]=(int)(image.rows*scale+.5);
  Mat resized;cv::resize(image,resized,{size[0],size[1]},0,0,cv::INTER_LINEAR_EXACT);
  std::fill(tensor,tensor+length,0.f);const float mean[]={123.675f,116.28f,103.53f},sd[]={58.395f,57.12f,57.375f};
  for(int y=0;y<resized.rows;y++)for(int x=0;x<resized.cols;x++){auto pixel=resized.at<cv::Vec3b>(y,x);for(int c=0;c<3;c++)tensor[c*1024*1024+y*1024+x]=(pixel[2-c]-mean[c])/sd[c];}
  return 0;
 }catch(const std::exception&e){errorText=e.what();return 1;}
}
YV_EXPORT int yv_mask_output(const char* path,const float* logits,int length,int width,int height){
 errorText.clear();try{
  if(!logits||length!=256*256||width<1||height<1||width>1024||height>1024)throw std::runtime_error("Invalid mask output buffer");
  Mat low(256,256,CV_32F,const_cast<float*>(logits)),large;cv::resize(low,large,{1024,1024},0,0,cv::INTER_LINEAR);
  Mat foreground=large(cv::Rect(0,0,width,height))>0,filled=Mat::zeros(foreground.size(),CV_8U);
  std::vector<std::vector<cv::Point>> contours;cv::findContours(foreground,contours,cv::RETR_EXTERNAL,cv::CHAIN_APPROX_SIMPLE);
  for(size_t i=0;i<contours.size();i++)if(cv::pointPolygonTest(contours[i],cv::Point2f(width*.5f,height*.5f),false)>=0)cv::drawContours(filled,contours,(int)i,cv::Scalar(255),cv::FILLED);
  if(cv::countNonZero(filled))foreground=filled;
  double fraction=cv::countNonZero(foreground)/(double)(width*height);if(fraction<.015||fraction>.65)throw std::runtime_error("Invalid foreground mask coverage");
  Mat canvas=Mat::zeros(1024,1024,CV_8U);foreground.copyTo(canvas(cv::Rect((1024-width)/2,(1024-height)/2,width,height)));
  std::vector<unsigned char> bytes;cv::imencode(".png",canvas,bytes);std::ofstream out(fs::u8path(path),std::ios::binary);out.write((const char*)bytes.data(),bytes.size());if(!out)throw std::runtime_error("Cannot save foreground mask");return 0;
 }catch(const std::exception&e){errorText=e.what();return 1;}
}
