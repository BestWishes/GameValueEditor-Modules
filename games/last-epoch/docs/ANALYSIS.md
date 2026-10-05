# Last Epoch 专属模块分析记录

本文记录 `game.last-epoch` 的可复用分析结论，避免后续版本从显示数值和临时地址重新摸索。当前验证基线为 Last Epoch 1.5.0.1、1.5.1 与 1.5.1.1，Unity IL2CPP 元数据版本 39。

## 身份与兼容

- 只接受进程名 `Last Epoch` 且命令行包含 `--offline` 的完全离线进程。
- 清单保存实机验证过的 EXE、`GameAssembly.dll` 与 `global-metadata.dat` 三重指纹用于审计。
- 小版本兼容不直接依赖固定 RVA：运行时从 IL2CPP 导出接口按类名、字段名和方法签名重新解析，并校验关键字段、函数地址和容器边界。任何关键语义缺失时拒绝启用。
- 地址、对象指针、RVA 和列表节点都只在一次进程运行内有效；快捷入口必须保存模块 ID、编辑器 ID、实体语义键和字段键。

## 人物权威对象

人物实时对象链为：

`PlayerFinder.playerActorComponent -> Actor.stats (CharacterStats)`

移动技能冷却恢复速度的派生消费字段位于：

`PlayerFinder.playerActorComponent -> Actor.mutatorManager -> AbilityStatsMutatorManager.increasedCooldownRecoverySpeedForMovementSkills`

它的可重建上游来源不是该字段本身，而是人物 `BaseStats.stats` 中的能力属性记录：`SP 58 + AbilityID 0x30E（Evade）+ specialTag 9 + extraTag 0`。

五维面板终值优先从 `CharacterStats.attributes` 字典中的 `AttributeValuePair.value` 读取。运行期统计可从 `Stats.stats` 的 `Stats.Stat` 列表按 `property + specialTag + tags + extraTag` 语义定位；这些 SP 只代表结构身份，不代表对应写法已经被最终业务逻辑采用：

- 移动速度：SP 9
- 力量、活力、智力、敏捷、协调：SP 19..23
- 通用冷却恢复：SP 70
- 药剂掉落率：SP 47
- 分类物品掉落加成：SP 104（按 `specialTag + tags` 区分装备、非装备、碎片、暗金和金币等类别，不是全局掉落率）
- 经验倍率：SP 105
- 效果范围：SP 116

不能把手工分配的 `Stats.Stat` 直接追加进 `Stats.stats` 后再读取同一列表作为成功证据：该路径会产生“编辑器已变化、人物面板和实际逻辑未变化”的自写自读假成功。公共的安全调用底座是：在实际 `CharacterStats` 运行时类型中按方法指针唯一定位 `OnUpdateTick` 虚表槽，安装一次性主线程入口，在命中当前人物对象后先恢复原虚表，再调用游戏自己的 `CharacterStats.AddStatModifier` 与 `BaseStats.UpdateStats`。具体属性仍必须逐项查到最终消费函数并按该函数读取的 `Stats.Stat` 值类型写入，不能统一假定为 `increasedValue` 或统一用 `Stats.GetTotalIncreased` 验收。

### 通用冷却恢复（SP 70，1.5.1 已完成端到端验证）

- `Ability.GetCooldownWithStats` 与 `AbilityMutator.GetCooldownWithStats` 都以 SP 70 调用 `Stats.GetTotalAddedForAbility`，实际读取 `Stats.Stat.addedValue`（对象偏移 `+0x1C`），再把 `1 + addedValue` 纳入冷却计算。
- 旧实现以 `modificationType = 1` 写入 `increasedValue`（`+0x20`），随后又用 `Stats.GetTotalIncreased` 读取同一列，因此会出现内部回读成功、人物面板和 13 秒技能冷却均不变化的假成功。
- 正确实现以 `modificationType = 0` 写入通用标签的 `addedValue`，再在 Unity 主线程调用 `ChargeManager.RefreshAllChargeInfo`，让游戏重新生成 `chargeRegen`。验收同时检查正确列、`ChargeManager.increasedRecoverySpeed` 与实际技能恢复缓存，不直接篡改缓存。
- 2026-10-03 实机可逆验证：100% 时游荡灵魂 `chargeRegen 0.076923 -> 0.153846`（约 13 秒 -> 6.5 秒），骨魔像 `0.33333334 -> 0.6666667`；恢复到 0% 后回到原值，再写 100% 后再次生效。用户实际施放确认有效，切换地图后仍有效。

