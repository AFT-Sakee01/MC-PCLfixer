# 签名配方自动更新验证

版本：1.0.0.2。日期：2026-10-21（Asia/Tokyo）。执行与记录：Codex。

当前使用与发布协议的 owner 为 [README](../../README.md)。本报告记录本地验收；公开发布结果另记维护日志。

## 验收结果

`scripts/verify.ps1` 完成 107 项核心及配方检查、44 项真实 CLR 引导检查、6 项 CNG 发布工具自检，共 **157 项通过**。构建无警告、无错误；版本一致性检查与文档 Gate 均 PASS。

四架构自包含发布成功。两个 Windows 架构的真实引导均能在内置配方不兼容后，选择已验签外部缓存配方，完成修复并自动重启 PCL。GUI 下载页正常显示 Minecraft 版本列表。Ubuntu 两架构的版本、默认源状态、修复与还原命令退出码全部为 0。

| 平台 | 环境 | 已执行验证 |
| --- | --- | --- |
| Windows ARM64 | Windows ARM64 主机 | 默认源、安装引导、签名外部配方交接、GUI 下载列表 |
| Windows x64 | Windows ARM64 主机 x64 兼容层 | 同上 |
| Linux ARM64 | WSL2 Ubuntu 26.04 原生 ARM64 | `--version` / `source-status` / `apply` / `restore` |
| Linux x64 | WSL2 中 QEMU x86_64 + Ubuntu amd64 sysroot | 同上 |

## 有效的失配测试

测试副本在一个下载方法及更新标记静态初始化中各插入一个 NOP，并将程序集版本改为 2.99.2.0。它是人工构造的测试输入，**不是官方未来发行版**。内置配方拒绝该副本；测试随后重新计算审查前后指纹，生成包含下载与更新两部分的签名配方。

57 项新检查验证了：确实失配后获取并使用新配方、恢复与幂等、精确还原、错误公钥与篡改拒绝、格式与最低版本、有效期、固定修改范围、依赖约束、HTTP/重定向/大小限制、15 秒超时、离线缓存、降级及同序号不同内容拒绝、三并发只下载一次，以及下载期间目标改变时拒绝修改。

HTTPS 传输异常测试使用 `HttpMessageHandler` 模拟响应；生产代码没有跳过 TLS 验证或允许 HTTP 的测试开关。测试签名私钥仅存在于测试进程内。合成配方不会进入公开发布源。

真实引导验收使用测试生成的有效签名缓存及不可路由的测试源地址，以验证交接和离线缓存路径：

```powershell
.\scripts\test-feed-handoff.ps1 -Runtime win-arm64 -FixtureDirectory <测试输出>/feed-tests/direct-handoff
.\scripts\test-feed-handoff.ps1 -Runtime win-x64 -FixtureDirectory <测试输出>/feed-tests/direct-handoff
```

两次均输出 `SIGNED RECIPE HANDOFF PASSED`，状态文件、实际 EXE 和预期哈希一致。测试目录：

- `Work/feed-handoff-win-arm64-3a54735394cb40f286936cf8e12627ce`
- `Work/feed-handoff-win-x64-2cdcc47dea1a4a66b5e5d4c29f14cc9e`

核心测试目录：`Work/feed-acceptance-c8509fbd25f648a89558f1329498e944`；最后一次完整 Gate 由 `scripts/verify.ps1` 在新的 `Work/tests/verify-*` 目录重新执行。

Linux 验证目录：`Work/recipe-feed-linux-cf46c391c0784988bbc71a31737be361/{arm64,x64}`。QEMU 使用与前一报告相同的进程局部 GC、硬件指令和库路径设置，不代表物理 x64 主机验收。

## 发布源与交付

发行版内置 HTTPS 源地址和固定公钥。发布者签名私钥通过 Windows CurrentUser CNG 创建，策略为不可导出 RSA-3072；未写入仓库或打包目录。公开材料仅包含公钥和签名目录。

生产公钥 SPKI SHA-256：

`9d0fa55c9a8e56cf5f0c743b2500dfa15dee6f10fe7930a673ef8607d01b5367`

首次生产目录：`sequence=1`、`profiles=1`、`min_tool_version=1.0.0.2`，仅包含已审查的官方 2.99.0.0 配方。生产目录已使用 `tools/feed-publisher` 的 `verify` 命令实际验签。

新本地目录为 `Artifacts/Windows-ARM64-Feed` 和 `Artifacts/Windows-x64-Feed`。分别已启用引导，且 `source-status` 输出 `ENABLED` 与上述公钥指纹。原先 Ready / Auto 目录保留。公开发行仅包含辅助程序及源码，不上传本地 PCL 副本。

| 内容 | SHA-256 |
| --- | --- |
| 原 ZIP 及其 Backups 副本 | `6044f82cf37f9c8cee746c537bf17dd31699e11d435edb6dbd8fd54b81c8ee5b` |
| 官方 2.99 输入与精确还原结果 | `6b6e1260cb5a1d6a303855f1c60cab3d0bc1aebba7e40a964180188b271e7386` |
| 四架构对官方 2.99 的修复结果 | `16cea70bb86ef6c9841d263d35d3c8c4a1185b7d06d30d0b143630bba13aca3d` |
| 合成失配测试副本修复结果 | `522eb42f2ece547e93a7dd3bc7bfd4d4df9de73e2a2c7915458ab8f32eeb74ee` |

## 实际限制

自动获取只消费维护者已经审查、测试并签名的配方，不会自动推导未知 PCL 代码。尚无匹配配方或网络不可用时不改 EXE，下一次打开 PCL 再试。程序不驻留、不无限轮询。

目标方法签名、运行时、引导方式、公钥或配方协议发生变化，可能需要更新辅助程序本身。下载功能完整安装验证沿用 1.0.0.0 报告，本次未重复下载全部游戏及整合包；五个恢复方法的现行生产配方未改变。Linux 工具仍只修复 Windows EXE。
