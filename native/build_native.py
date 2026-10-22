from pathlib import Path
import concurrent.futures, json, os, shutil, subprocess, sys

BASE=Path(__file__).resolve().parent
SRC=BASE/'runtime-source'; PROC=BASE/'processing-source'
DEPS=BASE/'native-deps'/'mingw64'; BIN=BASE/'native-build'; BIN.mkdir(exist_ok=True)
ORIGINAL=Path(os.environ.get('DV_RUNTIME_DIR',r'C:\Program Files\DV\runtime'))
CXX=Path(os.environ.get('DV_CXX',r'C:\msys64\mingw64\bin\g++.exe'))

if '--patch' in sys.argv:
    shutil.copy2(BASE/'log_safety.hpp',SRC/'src'/'log_safety.hpp')
    path=SRC/'src'/'log.cpp';text=path.read_text(encoding='utf-8')
    if '#include "log_safety.hpp"' not in text:
        text=text.replace('#include "log.hpp"','#include "log.hpp"\n#include "log_safety.hpp"',1)
        text=text.replace('static void writeMessage(const std::string &logString) {', 'static void writeMessage(const std::string &rawString) {\n\tdv_companion::LogGuard recursion;\n\tif (!recursion) return;\n\tconst auto logString = dv_companion::safeUtf8(rawString);\n\t{',1)
        text=text.replace('\t// Write to network.','\t} // Release the file lock before configuration callbacks.\n\t// Write to network.',1)
        path.write_text(text,encoding='utf-8')
    text=path.read_text(encoding='utf-8')
    if 'cachedLogLimit' not in text:
        text=text.replace('static dv::Cfg::Node logNode = nullptr;', 'static dv::Cfg::Node logNode = nullptr;\nstatic std::atomic<int64_t> cachedLogLimit{20971520};')
        text=text.replace('logNode.getLong("limitLogSize")','cachedLogLimit.load(std::memory_order_relaxed)')
        text=text.replace('if (logNode) {\n\t\tlogNode.updateReadOnly', 'if (logNode && !dv_companion::inConfigError) {\n\t\tlogNode.updateReadOnly')
        text=text.replace('static void logConfigLogger(const char *msg, bool fatal) {','static void logConfigLogger(const char *msg, bool fatal) {\n\tdv_companion::ConfigErrorGuard configurationError;')
        text=text.replace('// Try to open the specified file and error out if not possible.', 'cachedLogLimit.store(logNode.getLong("limitLogSize"), std::memory_order_relaxed);\n\t// Try to open the specified file and error out if not possible.')
        # Match the existing log-level listener; cache changes without reading a locked config node.
        text=text.replace('UNUSED_ARGUMENT(userData);\n\n\tif ((event == DVCFG_ATTRIBUTE_MODIFIED)', 'UNUSED_ARGUMENT(userData);\n\n\tif (event == DVCFG_ATTRIBUTE_MODIFIED && changeType == DVCFG_TYPE_LONG && std::string(changeKey) == "limitLogSize") {\n\t\tcachedLogLimit.store(changeValue.ilong, std::memory_order_relaxed);\n\t}\n\n\tif ((event == DVCFG_ATTRIBUTE_MODIFIED)')
        path.write_text(text,encoding='utf-8')
    text=path.read_text(encoding='utf-8')
    if 'cachedLogLimit.store(changeValue.ilong' not in text:
        at=text.rindex('static void logLevelListener(')
        tail=text[at:].replace('UNUSED_ARGUMENT(userData);','UNUSED_ARGUMENT(userData);\n\tif (event == DVCFG_ATTRIBUTE_MODIFIED && changeType == DVCFG_TYPE_LONG && std::string(changeKey) == "limitLogSize") { cachedLogLimit.store(changeValue.ilong, std::memory_order_relaxed); }',1)
        path.write_text(text[:at]+tail,encoding='utf-8')
    path=SRC/'src'/'devices_discovery.cpp';text=path.read_text(encoding='utf-8')
    marker='// Update devices present in both.'
    before,after=text.split(marker,1)
    after=after.replace('for (const auto &devName : devicesToAdd)', 'for (const auto &devName : devicesToUpdate)',1)
    path.write_text(before+marker+after,encoding='utf-8')
    path=SRC/'src'/'config_server'/'config_server_actions.cpp';text=path.read_text(encoding='utf-8')
    if 'Configuration strings must be valid UTF-8' not in text:
        text=text.replace('#include "config_server_actions.h"','#include "config_server_actions.h"\n#include "../log_safety.hpp"',1)
        text=text.replace('std::string s(str->string_view());','std::string s(str->string_view());\n\tif (dv_companion::safeUtf8(s) != s) {\n\t\tsendError("Configuration strings must be valid UTF-8 without NUL bytes.", client, receivedID);\n\t\tthrow std::invalid_argument("Invalid UTF-8 configuration string.");\n\t}',1)
        path.write_text(text,encoding='utf-8')
    path=PROC/'include'/'dv-processing'/'io'/'camera'/'davis.hpp';text=path.read_text(encoding='utf-8')
    if 'findFirstAvailable' not in text:
        needle='\t/**\n\t * Open the first DAVIS camera'
        helper='\t[[nodiscard]] static auto findFirstAvailable(const std::string_view serial = {}) {\n\t\tconst auto found = findDevices(serial);\n\t\tif (found.empty()) throw std::runtime_error("No available DAVIS camera matches serial: " + std::string(serial) + ". Check connection and other camera users.");\n\t\treturn found.front();\n\t}\n\n'
        if needle not in text:raise RuntimeError('DAVIS constructor anchor missing')
        text=text.replace(needle,helper+needle,1).replace('findDevices().at(0)','findFirstAvailable()').replace('findDevices(filterBySerialNumber).at(0)','findFirstAvailable(filterBySerialNumber)')
        path.write_text(text,encoding='utf-8')

    path=PROC/'include'/'dv-processing'/'io'/'camera'/'usb_device.hpp';text=path.read_text(encoding='utf-8')
    if 'LIBUSB_OPEN_DISCOVERY_LOCK' not in text:
        text=text.replace('static inline std::shared_mutex LIBUSB_LOG_MAP_LOCK{};', 'static inline std::shared_mutex LIBUSB_LOG_MAP_LOCK{};\n\t// WinUSB handles are exclusive: do not discover while another open is in progress.\n\tstatic inline std::mutex LIBUSB_OPEN_DISCOVERY_LOCK{};')
        text=text.replace('const compatibleCameraCallbackType &compatibleDevice, loggerCallbackType logger) {', 'const compatibleCameraCallbackType &compatibleDevice, loggerCallbackType logger) {\n\t\tstd::scoped_lock discoveryLock{LIBUSB_OPEN_DISCOVERY_LOCK};',1)
        text=text.replace('mDescriptor(deviceToOpen) {', 'mDescriptor(deviceToOpen) {\n\t\tstd::scoped_lock openingLock{LIBUSB_OPEN_DISCOVERY_LOCK};',1)
        path.write_text(text,encoding='utf-8')

