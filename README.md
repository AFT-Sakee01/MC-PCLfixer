# PCL (Local Download Repair)

版本 **1.0.0.2**。PCL 原作者：**龙腾猫跃**，[原项目](https://github.com/Meloong-Git/PCL) · [赞助作者](https://meloong.com/afd/a/LTCat)。

这是第三方独立制作的本地修复工具。针对官方 PCL 2.99.0.0，恢复普通 Minecraft 下载、在线整合包一键安装和本地整合包导入。包含 Windows x64 / ARM64 与 Linux x64 / ARM64 的辅助程序和完整源码。

## 直接使用

从 [GitHub Releases](https://github.com/AFT-Sakee01/MC-PCLfixer/releases) 下载对应架构的辅助工具。公开发行包不包含 PCL 本体，需要另行取得官方 PCL。

开发工作区另外准备了 `Artifacts/Windows-ARM64-Feed/` 与 `Artifacts/Windows-x64-Feed/` 两个全新副本，已启用自动修复，直接双击 **Plain Craft Launcher 2.exe** 即可。它们不包含原 ZIP 的账号、日志、游戏或启动脚本，也不上传到公开发行版。原始 `PCL2DLA.zip`、`Backups/` 和此前的交付目录保持原样。

如果用于其他 PCL 目录：

1. 从 `Artifacts/Packages/` 选择对应系统与架构的包并解压。
2. 将辅助程序放在 PCL EXE 旁边。PCL 默认文件名为 `Plain Craft Launcher 2.exe`。
3. 关闭正在运行的 PCL，双击辅助程序一次，完成修复及自动加载配置。其他文件名可通过下面的命令指定。
4. 此后可以直接打开 PCL；自更新自动重启时，会检查内置及缓存配方，必要时从预设发布源获取兼容新配方，修复后进入界面。保留辅助程序、引导 DLL、EXE 旁的配置及状态文件和 `.pcl-repair/` 备份目录。

Windows PowerShell：

```powershell
.\PCL-Download-Repair.exe inspect ".\Plain Craft Launcher 2.exe"
.\PCL-Download-Repair.exe apply   ".\Plain Craft Launcher 2.exe"
.\PCL-Download-Repair.exe run     ".\Plain Craft Launcher 2.exe"
.\PCL-Download-Repair.exe enable-auto  ".\Plain Craft Launcher 2.exe"
.\PCL-Download-Repair.exe disable-auto ".\Plain Craft Launcher 2.exe"
.\PCL-Download-Repair.exe restore ".\Plain Craft Launcher 2.exe"
```

`inspect` 只使用内置和本地缓存配方检查，不联网；`apply` 必要时获取新配方，备份并修复；`run` 修复、启用自动加载并启动；`enable-auto` 修复并启用自动加载但不启动；`disable-auto` 关闭自动加载，保留下载补丁；`restore` 关闭自动加载并精确还原原 EXE。成功退出码为 0，拒绝或失败为 2。`--version` 显示版本和进程架构。不要把 ZIP 或备份目录作为目标。

停用时会还原没有被另外编辑过的原配置。如果用户后来修改过配置，则只移除本工具的 CLR 加载项，保留用户设置。引导 DLL 可能被同目录其他 PCL 使用，停用时保留为不活动文件。已有其他 CLR 域管理器时拒绝覆盖。

Ubuntu 使用对应 Linux 包：

```bash
chmod +x PCL-Download-Repair
./PCL-Download-Repair inspect "/path/to/Plain Craft Launcher 2.exe"
./PCL-Download-Repair apply   "/path/to/Plain Craft Launcher 2.exe"
./PCL-Download-Repair restore "/path/to/Plain Craft Launcher 2.exe"
```

Linux 版是原生运行的 **EXE 修复工具**。PCL 本体依赖 Windows WPF，不能因此变成 Ubuntu 原生启动器；Linux 上的 `run` 会明确拒绝。辅助程序自带 .NET 运行时，仍需要发行版的 glibc、ICU、OpenSSL、zlib 等常规系统库。

## 更新与兼容范围

当前配方来自官方 2.14.0.0 和 2.99.0.0。启动入口每次检查方法结构；将来官方更新后，只要受影响方法及依赖仍匹配，就会重新修复，不依赖固定文件偏移、元数据编号或完整 EXE 哈希。

**新配方需要维护者先适配、验证并签名发布。** 辅助工具能自动获取已经发布的配方，不会自行猜测未来代码。方法缺失、内容改变、关键依赖变化或第三方只改了一部分，且没有已发布的匹配配方时，会拒绝写入。工具没有后台驻留、计划任务、注册表修改或全局 CLR 配置。

自动加载通过该 PCL 专属 `.exe.config` 中的标准 [.NET Framework AppDomainManager](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/runtime/appdomainmanagerassembly-element) 入口实现。启动时引导 DLL 对比已验证 EXE 的哈希：相同就直接继续；更新后不相同，则在主界面出现前启动辅助程序并退出。辅助程序等待该进程结束，备份、校验和修复新 EXE，更新状态，再自动启动 PCL。正常使用不必手动关闭或另开辅助程序。

引导在默认应用域最早初始化阶段设置已验证 PCL 使用的 .NET Framework 4.8 兼容语义，确保 PCL 的扩展长度路径正常工作；不修改其他应用或次级应用域。

当前 PCL 更新器只替换 EXE，因此外部配置、引导 DLL 和辅助程序会保留。若未来更新器改为重建整个目录、改 EXE 名、覆盖配置或迁移出 .NET Framework，需重新适配。引导不接管 `--update`、`--gpu`、`--memory` 等官方任务进程，保留它们原有退出码和等待语义。

补丁同时将已审查的 `UpdateMark` 初始化字面量改为第三方标识，让官方更新器选择**完整包**；已修改的 EXE 不适合作为官方差分包的基底。不会改变官方更新源或更新设置。

自动修复暂时无法联网或发布源尚无匹配配方时，会显示错误并停止自动重启。稍后直接打开 PCL 即可重新检查，不必替换辅助程序。也可运行 `disable-auto` 后以当前未修复状态打开 PCL。DLL 本身缺失时 CLR 会在进入 PCL 前报错，运行辅助工具重新安装，或用 `disable-auto` 解除配置。

未修改 PCL 的自动更新设置、登录、正版购买提示、下载校验、赞助或主题功能。工作区旧版本的 `SystemSystemUpdate:3` 设置保留。

## 配方自动更新与发布

发行版已内置本仓库的 [稳定配方源](https://raw.githubusercontent.com/AFT-Sakee01/MC-PCLfixer/main/feed/stable.json) 和固定发布者公钥，无需用户额外绑定。内置配方匹配时不联网；否则先检查已签名缓存，再尝试一次 HTTPS 下载。正常 TLS 验证保持开启，不接受重定向；总网络期限为 15 秒，响应上限 8 MiB。

签名覆盖完整目录原始字节，算法为 RSA-PSS-SHA256。辅助工具验证签名、格式、有效期、最低工具版本和单调发布序号，再验证具体 PCL 方法与依赖。远端不能增加修复目标，仍限于既有五个下载方法及更新标记。下载期间目标发生变化会拒绝写入。签名配方是可信发布者提供的可执行 IL，只有审核过的配方才能签名。

缓存位于辅助工具旁的 `recipe-cache/<源标识>/feed.json`；每次使用重新验签。有效且匹配的缓存可离线使用。过期缓存继续保留发布序号，阻止降级；损坏缓存会明确拒绝，不能通过忽略旧缓存重置这个边界。已修复 PCL 的正常启动不依赖联网。发布源无新配方时不会后台无限轮询，下一次打开 PCL 会重试。

可选管理命令：

```powershell
.\PCL-Download-Repair.exe source-status
.\PCL-Download-Repair.exe source-disable
.\PCL-Download-Repair.exe source-set "https://raw.githubusercontent.com/AFT-Sakee01/MC-PCLfixer/main/feed/stable.json" ".\publisher-public.pem"
```

`source-set` 可使用仓库中的 `feed/publisher-public.pem` 重新启用，也可绑定自行维护的 HTTPS 地址与对应公钥。覆盖设置写入 `recipe-source.json`；不会从网络自动接受新公钥。关闭源会保留缓存，但只允许内置配方继续工作。

维护者使用 `tools/feed-publisher/FeedPublisher.csproj` 生成模板和签名。私钥保存在 Windows CurrentUser CNG，不导出到文件；公钥、签名目录和源码公开。初次创建源时使用 `keygen <密钥名> <新公钥.pem>`。已有发布者应使用原密钥，更新 `sequence`、UTC 有效期和审查后的 `profiles`，保留仍受支持的旧配方：

```powershell
.\.dotnet\dotnet.exe run --project tools/feed-publisher/FeedPublisher.csproj -c Release -- template "Work\catalog-new.json"
# 编辑并验证新 catalog；示例密钥名只是本地密钥标识，不是私钥。
.\.dotnet\dotnet.exe run --project tools/feed-publisher/FeedPublisher.csproj -c Release -- sign PCL-RecipePublisher-AFTSakee01-MCPCLfixer "Work\catalog-new.json" "Work\stable-new.json"
```

签名工具只新建输出，不覆盖历史。完成验证后，将新的签名文件发布为仓库 `feed/stable.json`。模板默认有效期 30 天，续期也必须递增序号并重新签名。发布工具不会自动推送或替维护者审核新 PCL。公钥或引导协议需要变化时仍可能需要发布新版辅助程序。

## 工作原理与文件

| 功能 | 文件 / 入口 | 验证 |
| --- | --- | --- |
| 五处下载入口恢复 | `src/Recipe.cs`、`recipes/download-2.99.json`；符号 IL 与依赖指纹 | 结构测试及实际 GUI 安装 |
| 兼容检查、备份、还原 | `src/Repair.cs`；`Inspect`、`Transform`、`Apply`、`Restore` | 幂等、更新替换、异常输入、备份损坏、锁定测试 |
| 启动包装入口 | `src/Program.cs`；`Main` | 四架构 CLI 验证 |
| 更新后自动恢复 / 外部加载 | `src/AutoRepair.cs`、`bootstrap/Manager.cs` | 配置保留、启动交接及真实 PCL `--update` 流程 |
| 完整包更新兼容 | `src/UpdateCompatibility.cs`、`recipes/update-2.99.json` | 标记前后指纹、未知初始化代码拒绝 |
| 签名配方源与缓存 | `src/RecipeFeeds.cs`、`recipes/source.json`、`feed/` | `tests/FeedTests.cs`：失配、签名、回滚、离线、并发、超时、大小、目标变化 |
| 配方签名发布工具 | `tools/feed-publisher/Program.cs` | 临时 CNG 密钥及签名自检 |
| 运行日志 | `src/Journal.cs`；`Journal` | JSONL、排重、滚动、并发及写入失败测试 |
| 发布与校验 | `scripts/publish.ps1`、`scripts/verify.ps1` | 版本检查、行为及引导测试、文档 Gate |

修复只重建五个方法体：

- `ModModpack.ModpackInstall` 两个重载。
- `PageDownloadInstall.LoaderInit`。
- `PageDownloadModify.LoadInstanceFinished`。
- `PageDownloadResourceDetail.InitializeStackResourceVersions`。

此外，仅修改 `ModSecret` 静态初始化中的一个 `UpdateMark` 字符串。配方保存该初始化方法前后指纹，不复制它的其他常量。Mono.Cecil 写出后，工具重新读取 EXE，校验五个下载方法及更新标记，验证其他全部方法体与嵌入资源保持等价。写入前创建按 SHA-256 寻址的备份，校验备份后用同文件系统的原子替换提交。辅助程序不会强制终止现有 PCL；外部引导只让它自己所在的启动进程在进入界面前退出。

`.pcl-repair/backups/<原始SHA256>.exe` 保存原文件；`.pcl-repair/<修复后SHA256>.json` 是还原凭据；目录内锁文件协调并发操作。操作目标不能位于 `Backups/`、`.pcl-repair/` 或符号链接路径中。

`<PCL.exe>.config` 保存局部 CLR 入口，`PCL.Repair.Bootstrap.dll` 是 AnyCPU 引导组件，`<PCL.exe>.pcl-repair.json` 记录已验证 EXE 哈希及配置备份基线。原配置按哈希保存在 `.pcl-repair/config-backups/`。多个同时启动的更新实例共用 `auto.lock`，已提交的更新不会再次重启。原启动参数仅在进程间传递，不写入日志或状态文件。

`logs/full.jsonl`、`logs/error.jsonl` 和 `logs/error-dedup-state.json` 位于辅助程序旁。日志记录固定事件、异常类型及哈希，不保存账号、令牌或用户路径。超过 5 MiB 滚动压缩，每类保留三份，单文件上限 6 MiB；相同错误十分钟排重，Fatal 等事件不排重。日志写入失败不会阻止修复。

## 构建与维护

需要 .NET 10 SDK、Python 3，以及发布时可访问 NuGet。本工作区已在 `.dotnet/` 安装私有 SDK，没有要求全局安装。

```powershell
.\scripts\publish.ps1
.\scripts\verify.ps1 -Fixture "Work\upstream\latest\Plain Craft Launcher 2.exe"
python scripts/package.py
```

使用系统已安装的 SDK 时，给 PowerShell 脚本添加 `-Dotnet dotnet`。源码包不包含本工作区的私有 SDK、官方测试 EXE 或下载缓存。

`verify.ps1` 的锁定文件测试针对 Windows；Linux 运行验证使用发布后的 CLI。`scripts/check-version.py` 检查程序、程序集、文件和项目版本，发布前还应核对产物版本。测试输入始终复制到新目录。

`verify.ps1` 包含 50 项核心检查、57 项配方更新检查、44 项引导检查和 6 项发布工具自检。引导检查使用实际 CLR 子进程验证参数往返、外部 DLL 加载、框架兼容语义及扩展长度路径。网络异常测试使用模拟 HTTPS 响应，不改变生产 TLS 信任设置；发布后的真实 HTTPS 配方另行下载并验签。

`scripts/test-update.ps1 -Runtime win-arm64`（或 `win-x64`）在全新 `Work/` 子目录调用 PCL 的真实 `--update` 替换流程。它只把自己创建的短命等待进程交给官方更新器，不使用任何现有启动器 PID；结束后留下测试 PCL 窗口供检查。运行此测试前须完成对应架构发布。

测试套件会在隔离输出目录生成 `feed-tests/direct-handoff/`，其中只有合成测试 EXE、签名测试配方和公钥。`scripts/test-feed-handoff.ps1 -FixtureDirectory <该目录> -Runtime win-arm64`（或 `win-x64`）验证真实引导按已签名缓存修复不兼容副本后重启。测试私钥只存在于测试进程内，合成配方不进入公开发布源。

需要重新生成配方时，先审核官方新旧源代码和方法差异，然后运行：

```powershell
.\.dotnet\dotnet.exe run --project tools/MakeRecipe.csproj -c Release -- `
  "旧官方.exe" "新官方.exe" "Work\reviewed-recipe.json" "Work\reviewed-prototype.exe"
```

此命令只能生成供审核的候选配方，不能直接证明未来版本兼容。`scripts/build-framework.ps1` 仅保留早期 .NET Framework 分析生成器的构建入口，正式工具统一使用 .NET 10。

完整更新标记配方使用 `tools/update-recipe/UpdateRecipe.csproj`。运行参数为 `<已审查官方EXE> <新配方JSON>`；`--verify <已审查官方EXE>` 验证标记读写范围及转换结果。生成器限定已审查发行哈希，不导出其他静态初始化常量。

功能、接口索引在 `Docs/Indexes/`、`Docs/Interfaces/`；维护记录在 `Docs/Maintenance/`；当前验证见 [配方更新报告](Docs/Reports/Codex-RecipeFeed-v1.0.0.2-20261021.md)，早期结果见 [自动更新报告](Docs/Reports/Codex-AutoRepair-v1.0.0.1-20261021.md) 和 [1.0.0.0 报告](Docs/Reports/Codex-Verification-v1.0.0.0-20261021.md)。`Work/` 是下载、临时工具和隔离测试目录；`Artifacts/` 是可重新生成的产物，二者均不入库。

## 来源与许可

PCL 提供源码并使用自定义许可；本项目保留 `licenses/PCL-LICENCE.txt`，不将其声明为 MIT 或其他标准开源许可。修复配方源自 PCL，源码随本工具交付。

第三方组件：Mono.Cecil 0.11.6、.NET 10 运行时、Microsoft .NET Framework 4.8 引用程序集元数据。许可和通知位于 `licenses/`。引用程序集只用于读取和写出元数据，不用于执行 PCL 代码。官方版本信息见 [PCL 2.99.0.0 发布页](https://github.com/Meloong-Git/PCL/releases/tag/2.99.0.0)。
