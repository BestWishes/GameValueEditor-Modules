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

## Host API 8（主程序 v0.5.1 起）

公共契约位于 `sdk/GameValueEditor.ModuleSdk`。模块 ZIP 只包含模块 DLL 和 `module.json`，不能携带第二份 SDK 或宿主程序集。

- `IGameAdapter`：游戏身份、构建支持、编辑器描述和语义字段读写。
- `IInventoryGameAdapter`：标准集合/背包能力。
- `ICharacterAttributesGameAdapter`：标准人物属性能力。
- `IEntityEditorsGameAdapter`：装备、资源、进度等实体数字字段能力。
- `IGameEditorPageFactoryProvider`：API 6 起必需，按稳定编辑器 ID 创建模块自己的 `IGameEditorPage`。
- `ICoordinatedGameEditorPageProvider`：API 8 必需，承诺所有页面字段写入使用宿主协调桥接，加载器核验此标记。
- `IGameEditorFieldOperations`：通过 `context.Host` 获取的新增能力，页面调用 `WriteFieldAsync(稳定字段键, 目标显示值)`，不能直接调用适配器写入绕过保存别名和锁定协调。页面读取/布局仍由模块负责，模块的构建、范围、线程、刷新、保存与真实回读规则不变。
- `IGameEditorSnapshotOperations`：API 8 同时提供 `ReadSnapshotAsync<T>(读取回调, 应用回调)`；读取回调在后台执行，应用回调在原 Dispatcher 上仅同步更新页面数据/选择/完成提示，不等待、不访问游戏、不保存宿主资料。宿主按运行范围和模块写入修订原子核验并应用，相关写入重叠时抛出 `GameEditorSnapshotChangedException`；提示重新刷新、不自动重试、不把迟到错误当成游戏错误。缺少此能力的新源码页面明确要求更新主程序，不回退为无协调读取。
- `IGameEditorHostServices`：宿主提供的通用值输入、错误提示、状态和保存语义字段流程。
- `ModuleVisualResources`：API 7 提供宿主主题资源键与间距常量。
- `IGameCompatibilityDiagnosticsProvider`：API 5 必需，返回游戏专属的只读兼容检查结果。
- `IGameEditorFieldPolicyProvider`：可选，为混合页面声明单字段生命周期和锁定能力。
- `IGameVersionMetadataProvider`：可选，只提供游戏自报版本，不能代替构建验证。

模块决定页面清单、稳定 ID、名称、顺序、说明、完整 WPF 视觉树、页面 ViewModel、筛选和操作流程。可以使用模块内 XAML、`UserControl` 或纯代码页面，也可以选择编译进本模块程序集的共享源码控件；这些都不构成宿主模板。宿主只管理导航容器、主题、生命周期、通用弹框、保存字段和兼容性报告，不读取页面内部控件或按 ID/类型猜布局。官方 API 7 模块必须使用 `ModuleVisualResources` 与宿主控件样式，不能写死颜色、替换应用级资源字典、建立独立字体体系或创建自己的窗口/主题切换。

页面应在首次 `Loaded` 时读取数据，在 `Dispose()` 中解绑事件和释放状态，并在每次异步返回后检查 `GameEditorPageContext.Lifetime`。API 6 及以上 WPF 模块从影子副本加载；WPF 内部类型缓存使加载上下文保留到进程退出，但安装源包必须始终可删除，旧影子副本由下次启动清理。更新或回退这类已加载模块后，宿主只停用目标模块并等待用户重启，禁止在同一进程强行加载新旧两个版本。

宿主桥接绑定页面的原进程、构建、适配器和生命周期，并与保存字段/快捷入口共用按实际语义目标的 FIFO 队列，后台锁定只使用空闲目标。桥接成功后同步保存别名当前值及未被显式改变的锁定目标；浏览历史版本不会把结果写进历史资料。旧宿主没有 API 8 能力时新模块拒绝加载，页面也不能回退为直接写入。旧 API 6/7 页面包在新宿主仍可加载，但字段锁定明确暂停，不声称旧包已经具备新能力。已经进入的同步写入不能靠取消 Token 撤销。

API 8 的首个正式宿主为 v0.5.1，两仓 SDK 源码版本 4.0.3，CLR AssemblyVersion 保持 2.0.0.0。本次 WorldApart 1.3.4、Fzzml 2.1.4、Last Epoch 0.5.5 清单均明确要求 `hostApiVersion: 8` 与 `minimumHostVersion: 0.5.1`，两项门槛同时检查。发行号独立，稳定 ID 与精确游戏指纹不变；资产核验后才更新目录，旧 API 7 版本快照保持原声明。源码检查可用 `-SkipCatalog`，不据此放行正式发布。回退到旧宿主前必须由用户手动回退所需 API 8 模块，不自动回退。

共享背包/人物/实体页与 Last Epoch 资源页均接入整页快照，布局和资源分类不变。快照只识别宿主协调的模块写入（含真正写入的后台锁定），不声称阻止游戏自然变化、未知原生地址写入或旧 API 6/7 包的直接读取。旧不可变包仍可加载，能力和锁定降级边界不变。

兼容性诊断只能读取进程、构建和入口状态，不能写内存、调用游戏刷新/保存，也不能返回本机路径、用户名、PID、内存地址或存档内容。每个结果应给出稳定检查名、`Information`/`Passed`/`Warning`/`Failed` 状态和可直接给维护者阅读的结论；宿主会再次脱敏并隔离提供器异常。

