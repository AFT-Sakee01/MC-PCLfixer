# 配方自动维护验证报告

版本：1.0.0.3

日期：2026-10-21（Asia/Tokyo）

生成模型：Codex

当前用法和运行边界见 [README](../../README.md)。

## 实际结果

维护代码提交 `e6a1a58` 已推送到公开仓库。完整维护流程成功发布配方序号 **2**，发布提交为 `87363d1bd491d08eb4f2c74792db7b88f766c851`。随后再次执行 `run --publish`，自动快进前次发布数据，返回 `unchanged`，没有再次签名或发布。

本地当前聊天的“PCL 配方自动维护”已启用，间隔六小时，失败通知策略为 `failed_runs_only`。正常命令是 `python -X utf8 scripts/maintain-recipes.py run --publish`。调度依赖本机电脑、桌面应用、Windows 发布者账户及现有 CNG 密钥；私钥没有导出或上传。

| 验证 | 结果 |
| --- | --- |
| 核心修复及配方源 | 107 项通过 |
| 实际 .NET Framework 引导组件 | 44 项通过 |
| CNG 发布工具 | 6 项通过 |
| 等价适配维护工具 | 13 项通过；含三次各 10 项候选检查 |
| 维护流程单元测试 | 39 项通过 |
| 官方下载器单元测试 | 14 项通过 |
| 合计 | 223 项外层检查通过 |
| 版本一致性、文档 Gate、Git 空白检查 | 通过 |
| 实际官方获取、候选验证、签名、推送、远端字节核对 | 通过 |
| 第二次实际运行 | 自动同步后无需更新，序号仍为 2 |

验证命令包括 `scripts/verify.ps1`、`scripts/maintain-recipes.py check`、`scripts/maintain-recipes.py run` 及两次 `scripts/maintain-recipes.py run --publish`。生产发布前重新执行了完整回归套件。

## 输入与发布证据

官方发行是 **PCL 2.99.0.0**。GitHub 发行没有二进制附件，使用其标签所指提交的仓库根文件 `最新正式版.zip`。通过固定提交、Git blob 哈希和大小核对下载，随后核对 EXE 程序集版本。

- 官方源码提交：`6b9a50df911c9d68b69b24c979efba477fb14b67`
- 官方 ZIP Git blob：`52843e0ba1c97a888630be8326fbc5a132157ccc`
- 官方 ZIP SHA-256：`01877f06df84cbab376c286a633e76ef7fca0bb469e26df97f3b09b5abd503bc`
- 官方 EXE SHA-256：`6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386`
- 验证配方 SHA-256：`bd97f003deff7323c6bf071c510f490fd397e7278f1cbd791debc384f8472e2e`
- 线上签名目录 SHA-256：`f635ab36d9d80adede25ba4ee9b25d74b3416cee43a82d64d43d24d66459b24f`
- 目录到期时间：`2026-11-19T17:18:34.632015Z`
- 最低客户端版本：`1.0.0.2`，现有用户端无需为维护机制换包。

完整本地证据：

- `Work/maintenance-7e7459e3b92148dda9fe562cea15088d/result.json`：验证通过、不签名的运行。
- `Work/maintenance-669874bf59e24a6781bbeeaadaee0713/result.json`：生产签名发布。
- `Work/maintenance-669874bf59e24a6781bbeeaadaee0713/candidate/evidence.json`：候选十项断言与输入/输出哈希。
- `Work/maintenance-6f965f53be8449a8932a4d1fe85f7bef/result.json`：连续运行及无变化结果。

实现期间的一次整链运行在回归阶段返回非零，停止于签名前；后续完整运行通过。错误处理测试另行模拟了验签、适配、证据、回归、签名、远端验签和推送失败，确认失败不会继续发布。

## 覆盖与限制

自动等价适配测试包含仅版本变化、NOP 插入、分支/异常边界映射、前缀前 NOP 可接受，以及前缀和操作码之间的 NOP 必须拒绝。依赖逻辑变化、未知目标变化、坏签名、不同公钥、保护路径、ZIP 异常、远端竞争、未提交改动和远端维护代码变化均有拒绝验证。

候选检查绑定输入、配方、输出三个哈希；修复和还原实际写入全新隔离副本。公开发布只提交配方、维护状态和追加日志，保留全部已支持配方，容量满时拒绝自动淘汰。签名验证基线来自远端目录，不信任未经认证的 JSON。

本次没有新的官方业务逻辑变化，实际发布复用了已验证的 2.99.0.0 配方并建立自动维护状态。普通下载、在线整合包及本地导入的既有 GUI 证据见 [初次验证报告](Codex-Verification-v1.0.0.0-20261021.md)。本次结构与回归测试不能被解释为已经验证任意未来版本的完整下载业务；定时任务遇到业务变化时必须补充针对性实际安装验证，否则停止发布并报告失败。

原 `PCL2DLA.zip` 与 `Backups/original-20261020-145100/PCL2DLA.zip` 的 SHA-256 都保持：

`6044f82cf37f9c8cee746c537bf17dd31699e11d435edb6dbd8fd54b81c8ee5b`

没有修改或执行这两个 ZIP，没有修改 Backups 内容，没有覆盖既有 Ready、Auto、Feed 交付目录。
