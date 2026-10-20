# PCL 本地下载修复验证报告

版本：1.0.0.0。作者模型：Codex。验证日期：2026-10-21（Asia/Tokyo）；对应 UTC 2026-10-20。

## 输入与备份证据

| 输入 | SHA-256 |
| --- | --- |
| 原 `PCL2DLA.zip` | `6044f82cf37f9c8cee746c537bf17dd31699e11d435edb6dbd8fd54b81c8ee5b` |
| `Backups/original-20261020-145100/PCL2DLA.zip` | 与原 ZIP 相同 |
| 官方 2.99.0.0 EXE | `6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386` |
| 修复后 2.99.0.0 EXE | `09c0a80b75b54f7d9384d04598cd44a84f587cfca71494c532d8985e457d66d6` |

原 ZIP 只读并先建立备份。实际运行全部使用 `Work/` 内新副本；旧版本未启动、未更新，原 `Setup.ini` 未改。未提取或读取原账号日志、私人启动脚本。完整 ZIP 的前后哈希一致。

最新官方版本与旧版来源：[2.99.0.0 发布说明](https://github.com/Meloong-Git/PCL/releases/tag/2.99.0.0)、[官方仓库](https://github.com/Meloong-Git/PCL)。新版本来源提交为 `6b9a50df911c9d68b69b24c979efba477fb14b67`；旧版 2.14.0.0 来源提交为 `5ef81e490ae46b1100d61e33d0183094c7a80ca8`。修复未使用用户唯一旧 ZIP 作为可写材料。

## 行为与结构测试

实际执行：

```powershell
.\.dotnet\dotnet.exe run --project tests/Tests.csproj -c Release -- `
 "Work/upstream/latest/Plain Craft Launcher 2.exe" "Work/tests/final-20261021"
```

结果：**ALL 30 CHECKS PASSED**。包含以下方面：

- 五处方法及依赖指纹、其他方法和资源不变、重复修复幂等。
- 模拟改版 2.99.1.0 与重新编号后重应用；方法或依赖变化、半成品修复、无效 PE 拒绝。
- 原样备份、精确还原、更新后对应备份、配置不变、Windows 文件占用拒绝、损坏备份拒绝。
- ZIP 与备份路径保护；测试原件前后哈希不变。
- JSONL 结构、跨重启错误排重、Fatal 不排重、并发日志、三份压缩归档、UTF-8 文件大小上限、日志失败不影响主程序。

实际发布命令为 `scripts/publish.ps1`，四个 RID 均生成自包含单文件程序。Windows 产物程序集/文件/产品版本均为 1.0.0.0。

`python scripts/check-version.py` 输出 `Version consistent: 1.0.0.0`；`python Docs/validate_docs.py --root . --strict` 输出 `RESULT: PASS`。功能索引 5 项、接口索引 25 项，路径及关联 ID 均核对通过。

## Windows GUI 实测

环境：Windows 11 ARM64，隔离目录 `Work/gui-test/`，无账号登录。GUI 使用的修复 EXE 与交付工具生成的 EXE **完整 SHA-256 相同**。

| 路径 | 结果 | 磁盘和日志证据 |
| --- | --- | --- |
| 普通 Minecraft 下载 | 1.21.11 安装成功 | JAR 31,152,600 字节；与下载 JSON 中 `downloads.client.sha1` 相同；日志 00:10:17.842 安装成功 |
| 在线整合包一键安装 | Modrinth 的 Fabulously Optimized 安装成功 | 1.21.11/Fabric 实例、48 个 Mod JAR；日志 00:15:25.046 `Finished`，00:15:25.078 成功 |
| 本地整合包导入 | `PCL-Repair-Local-Test.mrpack` 安装成功 | 1.21.11 实例与 `repair-test-marker.txt` 内容 `LOCAL MODPACK RESTORED`；日志 00:21:26.316 `Finished`，00:21:26.319 成功 |

在线测试遇到过远端 503，原有下载器重试后完成。测试结束后已关闭测试启动器。未启动 Minecraft 游戏进程，未验证账户登录；也未逐一实测所有第三方整合包来源或格式，CurseForge 远端安装未单独实测。

## 架构验证

| 产物 | 环境 | 已验证范围 |
| --- | --- | --- |
| Windows ARM64 | 当前 ARM64 Windows 原生辅助程序 | 版本、修复、重复检查、还原；完整 30 项测试；PCL GUI 三条下载路径 |
| Windows x64 | ARM64 Windows 的 x64 兼容运行 | 版本、修复、还原；没有物理 x64 Windows 实机 |
| Linux ARM64 | WSL2 Ubuntu 26.04 ARM64 | 版本、修复、还原；没有运行 PCL GUI |
| Linux x64 | WSL2 + QEMU x86_64 + 隔离 Ubuntu x64 系统库 | 版本、修复、还原；仿真验证，不是物理 x64 Ubuntu 实机 |

各架构修复后 EXE 的 SHA-256 相同，还原后均与官方输入 SHA-256 相同。

QEMU 默认配置运行 .NET 10 时出现过越界/内存异常；限制测试进程 GC 堆并关闭仿真不稳定的 JIT/硬件优化后完成。测试进程参数：`DOTNET_GCHeapHardLimit=40000000`、`DOTNET_EnableWriteXorExecute=0`、`DOTNET_EnableHWIntrinsic=0`、`DOTNET_TieredCompilation=0`。这些参数只用于该仿真测试，没有写入系统配置或交付启动脚本。x64 依赖从 Ubuntu 官方包索引获取，按索引 SHA-256 校验，解压至 `Work/qemu-ubuntu/sysroot/`，没有 sudo 安装。

## 边界

- 更新重应用机制经过模拟兼容更新验证，不能证明尚未发布的任意 PCL 版本都兼容。
- 官方更新器立即自启可能绕过辅助入口；下一次通过辅助程序启动才会再次检查修复。
- Linux 交付的是修复工具。PCL 的 WPF 界面仍需要 Windows；本项目不宣称原生 Ubuntu 启动器兼容。
- 还原依赖 `.pcl-repair/` 原始备份与凭据；损坏时拒绝写入。
- 没有安装后台任务、注入系统、改动账号或移除正版购买提示。

当前用法、代码位置、日志策略与维护入口以根 `README.md` 为 owner 文档。
