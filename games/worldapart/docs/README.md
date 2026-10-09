# 不问凡尘专属修改模块

稳定游戏 ID：`game.worldapart`，进程名 `WorldApart`。本次版本 `1.3.5`，使用 Host API 8，最低主程序 `0.5.1`。它不是放置斩魔录，也不是万里仙途。提交、正式资产与目录验证状态见[发布记录](../../../docs/RELEASE_FZZML_2_1_5_WORLDAPART_1_3_5.md)。

页面由模块创建并沿用宿主主题和公共协调接口。主程序不包含本游戏的字段、保存流程或构建表。

## 游戏内编辑模块

- `game.worldapart.inventory`（背包物品）：按稳定物品配置 ID 定位当前背包，读取、修改总数。
- `game.worldapart.character-attributes`（人物属性）：玩家资源、基础属性、探索属性、五行灵根和战斗属性，类别、语义键不变。

精力、精力上限、灵力、灵力上限仍属于“基础属性”，当前值与上限相邻。灵力不等于丹田灵气。`基础属性 · 修为` 对应 `CombatModel.CultivateExp`；`资源 · 灵气` 对应 `CultivateReserveExp`。

快捷入口只保存语义键；每次从 `GameStoreManager.CurrentPlayer` 重新读取存档对象。修改在 Unity 主线程执行原生刷新/属性设置与自动存档，再重新定位回读。五行灵根新增使用当前字典实例的 `Dictionary<int,int>.set_Item` 和 MethodInfo，扩容交由 IL2CPP 处理。

## 小更新与安全边界

名称“WorldApart / 不问凡尘”决定游戏身份，不再由历史三文件哈希限制整个模块。`compatibleBuilds` 只保留历史记录，`supportsUnlistedBuildValidation` 声明当前元数据定位能力。

当前 GameAssembly 导出、完整类型名、字段名称/类型、完整方法签名决定实际数据与调用位置。程序集拆分可以按唯一完整类型名重新定位；不套用旧 RVA、列表/字典字段偏移或固定泛型地址。继承字段、字典元素类型和步长均来自实际运行类型。

绑定 PID、进程启动时间及真实加载的 GameAssembly 路径/地址；元数据缓存只用于这个进程实例，不缓存玩家对象。安装目录、旧哈希和 Steam 不是适配前提。一个启动入口可能有多个同名进程，数据读取使用真正加载 IL2CPP 的运行进程，不把启动器当存档进程。

保存/刷新方法只在写入时解析，游戏自报版本等可选显示信息缺失不禁用背包。主线程操作先确认当前玩家和可用存档，再检查待写入原值；不对旧存档对象或已变化字段继续写。超时不自动重复发送，未确认结束的跳板内存保留到游戏退出。

## 本地验证

2026-10-09 从正常启动并进入存档的游戏读取到 143 类背包物品、39 项人物属性。当前保存、背包排序/刷新、探索属性、战斗基础属性、灵根缓存及 Unity 主线程入口完整签名已定位；没有调用写入或存档。

```powershell
dotnet run --project tests/GameValueEditor.Modules.CompatibilityTests -c Release -- --read-only-worldapart
```

完整设计、三遍检查及离线包见 [本地修复记录](../../../docs/SMALL_UPDATE_TWO_GAMES_LOCAL.md)。只读成功不等于已经验证本更新版所有修改和持久化效果；交付包用于后续用户测试。

历史 `tests/WorldApartLiveTest.cs` 会执行写入，不属于此只读检查。更新若真的改变字段语义或删除原生能力，报告具体项目，不以“未登记版本”拒绝整个游戏。