自有页面注册只能报告 Information。每个能力应单独说明当前构建的支持条件，不支持的人物页不能随支持的背包页一起 Passed；诊断不得为验证而执行页面读写。未知固定布局不能靠短函数序言放行，实际入口必须先验证完整运行文件组合与进程启动实例。

## 清单与 Schema

`module.json` 是模块版本、程序集、进程、兼容构建和编辑器元数据的唯一人工维护来源。项目的 `AssemblyName`、`ModuleVersion` 必须与清单一致。每个版本必须明确填写 `hostApiVersion`、`minimumHostVersion` 和可选的 `maximumHostVersion`；模块编号与主程序编号独立，兼容关系只能由这些字段确定。

`contributors.generated.json` 由维护者在合并 PR 后运行 `scripts/update-contributors.ps1` 生成，不接受贡献者手工修改。`catalog.json` 只在不可变 Release 资产验证完成后更新。

`scripts/validate-modules.ps1` 会执行三个 JSON Schema，并核对：

- 模块、编辑器 ID 唯一且格式正确。
- 项目程序集名和默认版本与 `module.json` 一致。
- 三文件 SHA-256 不是占位值。
- 贡献者身份和日期有效。
- 公开目录的所有重复元数据、贡献者、URL 和源码清单完全一致。

宿主加载 DLL 时还会核对程序集与清单的模块显示名，以及编辑器 ID、名称、顺序和 `SessionOnly`。Host API 6 起 `editors` 不含 `kind`，程序集中的兼容描述统一使用 `GameEditorKind.Custom`；页面工厂必须能为每个编辑器创建非空页面。API 5 起仍必须提供只读兼容性诊断，API 7 起还会执行官方模块视觉规则检查。

无真实游戏的页面回归：`dotnet run --project tests/GameValueEditor.Modules.PageTests -c Release`，实际实例化编译模块中的背包/人物/实体/Last Epoch 资源页面，用模拟适配器和宿主检验正常写入、输入取消、失效、错误及缺少桥接能力；模拟适配器的直接写入必须保持零次。该验证不等于游戏内实际效果重新验收。

## 构建与支持边界

首选 EXE、`GameAssembly.dll` 和 `global-metadata.dat` 三文件精确指纹。只有完整覆盖实际使用的根对象、字段、方法、业务写入、刷新/保存和版本入口后，才允许对未登记的小版本使用语义解析或结构签名回退。

能对未知构建安全拒绝并给出只读诊断的模块可以在 `module.json` 声明 `supportsUnlistedBuildValidation: true`。该可选字段属于目录 Schema 5 的向后兼容扩展，目录顶层版本与对应 release 快照必须同步，包内清单也必须一致。声明该字段要求 Host API 5 以上、只读兼容诊断以及安全失败的 `Supports`；它只允许主程序在没有精确构建候选时下载最新版本，不把未知构建变成已支持构建。首次安装可在当前离线进程验证，替换已加载的 Host API 6 以上模块必须重启后验证。尚有未经验证的固定布局时必须拒绝未知构建；符号存在不是布局或行为兼容证明。未知构建不得提供猜测回退。

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
- 对 Host API 6 及以上模块验证页面来自模块程序集、安装源包无文件锁，并验证兼容性诊断保持只读且不包含本机敏感运行期信息。
- 对 Host API 7 官方模块检查硬编码颜色、应用级资源字典、自定义窗口和独立主题定义。

需要实机验证时显式指定游戏，例如：

```powershell
dotnet run --project tests/GameValueEditor.Modules.LiveTests/GameValueEditor.Modules.LiveTests.csproj -- --game=worldapart
```

实机测试只能执行已审核的同值或可恢复写入。任何改变都必须恢复原值并再次读取。

## 发布

任何模块 DLL 或包内清单字节变化都必须提升模块版本；已经发布的资产不得覆盖。每个游戏模块独立从自己的最新正式标签加 `0.0.1`，`0.4.9 -> 0.5.0`、`0.9.9 -> 1.0.0`；模块版本与主程序版本、其他模块版本无关。Host API 和 Schema 是整数协议号，不使用此进位规则。

```powershell
./scripts/Publish-GameModule.ps1 -Game example -SkipCatalog
```

标准顺序：

1. 同步提升 `module.json` 与项目默认版本，并确认 Host API 与最低/最高主程序版本。
2. 完成 Schema、全模块构建和当前宿主真实包加载验证。
3. 生成 ZIP，检查其恰好包含 DLL 和 `module.json`，记录 SHA-256。
4. 提交源码并保持工作树干净，从该提交重新生成最终 ZIP；检查 DLL `ProductVersion` 的提交号等于 `HEAD`。
5. 创建指向同一提交的带注释标签和不可变 Release，上传并复核远端资产。
6. Release 资产远端复核通过后，使用 `-CatalogOnly` 从已验证 ZIP 更新 `catalog.json`，自动保留该模块最多 3 个完整版本快照，再执行 `./scripts/verify-release.ps1 -VerifyReleasedVersions` 并单独提交目录；不得重新打包后换一个哈希。目录公开验证后才可清理该模块第 4 个及更旧的 Release/资产，Git 标签必须保留。该复验开关只允许已有同名正式标签，不放宽新版本的精确下一版校验。

宿主分别提供模块“更新”和“回退到 vX”入口；两者都验证主程序范围、Host API 与游戏构建。回退必须由用户确认，目录检查、安装失败或主程序版本变化都不得触发自动回退。

仓库不依赖 GitHub Actions。通过本地脚本不等于自动接受模块；维护者仍需人工审核离线单机边界、真实对象、线程、保存和实机证据。
