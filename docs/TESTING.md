# 测试说明

## 无相机测试

`--self-test report.json` 执行协议、XML 校验、保存失败保护、连续保存、同步引用和 Windows Job 生命周期测试。需要本机回环端口与创建自有子进程的权限。代码中的 10000001/10000002 是虚构测试编号。

`--save-cycle-test report.json path/to/dv-runtime.exe` 启动独立后台，用停止状态的处理模块验证新增/删除模块、连线、参数和布局的连续编辑保存及重启后恢复，不打开相机、不录制、不启动 GUI。

`--render-preview folder` 生成五个页面的默认/最小窗口图片及布局信息。预览会只读枚举本机设备和进程；不要将包含真实设备信息的图片或报告直接提交到仓库。

基础测试目前共 68 项，包含同步状态陈旧、身份变化、活动录制阻止同步、录制预检失败、部分启动失败、回执丢失、选择性停止及文件名冲突保护。测试会在报告旁创建本地 test-data；其中的模拟 AEDAT4 只用于测试文件存在性，不是可播放的真实录制。

## 硬件测试

硬件和 GUI 测试必须在没有其他采集任务、相机可独占使用时手动运行。不要在公共 CI 中自动执行。

`--camera-test report.json path/to/dv-runtime.exe` 会打开两台相机、同步时间戳并产生短录制；需显式设置 `DV_TEST_PRIMARY_SERIAL`、`DV_TEST_SECONDARY_SERIAL`，二者必须不同。缺少变量时拒绝运行。

`--restore-test report.json input.xml` 使用输入 XML 中的相机编号与运行标志核对恢复；会重建结构并打开项目要求的相机，保持文件录制模块停止。

`--stereo-test report.json path/to/dv-runtime.exe` 需要恰好两台可见相机及正确连接的同步线，会创建独立后台、读取真实设备编号并验证两次时间戳同步；不启动文件录制。

`--recording-test report.json path/to/dv-runtime.exe project.xml` 使用项目的隔离副本，将文件输出重定向到测试目录并移除临时可视化模块；会打开相机、执行同步并生成短录制。此回归工具要求三个模块名为 output_top、output_bottom、output_stereo，应用的正常录制控制不受此命名约束。

`--recording-ui-test report.json path/to/dv-runtime.exe project.xml` 打开使用隔离项目后台的管理器，供手动验证勾选、开始和停止。关闭后正常退出本测试后台。

上述测试生成的报告、XML 和录制含本机信息，只应保留在本地。不要将真实项目复制进源码树。测试完成后可删除本次测试输出的录制文件；删除前确认它们属于独立测试目录。

`--gui-smoke report.json` 启动原 DV；需要用户处理退出和项目保存对话框。

报告、设备编号、绝对路径、配置、录制及转储保留在本地，提交问题时只提供去除隐私后的最小信息。
