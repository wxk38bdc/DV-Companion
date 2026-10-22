// Local DV Companion patch; Apache-2.0. No platform-specific encoding assumptions.
#pragma once
#include <string>
#include <string_view>

namespace dv_companion {
inline std::string safeUtf8(std::string_view input) {
    std::string out;
    out.reserve(input.size());
    constexpr char hex[]="0123456789ABCDEF";
    for (std::size_t i=0;i<input.size();) {
        const auto c=static_cast<unsigned char>(input[i]);
        std::size_t n=c<0x80?1:(c>=0xC2&&c<=0xDF?2:(c>=0xE0&&c<=0xEF?3:(c>=0xF0&&c<=0xF4?4:0)));
        bool valid=n>0&&i+n<=input.size();
        for(std::size_t j=1;valid&&j<n;++j)valid=(static_cast<unsigned char>(input[i+j])&0xC0)==0x80;
        if(valid&&n>=3){const auto b=static_cast<unsigned char>(input[i+1]);valid=!(c==0xE0&&b<0xA0)&&!(c==0xED&&b>=0xA0)&&!(c==0xF0&&b<0x90)&&!(c==0xF4&&b>=0x90);}
        // NUL cannot safely traverse a C string-based configuration API.
        if(valid&&c!=0){out.append(input.substr(i,n));i+=n;}
        else{out.append("\\x");out.push_back(hex[c>>4]);out.push_back(hex[c&15]);++i;}
    }
    return out;
}
inline thread_local bool logging=false;
inline thread_local bool inConfigError=false;
class ConfigErrorGuard {
    bool previous;
public:
    ConfigErrorGuard():previous(inConfigError){inConfigError=true;}
    ~ConfigErrorGuard(){inConfigError=previous;}
};
class LogGuard {
    bool entered;
public:
    LogGuard():entered(!logging){if(entered)logging=true;}
    ~LogGuard(){if(entered)logging=false;}
    explicit operator bool()const{return entered;}
    LogGuard(const LogGuard&)=delete;
    LogGuard& operator=(const LogGuard&)=delete;
};
}
