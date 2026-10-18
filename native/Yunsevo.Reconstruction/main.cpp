#include "engine.h"
#include <cstdio>
#include <cstring>
#include <cstdlib>
static int progress(void*,int n,const char* text){ std::printf("%d\t%s\n",n,text);std::fflush(stdout);return 0; }
static int run(int argc,char**argv){
 if(argc==2&&std::strcmp(argv[1],"--capabilities")==0){std::puts(yv_capabilities());return 0;}
 if(argc<4){std::fputs("Usage: yunsevo-reconstruct INPUT_DIRECTORY OUTPUT.glb auto|cpu|vulkan [quality:0|1]\n",stderr);return 2;}
 int rc=yv_reconstruct_v2(argv[1],argv[2],argv[3],argc>4?std::atoi(argv[4]):0,argc>5?argv[5]:"object",progress,nullptr);
 if(rc)std::fprintf(stderr,"%s\n",yv_last_error());return rc;
}

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <vector>
#include <string>
int wmain(int argc,wchar_t**args){
 std::vector<std::string> storage;std::vector<char*> argv;
 for(int i=0;i<argc;i++){int n=WideCharToMultiByte(CP_UTF8,0,args[i],-1,nullptr,0,nullptr,nullptr);std::string value(n,'\0');WideCharToMultiByte(CP_UTF8,0,args[i],-1,value.data(),n,nullptr,nullptr);storage.push_back(std::move(value));}
 for(auto&s:storage)argv.push_back(s.data());return run(argc,argv.data());
}
#else
int main(int argc,char**argv){return run(argc,argv);}
#endif
