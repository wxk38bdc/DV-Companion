# 测试说明

## 无相机测试

`--self-test report.json` 执行协议、XML 校验、保存失败保护、连续保存、同步引用和 Windows Job 生命周期测试。需要本机回环端口与创建自有子进程的权限。代码中的 10000001/10000002 是虚构测试编号。

`--save-cycle-test report.json path/to/dv-runtime.exe` 启动独立后台，用停止状态的处理模块验证新增/删除模块、连线、参数和布局的连续编辑保存及重启后恢复，不打开相机、不录制、不启动 GUI。

`--render-preview folder` 生成三个页面的默认/最小窗口图片及布局信息。预览会只读枚举本机设备和进程；不要将包含真实设备信息的图片或报告直接提交到仓库。

## 硬件测试

硬件和 GUI 测试必须在没有其他采集任务、相机可独占使用时手动运行。不要在公共 CI 中自动执行。

`--camera-test report.json path/to/dv-runtime.exe` 会打开两台相机、同步时间戳并产生短录制；需显式设置 `DV_TEST_PRIMARY_SERIAL`、`DV_TEST_SECONDARY_SERIAL`，二者必须不同。缺少变量时拒绝运行。

`--restore-test report.json input.xml` 使用输入 XML 中的相机编号与运行标志核对恢复；会重建结构并打开项目要求的相机，保持文件录制模块停止。

`--gui-smoke report.json` 启动原 DV；需要用户处理退出和项目保存对话框。

报告、设备编号、绝对路径、配置、录制及转储保留在本地，提交问题时只提供去除隐私后的最小信息。
