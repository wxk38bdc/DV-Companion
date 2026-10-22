# 构建与打包

## C# 管理器

安装 .NET 10 SDK（含 Windows Desktop targeting pack），然后执行 README 中的 dotnet build。NuGet.Config 不启用外部包源，C# 工程没有第三方 NuGet 依赖。SDK 的 Windows 桌面引用包需要在本机可用。

## 原生后台

需要 Python 3（支持 hashlib.file_digest）、`zstandard`，以及 MSYS2 MinGW64 的 C++20 编译器。用户应先自行安装匹配版本的 DV；脚本使用其运行库进行链接。

```powershell
python -m pip install zstandard
python native/prepare_native.py
# 默认使用 C:\msys64\mingw64\bin\g++.exe 和 C:\Program Files\DV\runtime
# 如路径不同，可设置 DV_CXX 和 DV_RUNTIME_DIR 环境变量。
python native/build_native.py --patch --camera
```

prepare_native.py 按 source-archives.json 下载并校验上游源码，按 dependency-manifest.json 校验 MSYS2 头文件/导入库。网络源需要可达；历史 MSYS2 包可能被上游移走，此时需复核兼容版本，不能跳过哈希校验。脚本不安装系统包。

build_native.py 在下载的本地源码树应用补丁；不要另外重复手工应用 local-changes.patch。重新准备上游源码后，要重新带 --patch 构建。编译参数、链接库及日志写入 native/native-build。

将原生输出复制到 C# 发布目录：

```text
DVCompanion.exe
DVCompanion.dll
... .NET 配置文件
patched-runtime/
  dv-runtime.exe
  dv_modules/
    dv_davis.dll
```

不要复制或重新分发原 DV 的 GUI/SDK DLL；运行时从用户已有 DV 安装目录加载。启用修复后台时，管理器会检查已验证的 DV 提交号。其他版本、相机和工具链需要单独验证。

图标源为 app/Assets/dv-companion.svg；design_icon.py 使用 Pillow 生成 PNG 与多尺寸 ICO。脚本会生成本地多尺寸预览，不涉及用户录制数据。
