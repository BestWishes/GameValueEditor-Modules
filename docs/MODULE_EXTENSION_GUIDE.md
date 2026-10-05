# 游戏专属模块扩展规范

## 身份与目录

游戏专属模块代表一个游戏，稳定 ID 形如 `game.example`；游戏内编辑器代表一项能力，稳定 ID 形如 `game.example.inventory`。显示名称可以修改，稳定 ID 发布后不得改变。

每个游戏只修改自己的 `games/{短名}` 目录：

```text
games/{短名}/
  GameValueEditor.Modules.{Name}.csproj
  module.json
  contributors.generated.json
  src/
  tests/       # 可选实机测试，由通用运行器自动发现
  docs/
```

新增游戏不修改主程序、中央解决方案或中央发布脚本。建议使用 `scripts/new-game-module.ps1` 创建安全失败的初始目录。

## Host API 4

公共契约位于 `sdk/GameValueEditor.ModuleSdk`。模块 ZIP 只包含模块 DLL 和 `module.json`，不能携带第二份 SDK 或宿主程序集。

- `IGameAdapter`：游戏身份、构建支持、编辑器描述和语义字段读写。
- `IInventoryGameAdapter`：标准集合/背包能力。
- `ICharacterAttributesGameAdapter`：标准人物属性能力。
- `IEntityEditorsGameAdapter`：装备、资源、进度等实体数字字段能力。
- `IGameEditorPageProvider`：API 4 必需，显式把每个编辑器绑定到 `Inventory`、`CharacterAttributes` 或 `Entity` 页面角色。
- `IGameEditorFieldPolicyProvider`：可选，为混合页面声明单字段生命周期和锁定能力。
- `IGameVersionMetadataProvider`：可选，只提供游戏自报版本，不能代替构建验证。

模块决定页面清单、稳定 ID、名称、顺序、说明、空状态和操作能力；宿主统一管理主题、布局、弹框、忙碌状态和标准控件。不要把任意 WPF 页面放入模块。两个以上游戏出现相同新交互形态后，再讨论增加宿主标准页面角色。

## 清单与 Schema

`module.json` 是模块版本、程序集、进程、兼容构建和编辑器元数据的唯一人工维护来源。项目的 `AssemblyName`、`ModuleVersion` 必须与清单一致。

`contributors.generated.json` 由维护者在合并 PR 后运行 `scripts/update-contributors.ps1` 生成，不接受贡献者手工修改。`catalog.json` 只在不可变 Release 资产验证完成后更新。

`scripts/validate-modules.ps1` 会执行三个 JSON Schema，并核对：

- 模块、编辑器 ID 唯一且格式正确。
- 项目程序集名和默认版本与 `module.json` 一致。
- 三文件 SHA-256 不是占位值。
- 贡献者身份和日期有效。
- 公开目录的所有重复元数据、贡献者、URL 和源码清单完全一致。

宿主加载 DLL 时还会核对程序集与清单的模块显示名，以及编辑器 ID、名称、类型、顺序和 `SessionOnly`；API 4 页面注册必须与编辑器集合一一对应。

## 构建与支持边界

首选 EXE、`GameAssembly.dll` 和 `global-metadata.dat` 三文件精确指纹。只有完整覆盖实际使用的根对象、字段、方法、业务写入、刷新/保存和版本入口后，才允许对未登记的小版本使用语义解析或结构签名回退。

窗口标题、显示版本、文件时间、相似序言和单个 getter 都不能代替构建验证。任一语义成员缺失、类型异常、地址越界或线程不正确都必须拒绝启用。

某个编辑器只支持部分构建时，在自己的能力判断中安全失败，不得拖垮同包其他页面。

## 数据、线程和生命周期

实体、字段和快捷入口必须使用稳定语义键。每次操作重新定位实时对象，不保存堆指针或跨进程地址。

写入流程必须完整覆盖：

1. 验证进程、构建和当前对象身份。
2. 核对旧值、范围和目标状态。
3. 在游戏要求的线程执行。
4. 调用必要的缓存、事件、派生值和保存路径。
5. 从游戏权威对象或最终消费者回读。

持久化字段必须经过游戏重启验证。`SessionOnly` 字段不得锁定或在连接、重启后自动重应用。人物面板、技能消费、经验结算、掉落实物或其他最终结果才是有效证据；模块自己的写入回读不是完成证据。

Unity/IL2CPP 主线程挂接必须保存并恢复原始字节和页面保护，使用完成状态与硬超时。目标线程可能仍在执行时，宁可保留少量远程内存，也不能提前释放造成崩溃。

## 自动验证

```powershell
./scripts/validate-modules.ps1 -SkipCatalog
./scripts/verify-release.ps1 -SkipCatalog
```

第二条命令会：

- 自动发现并构建全部 `games/*` 模块。
- 生成只含 DLL 与 `module.json` 的真实 ZIP。
- 构建模块目录中的实机测试代码，但不自动连接或写入游戏。
- 核对模块仓库与当前宿主 SDK 源码一致。
- 使用当前宿主实际加载每个 ZIP，验证 ABI、清单、页面注册和贡献者。

需要实机验证时显式指定游戏，例如：

```powershell
dotnet run --project tests/GameValueEditor.Modules.LiveTests/GameValueEditor.Modules.LiveTests.csproj -- --game=worldapart
```

实机测试只能执行已审核的同值或可恢复写入。任何改变都必须恢复原值并再次读取。

## 发布

任何模块 DLL 或包内清单字节变化都必须提升模块版本；已经发布的资产不得覆盖。

```powershell
./scripts/Publish-GameModule.ps1 -Game example -SkipCatalog
```

标准顺序：

1. 同步提升 `module.json` 与项目默认版本。
2. 完成 Schema、全模块构建和当前宿主真实包加载验证。
3. 生成 ZIP，检查其恰好包含 DLL 和 `module.json`，记录 SHA-256。
4. 提交源码，创建不可变标签和 Release，上传并复核远端资产。
5. 最后更新 `catalog.json`，执行不带 `-SkipCatalog` 的完整验证并单独提交。

仓库不依赖 GitHub Actions。通过本地脚本不等于自动接受模块；维护者仍需人工审核离线单机边界、真实对象、线程、保存和实机证据。