### 移动技能冷却恢复（1.5.1 已完成端到端验证）

- `AbilityStatsMutatorManager.UpdateAbilityStats` 每次重建时先把 `increasedCooldownRecoverySpeedForMovementSkills` 清零，再枚举人物 `BaseStats.stats`；其中 `SP 58 + AbilityID 0x30E + specialTag 9` 的 `addedValue` 会累加到该字段。
- 旧实现直接写派生字段并刷新 `ChargeManager`，因此第一次闪避曾短暂从约 1.9 秒变为 1.3 秒；施放触发 `UpdateAbilityStats` 后字段被清零，后续闪避又回到 1.9 秒，人物面板也始终为 0。
- 正确实现通过 `CharacterStats.AddStatModifier` 写入上述上游能力属性，在 Unity 主线程依次调用 `BaseStats.UpdateStats`、`AbilityStatsMutatorManager.UpdateAbilityStats`、`ChargeManager.RefreshAllChargeInfo` 和 `CharacterSheet.UpdateSheet`。读取也改为上游能力属性，不再把可覆盖字段当作权威值。
- 2026-10-03 实机验证：移动冷却 100% 时，只有闪避 `chargeRegen 0.51374996 -> 0.76374996`（通用冷却 100% 保持开启），游荡灵魂和骨魔像不变；用户确认人物面板显示正确，连续施放后仍保持约 1.3 秒，不再回退到 1.9 秒。

### 经验倍率（SP 105，1.5.1 已完成最终结算验证）

- 游戏的 `ExperienceTracker.GainExpFromEnemyOrMote` 和 `GainExpDirect` 最终都进入私有的 `ExperienceTracker.GainExp(long characterExp, long abilityExp, long expForFavourGain)`；该路径本身不读取 SP 105，因此旧实现只写人物 `Stats` 会出现回读成功但实际经验不变的假成功。
- 正确实现在该最终入口校验 1.5.1 原始指令后安装进程期钩子，从 SP 105 取用户设置，仅放大 `characterExp`，不改动技能经验和阵营声望经验。设为 0% 时钩子为中性，退出游戏后由进程回收；如入口已被其他补丁改写则拒绝叠加。
- 2026-10-03 实机验证：经验倍率 100% 时，用户击杀一只普通怪后，钩子在最终入口记录到最后一笔 `characterExp 10 -> 20`，本次共观测到 4 次结算调用，人物当前经验由 123 增加到 203。

### 效果范围（SP 116，1.5.1 已完成实际技能验证）

- `GenericAreaSkillMutator.Mutate` 在技能创建时调用 `Stats.GetIncreasedAreaForAreaSkill`，后者以 SP 116 进入 `Stats.GetTotalAdded`，再用 `Maths.GetIncreasedRadiusAfterAdditiveAreaIncrease` 把面积加成换算为实际半径。
- 旧实现写入并回读 `increasedValue`，没有进入上述最终消费路径。正确实现以 `modificationType = 0` 写入通用标签的 `addedValue`，每次创建区域技能时由游戏自行取值，无需直接改半径缓存。
- 2026-10-04 用户确认效果范围 300% 在实际技能中生效。退出并重启游戏后该值恢复为 0%，进一步确认它属于当前进程会话，不写入角色存档。
- SP 116 也会被部分技能用于视觉缩放。例如“收割”的 `HarvestMutator.getIncreasedCastVFXSize` 与碰撞体修改都读取同一面积加成，因此 300% 会同时放大实际范围和挥舞的镰刀虚影。这不是持戴武器模型被改写；按用户决定保留统一范围语义，不为“收割”增加技能特例。

### 药剂掉落率（SP 47，1.5.1.1 已完成最终概率与实战验证）

- `HealthPotion.updatePotionStats` 只读取 SP 47 的 `addedValue`，汇总到 `HealthPotion.increasedPotionDropRate`，并把最终 `dropChance` 重建为 `baseDropChance * (1 + increasedPotionDropRate)`；旧实现写 `increasedValue`，不会进入该路径。
- 正确实现以 `modificationType = 0` 写入 SP 47，并在游戏主线程调用 `HealthPotion.updatePotionStats`。写入后同时验证派生加成和 `HealthPotion.getDropChance()` 的最终返回值。
- 2026-10-04 的 1.5.1.1 实机中，基础概率为 14%；1000% 测试值使最终返回值达到 154%。用户观察到生命药剂明显增多，随后测试值恢复为 100%（最终概率 28%）。

