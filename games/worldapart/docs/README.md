# WorldApart 专属修改模块

稳定游戏 ID：`game.worldapart`，当前模块版本：`1.3.0`。

## 游戏内编辑模块

- `game.worldapart.inventory`（背包物品）：按物品配置 ID 重新定位当前背包对象，实时读取和修改物品总数。
- `game.worldapart.character-attributes`（人物属性）：按玩家、属性类别和配置 ID 重新定位道途点、灵根点、修为、灵气、精力及其上限、灵力及其上限、基础属性、探索属性、五行灵根与战斗属性。

人物面板中的精力、精力上限、灵力和灵力上限统一归入游戏原有的“基础属性”分类，并按当前值、上限成对相邻显示。人物面板的“灵力”与突破界面的“丹田灵气”是两个概念；当前模块不会把灵力错误标记为丹田灵气。

`基础属性 · 修为` 对应 `CombatModel.CultivateExp`；`资源 · 灵气` 对应修炼/突破系统使用的 `CombatModel.CultivateReserveExp`。两者都按当前存档对象重新定位、在游戏主线程写入、调用自动存档并回读。

快捷入口只保存语义键，不保存进程地址。每次读取或写入都会从 `GameStoreManager.CurrentPlayer` 重新解析当前存档对象；写入在 Unity 主线程执行，并调用游戏自己的自动存档流程。

五行灵根字典中尚不存在的属性也使用游戏自己的 `Dictionary<int,int>.set_Item` 新增。模块从当前字典实例解析构造泛型方法元数据，让 IL2CPP 自行处理扩容和版本更新，不直接拼装托管字典内部结构。

## 兼容性与安全边界

模块优先接受 `module.json` 中实现代码明确登记的三重指纹。对于仅替换启动 EXE 的第三方发行版本，只要 `GameAssembly.dll` 与 `global-metadata.dat` 同时命中同一项已验证布局，就忽略启动器哈希差异；这两个文件才是 IL2CPP 对象布局和方法地址的权威来源。新版布局另有保守结构签名回退：只有根对象、配置表、本地化、业务写入、刷新、保存、主线程钩子和游戏版本读取等全部关键函数的机器码都与已验证布局一致时才会启用；任一签名变化就拒绝写入。

游戏目录可任意移动。若第三方发行版本修改了可执行文件名，模块会优先寻找与 EXE 同名的 `_Data` 目录，再寻找 `WorldApart_Data`；仅当安装根目录中只有一个可验证的 Unity `_Data` 目录时才自动采用，避免误选其他游戏数据。

当前首个受支持构建：

- EXE SHA-256：`35369BA362352B5A80A2E5844CD93F5A5FFFD18CEE61EB4861B9F4E920CDE835`
- GameAssembly SHA-256：`E4BFA837BD5F43BF5FFBE3E28C80B40CD3E20B24CE63941CE2280874DF2FA056`
- metadata SHA-256：`55F65FE395395CAA4C3C8DE6F874107AB92742CA638F1E0F77ECB609A22154FA`

当前第二个受支持构建（Steam Build ID `25617557`，游戏自报版本 `0.34.7343955`）：

- EXE SHA-256：`35369BA362352B5A80A2E5844CD93F5A5FFFD18CEE61EB4861B9F4E920CDE835`
- GameAssembly SHA-256：`EDE8A956051C0C831F79E0874AFF28297F41D3FA18C18AD2CA2FF16C33D0938D`
- metadata SHA-256：`BBECA25F98CFC56BFE48A5BD6DC90B9AADFEB1A6BB8F0DC03CA8DA2EF26DBDBC`

游戏安装目录不是兼容身份。移动到其他目录或 Steam 库后，只要当前运行进程通过相同运行时代码/元数据或完整结构校验，模块仍可使用；宿主会在重新连接后刷新保存路径。

更新游戏后，应先按扩展指南重新验证对象布局、关键函数签名、主线程入口、界面刷新和存档回读。布局变化时新增精确 `BuildLayout`；只有哈希变化且完整签名不变时才使用现有布局回退。
