# 自动更新后恢复验证

版本：1.0.0.1。日期：2026-10-21（Asia/Tokyo）。执行与记录模型：Codex。

当前用法和实现说明由项目 [README](../../README.md) 维护。本报告记录本次已执行验证，不承诺未来未审查版本兼容。

## 结果

在 Windows ARM64 主机上，ARM64 和 x64 辅助程序均完成：官方 PCL 更新器替换 EXE → 自动重启 → 外部引导交接 → 重新修复 → 自动启动正常 PCL 界面。两次均打开下载页，看到正常 Minecraft 版本列表。全部 50 项核心检查及 44 项引导检查通过。

四架构发布成功。Ubuntu 26.04 ARM64 原生及 x64 QEMU 的辅助程序均完成修复和精确还原，命令退出码为 0。Linux 结果不代表 PCL GUI 可以在 Linux 原生运行。

## 自动更新测试边界

测试调用官方 2.99.0.0 二进制内真实的 `--update` 实现，而非用测试代码替代更新器。使用同一官方版本模拟新下载到的完整 EXE，覆盖已修复副本，测试其真实删除、复制、自动重启与外部引导链。没有声称服务器已经提供另一个新版本，也没有运行一次真实远端版本升级。

测试只向更新器传入脚本新建的等待进程 PID；未终止用户原有启动器或游戏。全部测试运行在新 `Work/` 子目录。

| 平台 | 实际环境 | 验证结果 |
| --- | --- | --- |
| Windows ARM64 | ARM64 主机原生辅助程序；PCL 使用其自身运行架构 | 官方替换重启、自动恢复、正常 GUI 和版本列表 |
| Windows x64 | 同一 ARM64 主机的 Windows x64 兼容执行 | 官方替换重启、自动恢复、正常 GUI 和版本列表 |
| Ubuntu ARM64 | WSL2 Ubuntu 26.04 ARM64 | `--version`、`apply`、`restore` 全部成功 |
| Ubuntu x64 | ARM64 WSL2 下 QEMU x86_64 与 Ubuntu amd64 sysroot | `--version`、`apply`、`restore` 全部成功 |

Windows 测试目录：

- `Work/update-e2e-win-arm64-6116b6e87f6d4787b4e5b1e3f3946347`
- `Work/update-e2e-win-x64-cff80d0474e84cf386215b9a029d94d5`

Linux 测试目录：`Work/linux-auto-smoke-7eeb8704361e4426998564ddd63efe34/{arm64,x64}`。Linux 工具也复制到各自测试目录执行，使日志与备份留在隔离目录。

测试结束后关闭了两个 Windows 测试窗口。

## 检查与证据

运行命令：

```powershell
.\scripts\publish.ps1
.\scripts\verify.ps1
.\scripts\test-update.ps1 -Runtime win-arm64
.\scripts\test-update.ps1 -Runtime win-x64
```

`verify.ps1` 输出 `ALL 50 CHECKS PASSED`、`PASS 44 bootstrap component checks`、`Version consistent: 1.0.0.1` 和文档 `RESULT: PASS`。两架构更新脚本均输出 `REAL UPDATE HANDOFF PASSED`，且日志包含 `auto_repaired`。GUI 检查显示正式版 26.3 和预览版 26.4-snapshot-5。

核心检查包括：方法结构与依赖拒绝、幂等、其他方法及资源不变、文件锁、备份损坏拒绝、还原精度、ZIP/备份目录保护、配置精确恢复、保留用户后续编辑、外部域管理器冲突拒绝、状态损坏恢复、重复交接不重复重启、旧补丁升级的原始还原链，以及日志排重、并发、滚动、大小限制与写入失败容错。

引导检查包括：Windows CRT 参数引用规则及实际子进程往返、状态结构及大小限制、Windows 文件名大小写、任务进程跳过、真实 CLR 加载、.NET 4.8 目标框架语义、路径兼容开关和 `\\?\` 路径读写。

完整更新配方生成器另行完成 5 项验证：初始化前后指纹及转换范围符合审查结果。五处游戏下载方法的修复内容与 1.0.0.0 配方相同；此前已验证的 Minecraft 安装、在线 Modrinth 整合包和本地 mrpack 导入见 [初始验证报告](Codex-Verification-v1.0.0.0-20261021.md)，本次未重复完整游戏下载。

Linux x64 的 QEMU 调用使用以下仅影响测试子进程的环境设置：

```text
LD_LIBRARY_PATH=<sysroot>/usr/lib/x86_64-linux-gnu
DOTNET_GCHeapHardLimit=40000000
DOTNET_EnableWriteXorExecute=0
DOTNET_EnableHWIntrinsic=0
DOTNET_TieredCompilation=0
```

## 内容校验

| 文件 | SHA-256 |
| --- | --- |
| 原始 `PCL2DLA.zip` | `6044f82cf37f9c8cee746c537bf17dd31699e11d435edb6dbd8fd54b81c8ee5b` |
| `Backups/original-20261020-145100/PCL2DLA.zip` | `6044f82cf37f9c8cee746c537bf17dd31699e11d435edb6dbd8fd54b81c8ee5b` |
| 官方 PCL 2.99.0.0 输入及还原结果 | `6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386` |
| 1.0.0.1 四架构修复结果 | `16cea70bb86ef6c9841d263d35d3c8c4a1185b7d06d30d0b143630bba13aca3d` |

原始 ZIP 和备份在本次工作结束阶段再次计算哈希，均与原记录一致。原先 `Windows-ARM64-Ready` / `Windows-x64-Ready` 目录保留，新交付使用 `Artifacts/Windows-ARM64-Auto` / `Artifacts/Windows-x64-Auto`；两份新 EXE 的哈希均为上表修复结果，自动加载已经启用。

## 已知限制

当前自动恢复面向已审查的 .NET Framework PCL 和 EXE 替换式更新器。方法、直接依赖或更新标记初始化发生未知变化时拒绝修改；更新器删除外部文件、覆盖局部配置、改启动器文件名或迁移运行时后需要适配。未知版本失败会显示提示，不自动重启循环。

Windows x64 和 Linux x64 均未在物理 x64 主机上验证。测试没有覆盖所有可能的整合包来源或第三方配置组合。辅助工具不修改官方自动更新设置；仅将已审查 UpdateMark 标识改为第三方标识，促使官方更新器选择完整包。