### 总物品掉落率（最终掉落协程，1.5.1.1 已完成结算路径验证）

- SP 104 不是全局掉落倍率：`ItemDropBonuses.onStatsUpdate` 会按 `specialTag + tags` 分流到具体装备类型、非装备、碎片、暗金或金币加成。把 SP 104 的通用标签写成一个“物品掉落率”会只影响某个分类，产品语义错误。
- 总倍率改为在 `ItemDrop.<DropItem>d__*.MoveNext` 调用 `ItemDrop.getItemDropChance` 后、把结果保存为 `<iDropChance>5__5` 前处理。运行时先从 `ItemDrop` 的嵌套类型语义定位 `DropItem` 状态机，再验证唯一直接调用和 14 字节原始指令；若结构或指令不匹配则拒绝安装。
- 钩子只在当前进程中保存倍率，并记录最近一次原概率、应用后概率与实际结算次数。2026-10-04 以 1000%（11 倍）测试时，记录从 2 次增长到 33 次；最近一次概率由约 3.55% 变为 39.05%，证明倍率经过了真实怪物掉落结算，而不是人物统计自写自读。

### 分类掉落与金币数量（1.5.1.1）

- 暗金与套装共用 `GenerateItems.RollRarity` 的稀有度流程。极端倍率测试先造成套装分支截走结果，拆分暗金分支后又出现无可用暗金模板时吞掉普通装备以及钩子稳定性问题。按用户决定，暗金专用字段、运行期钩子和诊断入口已全部移除；装备掉落只保留上文已完成真实结算验证的“总物品掉落率”。
- 普通、魔法、稀有、暗金和套装的品质生成不是一个可安全拆分的通用倍率；金币堆频率也只有派生缓存证据，未完成实际频率验收。按用户决定，模块不再提供任何单独品质或金币堆频率滑块。
- “金币倍率”安装在 `GroundItemManager.dropGoldForPlayer(Actor, int goldValue, Vector3, bool)` 的最终金币落地入口，只改变 `goldValue`，不改变是否生成金币堆。入口原始指令不匹配或已有未知补丁时会拒绝安装；钩子记录最近一次原始数量、应用后数量和实际金币堆计数，用于区别内部回读与真实掉落。

近战范围只有 `Actor.GetWeaponRange()` 回读与 `WeaponInfoHolder.weaponRange` 写入证据，未证明实际近战命中/碰撞距离改变，按用户决定连同相关字段、校验与写入路径一并移除。掉落不能只以人物 `Stats` 回读作为完成证据。

一次性主线程入口必须保留并恢复原始 `RCX/RDX/R8/R9/XMM1`，把 IL2CPP `MethodInfo*` 放到正确的隐藏参数位置，并在超时时恢复虚表。超时情况下不能释放可能仍被游戏线程取走的代码页；宁可把少量临时内存保留到进程退出，也不能制造延迟访问冲突。

五维、已确认的两种冷却、效果范围、经验倍率、药剂掉落率、总物品掉落率和金币倍率都按当前游戏运行/人物会话字段处理，不伪装成持久存档字段。通用冷却已验证跨地图重建；2026-10-04 重启游戏后，通用冷却、移动冷却、效果范围和经验倍率均恢复为 0，经验钩子也不再存在，符合仅当前会话有效的产品语义。

`LocalPlayer.stats` 不是唯一权威根，不应作为人物属性的固定入口。

## 资源与本地化

持久化资源根来自人物资源存档；实时界面根来自活动的 `StashItemContainer`：

- 材料：`StashItemContainer.Materials -> SingleSubTypeContainer -> OneSlotItemContainer.content -> ItemContainerEntry.quantity`
- 副本钥匙：`StashItemContainer.Keys -> ItemContainer.content -> ItemContainerEntry.quantity`

写入时先更新持久化权威记录并标脏，再在已有实时槽位中同步 `quantity`。因此已存在的符文、雕文、碎片和钥匙可在重新打开/整理资源页后显示新值，不必重新选择人物；从 0 首次创建、而运行期尚无槽位对象的资源，仍可能需要重新进入人物由游戏创建界面对象。