generated=BIN/'include'/'dv-sdk';generated.mkdir(parents=True,exist_ok=True)
(generated/'api_visibility.h').write_text('#pragma once\n#define DVSDK_EXPORT\n#define DVSDK_NO_EXPORT\n#define DVSDK_DEPRECATED [[deprecated]]\n#define DVSDK_DEPRECATED_EXPORT DVSDK_EXPORT DVSDK_DEPRECATED\n#define DVSDK_DEPRECATED_NO_EXPORT DVSDK_DEPRECATED\n',encoding='ascii')
includes=[BIN/'include',SRC/'include',PROC/'include',DEPS/'include',DEPS/'include'/'libusb-1.0',DEPS/'include'/'opencv4',DEPS/'include'/'eigen3',Path(r'C:\msys64\mingw64\include')]
flags=['-std=c++20','-O1','-g','-DOS_WINDOWS=1','-DFMT_HEADER_ONLY=1','-DBOOST_ALL_NO_LIB=1','-DBOOST_FILESYSTEM_DYN_LINK=1','-DBOOST_NOWIDE_DYN_LINK=1','-DBOOST_PROGRAM_OPTIONS_DYN_LINK=1','-D_WIN32_WINNT=0x0A00','-DDV_MODULES_DIR='+str(ORIGINAL/'dv_modules').replace('\\','/')]
flags += ['-I'+str(i) for i in includes]
sources=['log.cpp','config.cpp','config_server/config_server_actions.cpp','config_server/config_server_connection.cpp','config_server/config_server.cpp','devices_discovery.cpp','modules_discovery.cpp','module.cpp','types.cpp','main.cpp']
def compile_one(name):
    src=SRC/'src'/name;obj=BIN/(name.replace('/','_')+'.o');log=BIN/(name.replace('/','_')+'.log')
    if obj.exists() and obj.stat().st_mtime>max(src.stat().st_mtime,(BASE/'log_safety.hpp').stat().st_mtime):return obj
    command=[str(CXX),*flags,'-c',str(src),'-o',str(obj)]
    result=subprocess.run(command,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
    log.write_bytes(result.stdout)
    print(json.dumps({'source':name,'exit':result.returncode,'log':str(log)}),flush=True)
    if result.returncode:raise RuntimeError(name+'\n'+result.stdout.decode('utf-8','replace')[:7000])
    return obj
with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:objects=list(pool.map(compile_one,sources))
libraries=['libdvsdk.dll','libboost_nowide-mt.dll','libboost_filesystem-mt.dll','libboost_program_options-mt.dll','libssl-3-x64.dll','libcrypto-3-x64.dll','libopencv_core-411.dll']
command=[str(CXX),'-o',str(BIN/'dv-runtime.exe'),*[str(x) for x in objects],*[str(ORIGINAL/x) for x in libraries],'-lws2_32','-lpsapi','-lcrypt32','-lmswsock','-lbcrypt','-liphlpapi']
result=subprocess.run(command,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
(BIN/'link.log').write_bytes(result.stdout)
print(result.stdout.decode('utf-8','replace')[:9000],flush=True)
if result.returncode:raise RuntimeError('link failed')
(BIN/'build.json').write_text(json.dumps({'compiler':str(CXX),'flags':flags,'sources':sources,'libraries':libraries,'patched':'--patch' in sys.argv},indent=2),encoding='utf-8')
print('BUILT '+str(BIN/'dv-runtime.exe'),flush=True)
if '--camera' in sys.argv:
    dest=BIN/'dv_modules';dest.mkdir(exist_ok=True)
    command=[str(CXX),*flags,'-shared',str(SRC/'modules'/'cameras'/'davis.cpp'),'-o',str(dest/'dv_davis.dll'),*[str(ORIGINAL/x) for x in ['libdvsdk.dll','libopencv_core-411.dll','libopencv_imgproc-411.dll','libusb-1.0.dll']],'-lws2_32','-lpsapi','-lbcrypt']
    result=subprocess.run(command,stdout=subprocess.PIPE,stderr=subprocess.STDOUT)
    (BIN/'camera-build.log').write_bytes(result.stdout)
    print(result.stdout.decode('utf-8','replace')[:7000],flush=True)
    if result.returncode:raise RuntimeError('camera module build failed')
    print('BUILT '+str(dest/'dv_davis.dll'),flush=True)
