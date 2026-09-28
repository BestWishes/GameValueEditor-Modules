# WorldApart 专属修改模块

稳定游戏 ID：`game.worldapart`，当前模块版本：`1.0.0`。

## 游戏内编辑模块

- `game.worldapart.inventory`（背包物品）：按物品配置 ID 重新定位当前背包对象，实时读取和修改物品总数。
- `game.worldapart.character-attributes`（人物属性）：按玩家、属性类别和配置 ID 重新定位道途点、灵根点、修为、灵气、精力及其上限、灵力及其上限、基础属性、探索属性、五行灵根与战斗属性。

人物面板中的精力、精力上限、灵力和灵力上限统一归入游戏原有的“基础属性”分类，并按当前值、上限成对相邻显示。人物面板的“灵力”与突破界面的“丹田灵气”是两个概念；当前模块不会把灵力错误标记为丹田灵气。

`基础属性 · 修为` 对应 `CombatModel.CultivateExp`；`资源 · 灵气` 对应修炼/突破系统使用的 `CombatModel.CultivateReserveExp`。两者都按当前存档对象重新定位、在游戏主线程写入、调用自动存档并回读。

快捷入口只保存语义键，不保存进程地址。每次读取或写入都会从 `GameStoreManager.CurrentPlayer` 重新解析当前存档对象；写入在 Unity 主线程执行，并调用游戏自己的自动存档流程。

五行灵根字典中尚不存在的属性也使用游戏自己的 `Dictionary<int,int>.set_Item` 新增。模块从当前字典实例解析构造泛型方法元数据，让 IL2CPP 自行处理扩容和版本更新，不直接拼装托管字典内部结构。

## 兼容性与安全边界

模块只接受 `module.json` 中实现代码明确登记的 EXE、`GameAssembly.dll` 与 `global-metadata.dat` 三重指纹。未知构建会拒绝写入，不能用相似版本的偏移猜测兼容。

当前首个受支持构建：

- EXE SHA-256：`35369BA362352B5A80A2E5844CD93F5A5FFFD18CEE61EB4861B9F4E920CDE835`
- GameAssembly SHA-256：`E4BFA837BD5F43BF5FFBE3E28C80B40CD3E20B24CE63941CE2280874DF2FA056`
- metadata SHA-256：`55F65FE395395CAA4C3C8DE6F874107AB92742CA638F1E0F77ECB609A22154FA`

更新游戏后，应先按扩展指南重新验证对象布局、主线程入口、界面刷新和存档回读，再新增一个精确构建布局。
