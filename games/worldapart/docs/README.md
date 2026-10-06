# WorldApart 专属修改模块

稳定游戏 ID：`game.worldapart`，当前模块版本：`1.3.3`。

本版本使用 Host API 7。背包和人物属性页面均由本模块程序集创建，并使用宿主提供的主题资源与间距；宿主只承载导航、视觉系统、生命周期和公共弹框/保存字段服务，页面布局不由 `Kind`/`Role` 模板决定。游戏定位、主线程写入和保存链路未改变。

实机验证代码位于同游戏目录的 `tests/WorldApartLiveTest.cs`，由仓库通用运行器执行：

```powershell
dotnet run --project tests/GameValueEditor.Modules.LiveTests/GameValueEditor.Modules.LiveTests.csproj -- --game=worldapart
```

## 游戏内编辑模块

- `game.worldapart.inventory`（背包物品）：按物品配置 ID 重新定位当前背包对象，实时读取和修改物品总数。
- `game.worldapart.character-attributes`（人物属性）：按玩家、属性类别和配置 ID 重新定位道途点、灵根点、修为、灵气、精力及其上限、灵力及其上限、基础属性、探索属性、五行灵根与战斗属性。

人物面板中的精力、精力上限、灵力和灵力上限统一归入游戏原有的“基础属性”分类，并按当前值、上限成对相邻显示。人物面板的“灵力”与突破界面的“丹田灵气”是两个概念；当前模块不会把灵力错误标记为丹田灵气。

`基础属性 · 修为` 对应 `CombatModel.CultivateExp`；`资源 · 灵气` 对应修炼/突破系统使用的 `CombatModel.CultivateReserveExp`。两者都按当前存档对象重新定位、在游戏主线程写入、调用自动存档并回读。

快捷入口只保存语义键，不保存进程地址。每次读取或写入都会从 `GameStoreManager.CurrentPlayer` 重新解析当前存档对象；写入在 Unity 主线程执行，并调用游戏自己的自动存档流程。

五行灵根字典中尚不存在的属性也使用游戏自己的 `Dictionary<int,int>.set_Item` 新增。模块从当前字典实例解析构造泛型方法元数据，让 IL2CPP 自行处理扩容和版本更新，不直接拼装托管字典内部结构。

## 兼容性与安全边界

模块优先接受 `module.json` 中实现代码明确登记的三重指纹。对于仅替换启动 EXE 的发行版本，只有 `GameAssembly.dll` 与 `global-metadata.dat` 同时完整命中同一项已验证布局，才忽略启动器哈希差异。不再使用短函数序言回退，因为它不能证明对象字段和固定方法地址未变化。缺少哈希、跨记录拼接及未知运行文件组合均拒绝。

实际入口核对进程启动实例、真实 EXE 和已加载 GameAssembly 路径，再读取完整文件哈希；哈希缓存绑定 PID/创建时间/路径/文件状态，不跨进程实例复用。验证期间文件变化会拒绝，Session 在任何远程调用之前再次用实际进程句柄核对创建时间。该变更未调整已有业务布局或保存路径；本次未执行实机写入验证。

游戏目录可任意移动。若第三方发行版本修改了可执行文件名，模块会优先寻找与 EXE 同名的 `_Data` 目录，再寻找 `WorldApart_Data`；仅当安装根目录中只有一个可验证的 Unity `_Data` 目录时才自动采用，避免误选其他游戏数据。

当前首个受支持构建：

- EXE SHA-256：`35369BA362352B5A80A2E5844CD93F5A5FFFD18CEE61EB4861B9F4E920CDE835`
- GameAssembly SHA-256：`E4BFA837BD5F43BF5FFBE3E28C80B40CD3E20B24CE63941CE2280874DF2FA056`
- metadata SHA-256：`55F65FE395395CAA4C3C8DE6F874107AB92742CA638F1E0F77ECB609A22154FA`

当前第二个受支持构建（Steam Build ID `25617557`，游戏自报版本 `0.34.7343955`）：

- EXE SHA-256：`35369BA362352B5A80A2E5844CD93F5A5FFFD18CEE61EB4861B9F4E920CDE835`
- GameAssembly SHA-256：`EDE8A956051C0C831F79E0874AFF28297F41D3FA18C18AD2CA2FF16C33D0938D`
- metadata SHA-256：`BBECA25F98CFC56BFE48A5BD6DC90B9AADFEB1A6BB8F0DC03CA8DA2EF26DBDBC`

游戏安装目录不是兼容身份。移动到其他目录或 Steam 库后，只要当前运行进程与相同已验证运行文件组合一致，模块仍可使用；宿主会在重新连接后刷新保存路径。

更新游戏后，应先按扩展指南重新验证对象布局、关键函数、主线程入口、界面刷新和存档回读。运行文件哈希变化时需新增经过验证的精确 `BuildLayout`，不能因短签名不变沿用旧布局。诊断中的页面注册是 Information，不能视为页面读写已成功。
