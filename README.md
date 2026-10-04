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

`game.worldapart` v1.2.0：

- 背包物品与人物属性：每次重新定位当前存档对象，在 Unity 主线程修改，调用游戏自动存档并回读。
- 同时支持两个已验证构建；第三方发行版仅替换启动 EXE 时，以一致的 `GameAssembly.dll` 与 metadata 识别原布局，其他未知构建继续使用完整关键函数签名验证并安全失败。
- 向宿主提供游戏自报版本、产品名和构建 GUID，和 Steam Build ID、引擎文件版本分开显示。

`game.last-epoch` v0.4.1：

- 只支持 Last Epoch 完全离线模式。已登记精确构建直接识别；后续小版本会按 IL2CPP 类名、字段名和方法签名重新定位，并且只有完整语义结构校验通过才启用，结构不兼容时安全拒绝。
- 人物属性提供剩余天赋点、剩余技能点、五维、移动速度、效果范围、两种冷却恢复、经验倍率、总物品掉落率、金币倍率及药剂掉落率；点数走存档权威链，运行期属性在游戏主线程进入真实消费或最终结算路径验证，不以模块自己的写入回读冒充生效。
- 装备编辑只读取熔炉主槽当前放入的一件装备，可修改锻造潜能（含潜能类型对应名称）、编织者意志、编织者之触和传奇潜能；更换熔炉装备后必须刷新，旧实体键不会误写新装备。
- 资源枚举全部词缀碎片、符文、雕文和副本钥匙，使用游戏中文本地化；已有实时槽位会同步数量，未持有的固定资源也能从 0 创建。异界页只修改已有进度，不创建或解锁内容。
- 世界功能只提供已验证安全的摄像头视野角度；近战范围、地图迷雾、暗金/套装等分类掉率和金币堆频率均已移出，没有稳定权威入口的功能不会保留占位项。

## 构建

要求 Windows x64 与 .NET 8 SDK。

```powershell
dotnet build GameValueEditor.Modules.slnx -c Release
./scripts/validate-modules.ps1 -SkipCatalog
./scripts/publish-fzzml.ps1 -Version 2.0.1
./scripts/publish-last-epoch.ps1 -Version 0.4.1
./scripts/publish-worldapart.ps1 -Version 1.2.0
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
