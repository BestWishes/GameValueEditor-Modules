# 放置斩魔录模块实现说明

稳定游戏 ID：`game.fzzml`。本次版本 `2.1.5`，使用 Host API 8，最低主程序 `0.5.1`。提交、正式资产与目录验证状态见[发布记录](../../../docs/RELEASE_FZZML_2_1_5_WORLDAPART_1_3_5.md)。

背包和人物页面由模块创建，使用宿主主题与公共协调接口；主程序不包含本游戏的类型、字段、地址或保存规则。

## 小更新识别与定位

游戏身份按“放置斩魔录 / fzzml”名称识别，不以历史 EXE、GameAssembly 或 metadata 哈希禁用功能。`compatibleBuilds` 是历史记录，`supportsUnlistedBuildValidation` 声明当前运行元数据解析能力。

模块从实际加载的 GameAssembly 导出查询当前程序集、完整类型名、字段类型/偏移以及完整方法签名。程序集拆分可按唯一完整类型名重新定位；不沿用旧 RVA、旧快照偏移或旧主线程序言。仅缓存当前进程实例的元数据（PID、启动时间、加载模块路径/地址），不缓存人物、背包对象或跨启动地址。

只读数据不依赖保存方法。写入时才解析所需原生刷新和保存调用；字段或调用签名真的改变时报告具体缺失项，不把它说成整个游戏名称不匹配。

## 背包物品

编辑器 ID：`game.fzzml.inventory`。原语义键保持物品名，不改变已有快捷入口。

从 `SaveManager._cachedSnapshot -> AllParsedData.inventoryRows` 读取 `List<List<string>>`，按物品名聚合数量。所有字段由当前元数据定位。修改在 Unity 主线程创建新数量字符串并使用写屏障替换匹配元素，不更改共享字符串内容；随后重建缓存、调用原生保存流程并回读。超过 9999 的数值仍交由游戏后台处理，不另造拆栈规则。

操作进入主线程前后核对当前快照、背包和目标数量字符串引用，避免存档切换后写旧对象。超时不自动重试；可能仍在执行的跳板内存保留到游戏退出。

## 人物属性

编辑器 ID：`game.fzzml.character-attributes`，`SessionOnly = true`。

从 `playerDefault.units` 枚举人物，以 `UnitSlotData.unitId` 为身份；保留根骨、力道、神识、身法、体魄五项。字段和 `PlayerUnitConfig`、聚合器、事件调用均解析当前元数据。

写入以 `newBase = oldBase + targetRaw - currentRaw` 保留等级、成长和境界部分，Unity 主线程执行 `PlayerAttributeAggregator.Compute` 与 `PlayerAttributeEventHub.RaiseAttributesChanged`。仍只影响本次运行，不写人物存档；重启恢复，不锁定、不自动重应用。

## 本地验证

2026-10-09 更新后的游戏读取成功：10 个人物及背包物品；当前快照偏移已由旧 `0x1A8` 自动解析为 `0x1B0`。背包类型数随正常游玩变化，不作为固定数量断言。已定位背包保存/刷新、人物配置/聚合/事件完整签名及五项字段；错误类型和错误重载被拒绝，没有写入玩家数值或调用保存。

回归入口（默认只做本测试进程的合成函数测试；两项参数仅读取已运行游戏）：

```powershell
dotnet run --project tests/GameValueEditor.Modules.CompatibilityTests -c Release -- --read-only-fzzml --read-only-worldapart
```

完整设计、三遍检查与离线包证据见 [本地修复记录](../../../docs/SMALL_UPDATE_TWO_GAMES_LOCAL.md)。当前读出/方法定位不能替代实际修改及重启持久化测试。

历史实机证据：10 人、每人 5 项，同值主线程写入；玄道力道从界面 506 调到 1036，重启恢复 506。这是先前验证，不是本次新写入证据。原 `tests/FzzmlLiveTest.cs` 会调用写入，不属于上述只读检查。