中文名称直接读取全局 `Localization._tableCache`：

- 词缀碎片：`Item_Affix_{id}_DisplayName`
- 普通物品与副本钥匙：`Item_SubType_Name_{itemType}_{subType}`
- 唯一装备：`Unique_Name_{uniqueId}`

本地化表只做显示增强；读取失败不能改变稳定的数值身份，也不能使安全写入退化为按名称匹配。

## 装备、世界与失败边界

装备只定位 `localItemContainersManager.crafting.main.content`，每次写入前重新读取熔炉主槽，并以 `ItemData.individualID` 核对当前物品。只暴露该物品潜能字段，不读取或修改穿戴栏、普通背包和仓库。

摄像头从 `CameraManager.instance` 重新定位，仅改当前虚拟摄像头 FOV。地图迷雾已从产品范围永久移除：旧实现从注入工作线程调用 `Object.FindObjectsOfTypeAll`，会在后续刷新时崩溃，严禁恢复相关入口。怪物数量只发现了场景生成临时字段，也不交付。

曾验证会造成崩溃的共同模式是：从远程工作线程直接调用 Unity API、序列化入口或游戏业务方法。安全实现只能使用 IL2CPP 元数据解析/分配、直接字段和托管容器操作，并把保存交给游戏自己的正常更新循环。任何新增路径都必须完成读取、写入、即时复读、重新进入人物与重启游戏后的分层验证。

模块验收不能只直接实例化适配器读取数据；还必须从宿主 `GameAdapterRegistry` 加载已打包 DLL，并使用当前进程与指纹执行 `Resolve`。这一步能发现“运行时对象字段可从派生类读取，但兼容校验误查基类”等只会在宿主启用阶段出现的问题。

## v0.4.1 发布收口

- 保留已经进入最终消费链并有游戏内观察证据的效果范围、两种冷却、经验倍率、药剂掉落率、总物品掉落率和金币数量倍率。
- 近战范围只有 getter/字段回读，没有命中或碰撞证据；金币堆频率只有派生缓存证据；二者连同字段、校验与写入路径全部移除。
- 暗金与套装共享品质流程，极端倍率会改变分支竞争，错误拆分还可能吞掉正常装备或导致进程退出，因此不再提供任何品质分类倍率，只保留全局物品生成概率。
- 后续属性只有在“稳定语义定位、正确线程、最终消费者、可逆实测、安全失败”五项同时成立时才能进入清单；不稳定实现应删除，不为保留选项而积累特殊补丁。

## v0.4.2 Host API 3 兼容收口

- 本版本不改变 Last Epoch 的游戏内读写逻辑，只修正宿主协议声明：模块依赖 `IEntityEditorsGameAdapter`，因此必须声明 Host API 3，不能继续伪装成 API 2 可加载。
- SDK 产品版本提升为 3.0.0，同时固定 CLR `AssemblyVersion` 为 2.0.0.0，使新宿主仍能绑定不依赖新接口的既有 API 2 模块；真正的能力门槛由 `module.json` 的 `hostApiVersion` 控制。
- 发布前必须使用本地脚本完成全模块构建、清单校验和仅含 DLL/`module.json` 的包内容检查；GitHub Actions 不再作为发布前提。

## v0.5.0 Host API 4 页面归属

- 模块通过 `IGameEditorPageProvider` 明确拥有人物属性、装备、资源、异界进度和世界功能五个页面；主程序只渲染标准页面模板，不再按 Last Epoch 编辑器 ID 后缀创建固定 Tab。

## v0.5.1 Host API 6 模块自有页面

- 五个页面改由模块的 `IGameEditorPageFactoryProvider` 创建完整 WPF 页面，清单不再声明 `kind` 或页面角色。
- 宿主只提供导航容器、生命周期令牌、状态、通用输入/错误弹框与保存语义字段服务，不读取页面内部控件。
- 本次不改变任何 IL2CPP 类型、字段、方法、写入、刷新或保存路径；既有构建校验和安全失败边界保持不变。
- 异界页面的无记录提示由模块注册，避免主程序包含 Last Epoch 专属文案。
- 人物页面通过 `IGameEditorFieldPolicyProvider` 区分持久化点数与仅本次运行的属性和倍率，运行期字段不再允许锁定或自动重应用。
- 本版本不改变任何 Last Epoch 内存定位、主线程写入、刷新、保存或回读实现。
