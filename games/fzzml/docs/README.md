# fzzml 模块实现说明

稳定游戏 ID：`game.fzzml`，当前模块版本：`2.0.0`。

## 背包物品

编辑器 ID：`game.fzzml.inventory`。

每次从 `SaveManager._cachedSnapshot -> AllParsedData.inventoryRows` 读取 `List<List<string>>`，按物品名聚合数量。写入在 Unity 主线程创建新的 IL2CPP 数量字符串、替换目标行、重建缓存并调用 `SaveInventory2D_Binary`，随后重新定位并回读总数。超过 9999 的值不由模块拆栈，交给游戏后台逻辑处理。

## 人物属性

编辑器 ID：`game.fzzml.character-attributes`，`SessionOnly = true`。

从 `playerDefault.units` 枚举人物，以 `UnitSlotData.unitId` 为稳定身份。开放根骨、力道、神识、身法、体魄五项白名单。写入时读取当前原始值和当前 `PlayerUnitConfig` 基础值，以 `newBase = oldBase + targetRaw - currentRaw` 保持等级、成长和境界部分不变，然后在 Unity 主线程调用 `PlayerAttributeAggregator.Compute` 与 `PlayerAttributeEventHub.RaiseAttributesChanged`。

修改立即反映到游戏，但不写存档。关闭或重启游戏后恢复，不锁定，不自动重应用。

## 当前人物构建

- EXE：`8B476C50395ACF8B4BD32E3DB60E29AC136436A5FADAB0E8D0049CA87CE3AACD`
- GameAssembly：`BF156D35DDB79839797517BC95BC07080DAACDCF78D08579B7D0D4C09386D752`
- metadata：`AEB09A9D3C8359F54C2DF29F3045C5AE1D9270DC269EC0A8E18C0D359CE4CD29`

实机验证：背包读取成功；人物枚举 10 人，每人 5 项属性；同值主线程写入成功；先前的玄道力道实验从界面 506 调整到 1036，重启后恢复 506。
