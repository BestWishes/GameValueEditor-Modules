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
    src/
    docs/
sdk/
  GameValueEditor.ModuleSdk/
schemas/
catalog.json
```

## 当前模块

`game.fzzml` v2.0.0：

- 背包物品：实时读取、筛选和修改物品总数，并调用游戏自身保存流程。
- 人物属性：列出人物及五维属性，修改后立即刷新游戏界面；该实验功能仅本次游戏运行有效，关闭游戏后失效，不锁定、不自动重应用。

## 构建

要求 Windows x64 与 .NET 8 SDK。

```powershell
dotnet build GameValueEditor.Modules.slnx -c Release
./scripts/publish-fzzml.ps1 -Version 2.0.0
```

发布脚本生成 `dist/GameValueEditor.Module.Fzzml-v2.0.0.zip`，并把真实 SHA-256 写入 `catalog.json`。

新增游戏或编辑能力前请阅读 [模块扩展规范](docs/MODULE_EXTENSION_GUIDE.md) 和 [fzzml 实现说明](games/fzzml/docs/README.md)。

## 安全范围

本仓库仅面向离线单机游戏、个人研究、调试和可访问性用途。不接受在线游戏、反反作弊、驱动绕过或隐藏修改行为。模块是可执行代码；安装前必须经过 HTTPS 下载、SHA-256 校验、清单核对和安全解压。

## License

[MIT](LICENSE)
