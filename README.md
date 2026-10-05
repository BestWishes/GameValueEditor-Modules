# 肝肾大圣游戏专属模块中心

本仓库是 [肝肾大圣-单机游戏数值编辑器](https://github.com/BestWishes/GameValueEditor) 的独立游戏模块中心。主程序只负责宿主、统一界面、下载校验和通用扫描；本仓库按游戏保存专属模块源码、构建清单、文档和发行包。

## 两层结构

- 游戏专属模块代表整个游戏，例如 `game.fzzml`。
- 游戏内编辑模块代表该游戏中的一种编辑能力，例如 `game.fzzml.inventory` 和 `game.fzzml.character-attributes`。
- 一个游戏使用一个可下载 ZIP/DLL，包内可注册多个编辑模块。
- Release 只承载不可变的 ZIP；源码、说明和 `catalog.json` 保留在仓库目录中。

```text
games/
  {游戏}/
    module.json
    contributors.generated.json
    src/
    tests/
    docs/
sdk/
  GameValueEditor.ModuleSdk/
schemas/
templates/
tests/
catalog.json
```

## 当前模块

- [`game.fzzml` v2.1.0](games/fzzml/docs/README.md)
- [`game.worldapart` v1.3.0](games/worldapart/docs/README.md)
- [`game.last-epoch` v0.5.0](games/last-epoch/docs/README.md)，分析证据见其 [ANALYSIS.md](games/last-epoch/docs/ANALYSIS.md)

具体功能、线程、保存链路和实机证据只维护在对应游戏目录，不复制到主程序架构文档。

## 构建

要求 Windows x64 与 .NET 8 SDK。

```powershell
./scripts/validate-modules.ps1 -SkipCatalog
./scripts/verify-release.ps1 -SkipCatalog
./scripts/Publish-GameModule.ps1 -Game fzzml -SkipCatalog
```

验证和发布脚本自动发现 `games/*`，新增游戏不修改中央解决方案或创建专属发布脚本。发布脚本从 `module.json` 推导项目、程序集、版本、资产名和 Release 标签；`module.json` 是游戏身份和兼容范围的唯一人工维护来源。

`verify-release.ps1` 要求相邻目录存在当前主程序仓库，构建每个真实模块 ZIP，并由当前宿主实际加载和核对页面契约。游戏运行时验证仍在对应模块目录执行，例如：

```powershell
dotnet run --project tests/GameValueEditor.Modules.LiveTests/GameValueEditor.Modules.LiveTests.csproj -- --game=fzzml
```

新增游戏使用 `scripts/new-game-module.ps1`。脚手架要求真实三文件 SHA-256，生成的适配器默认拒绝所有构建，不会把占位实现误当成可用模块。

仓库不依赖 GitHub Actions。正式发布先使用 `-SkipCatalog` 生成并验证 ZIP，通过 GitHub API 创建不可变 Release；线上资产校验成功后再更新 `catalog.json` 并执行不带 `-SkipCatalog` 的完整目录验证。

## 贡献者与审核

- 贡献者按整个游戏专属模块展示，不按“背包物品”“人物属性”等编辑模块拆分，也不显示贡献数量或排名。
- 合并 PR 后，由维护者运行 `scripts/update-contributors.ps1`，根据该 PR 实际修改过的 `games/{短名}` 写入 GitHub 显示名称、主页、首次和最新贡献日期；贡献者不直接编辑 `contributors.generated.json`。
- 维护者必须运行 `scripts/verify-release.ps1` 校验稳定 ID、精确构建指纹、清单/目录一致性并构建所有模块。模块含可执行代码，因此脚本通过后仍需做安全边界和最小实机读写复核。

新增游戏或编辑能力前请阅读 [模块扩展规范](docs/MODULE_EXTENSION_GUIDE.md)、[参与维护](CONTRIBUTING.md) 和现有游戏实现说明。

## 安全范围

本仓库仅面向离线单机游戏、个人研究、调试和可访问性用途。不接受在线游戏、反反作弊、驱动绕过或隐藏修改行为。模块是可执行代码；安装前必须经过 HTTPS 下载、SHA-256 校验、清单核对和安全解压。

## License

[MIT](LICENSE)
