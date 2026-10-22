#include <dv-processing/io/mono_camera_recording.hpp>
#include "log_safety.hpp"
#include <iostream>
int main(int argc,char**argv){
 if(argc==1){
  using namespace dv_companion;
  if(safeUtf8("abc")!="abc"||safeUtf8("\xE4\xB8\xAD")!="\xE4\xB8\xAD"||safeUtf8(std::string("a\0b",3))!="a\\x00b"||safeUtf8("\xFF")!="\\xFF"||safeUtf8("\xED\xA0\x80")!="\\xED\\xA0\\x80"||safeUtf8("\xF4\x90\x80\x80")!="\\xF4\\x90\\x80\\x80"||safeUtf8("\xC0\xAF")!="\\xC0\\xAF"||safeUtf8("\xE4\xB8")!="\\xE4\\xB8")return 2;
  {LogGuard first;if(!first)return 3;{LogGuard nested;if(nested)return 4;}if(!logging)return 5;}if(logging)return 6;
  {ConfigErrorGuard first;{ConfigErrorGuard nested;}if(!inConfigError)return 7;}if(inConfigError)return 8;
  std::cout<<"UTF8 and reentry guards passed\n";return 0;
 }
 for(int i=1;i<argc;i++)try{
  dv::io::MonoCameraRecording file(argv[i]);long long count=0,first=-1,last=-1;bool sorted=true;
  while(auto events=file.getNextEventBatch("")){for(const auto&e:*events){if(first<0)first=e.timestamp();if(last>e.timestamp())sorted=false;last=e.timestamp();count++;}}
  std::cout<<argv[i]<<"\t"<<file.getCameraName()<<"\t"<<count<<"\t"<<first<<"\t"<<last<<"\t"<<(sorted?"sorted":"UNSORTED")<<"\n";
  if(count==0||!sorted)return 9;
 }catch(const std::exception&e){std::cerr<<e.what()<<"\n";return 10;}
}
