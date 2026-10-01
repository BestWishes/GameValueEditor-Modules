# 肝肾大圣游戏专属模块中心

本仓库是 [肝肾大圣-单机游戏数值编辑器](https://github.com/BestWishes/GameValueEditor) 的独立游戏模块中心。主程序只负责宿主、统一界面、下载校验和通用扫描；本仓库按游戏保存专属模块源码、构建清单、文档和发行包。

## 两层结构

- 游戏专属模块代表整个游戏，例如 `game.fzzml`。
- 游戏内编辑模块代表该游戏中的一种编辑能力，例如 `game.fzzml.inventory` 和 `game.fzzml.character-attributes`。
- 一个游戏使用一个可下载 ZIP/DLL，包内可注册多个编辑模块。
- Release 只承载不可变的 ZIP；源码、说明和 `catalog.json` 保留在仓库目录中。

```text
games/
  fzzml/
    module.json
    contributors.generated.json
    src/
    docs/
sdk/
  GameValueEditor.ModuleSdk/
schemas/
catalog.json
```

## 当前模块

`game.fzzml` v2.0.1：

- 背包物品：实时读取、筛选和修改物品总数，并调用游戏自身保存流程。
- 人物属性：列出人物及五维属性，修改后立即刷新游戏界面；该实验功能仅本次游戏运行有效，关闭游戏后失效，不锁定、不自动重应用。

`game.worldapart` v1.1.0：

- 背包物品与人物属性：每次重新定位当前存档对象，在 Unity 主线程修改，调用游戏自动存档并回读。
- 同时支持两个已验证构建；新版布局对后续小版本使用完整关键函数签名验证，未知布局安全失败。
- 向宿主提供游戏自报版本、产品名和构建 GUID，和 Steam Build ID、引擎文件版本分开显示。

## 构建

要求 Windows x64 与 .NET 8 SDK。

```powershell
dotnet build GameValueEditor.Modules.slnx -c Release
./scripts/validate-modules.ps1 -SkipCatalog
./scripts/publish-fzzml.ps1 -Version 2.0.1
./scripts/publish-worldapart.ps1 -Version 1.1.0
```

发布脚本从各游戏的 `module.json` 生成包内清单，将自动维护的贡献者信息注入包中，生成 ZIP，并把同一份清单与真实 SHA-256 同步到 `catalog.json`。`module.json` 是兼容构建、编辑器列表和游戏身份的唯一人工维护来源。

## 贡献者与审核

- 贡献者按整个游戏专属模块展示，不按“背包物品”“人物属性”等编辑模块拆分，也不显示贡献数量或排名。
- 合并 PR 后，GitHub Actions 根据该 PR 实际修改过的 `games/{短名}` 自动写入 GitHub 显示名称、主页、首次和最新贡献日期；贡献者不直接编辑 `contributors.generated.json`。
- PR 会自动校验稳定 ID、精确构建指纹、清单/目录一致性并构建所有模块。模块含可执行代码，因此自动检查通过后仍需维护者做安全边界和最小实机读写复核。

新增游戏或编辑能力前请阅读 [模块扩展规范](docs/MODULE_EXTENSION_GUIDE.md)、[参与维护](CONTRIBUTING.md) 和现有游戏实现说明。

## 安全范围

本仓库仅面向离线单机游戏、个人研究、调试和可访问性用途。不接受在线游戏、反反作弊、驱动绕过或隐藏修改行为。模块是可执行代码；安装前必须经过 HTTPS 下载、SHA-256 校验、清单核对和安全解压。

## License

[MIT](LICENSE)
