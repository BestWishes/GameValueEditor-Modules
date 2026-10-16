# Deskrawl（桌面破坏神）专属模块

稳定模块 ID：`game.deskrawl`。模块提供六个编辑器：

- `人物属性`：金币、当前经验、可用天赋点、可用巅峰点、基础主属性，以及力量、敏捷、智力、金币发现、物品发现、生命药水发现、材料发现和获得经验值。百分比属性按游戏面板单位显示；天赋与巅峰是两套独立数据。
- `装备生成`：新生成装备的随机词条原本在基础值的 `85%～115%` 之间取值；可在本次运行中固定为游戏合法上限 `115%`。
- `掉落规则`：可覆盖怪物每次掉落尝试产出装备的概率；另行提供 `OfflineTradableDropMultiplier` 对应的特殊物品权重倍率，因为稀有鱼和部分特殊掉落路径不经过人物物品发现。
- `钓鱼`：可覆盖最终钓鱼成功率。原生钓鱼成功后固定加入 1 个物品，没有独立数量参数。
- `材料`：从完整 `ItemData` 定义中枚举全部材料并显示游戏中文本地化名称；缺少库存行的材料也显示为 `0`。
- `背包物品`：只显示当前拥有且可以堆叠的非材料物品，并显示中文名称；可通过游戏自己的入库/移除和保存路径修改总数。不可堆叠的宝箱或装备不列入数量编辑器。

金币、经验、天赋点、巅峰点、基础主属性、材料和背包物品会调用游戏保存链。最终人物派生属性使用最终属性消费者覆盖，不会被装备重算清除；装备最高词条、掉落规则和钓鱼成功率是本次运行覆盖，关闭对应页面时自动恢复。

模块使用 Host API 8，最低主程序版本为 `0.5.1`。正式版 `1.0.4` 延续本地交付 `1.0.3` 的小更新自动验证：文件指纹未登记也会检查实际运行时，不再仅因哈希不同整包拒绝。混淆方法按完整签名、唯一候选和已分析函数的完整规范化指令结构定位，而非固定方法名/RVA；能力失配分别拒绝。精确指纹保留为构建证据，不是唯一启用开关。

模块同时提供游戏自身声明的版本、名称和构建 GUID，在宿主的“游戏版本”页及兼容性诊断中显示。版本读取 Unity `Application.version`，名称和 GUID 读取同一安装中的 Unity 启动信息文件；不以模块名称、Unity 引擎版本或 Steam 构建号代替。

声明信息读取包含在正式版 `1.0.4` 和此前本地 `1.0.3` 包中；历史公开 `1.0.2` 包不包含该能力。正式版从干净的源码提交重新构建并使用独立版本，不覆盖已交付的本地包或历史公开资产。发布说明见 [docs/RELEASE_1_0_4.md](docs/RELEASE_1_0_4.md)。

构建与实机验证：

```powershell
dotnet build .\games\deskrawl\GameValueEditor.Modules.Deskrawl.csproj -c Release -p:Platform=x64
pwsh -NoProfile -File .\scripts\validate-modules.ps1 -SkipCatalog
dotnet run --project .\tests\GameValueEditor.Modules.LiveTests\GameValueEditor.Modules.LiveTests.csproj -c Release -- --game=deskrawl
```

只验证上述三个只读字段、不运行属性和规则写入测试：

```powershell
dotnet run --project .\tests\GameValueEditor.Modules.LiveTests\GameValueEditor.Modules.LiveTests.csproj -c Release -- --game=deskrawl --metadata-only
```

权威对象、游戏公式、保存路径、写入边界和当前验证状态见 `docs/README.md`。

小更新与故障隔离验证（不执行属性/物品写入、不安装业务钩子或保存）：

```powershell
dotnet run --project ./tests/GameValueEditor.Modules.LiveTests -c Release -- --game=deskrawl --compatibility-only
dotnet run --project ./tests/GameValueEditor.Modules.LiveTests -c Release -- --game=deskrawl-semantic-code
```

`--snapshot-only` 另外验证人物、中文材料、已有可堆叠物品及三种规则读取；名称读取会临时使用并恢复原有主线程派发入口，不是纯兼容诊断。最新证据和边界见 `docs/SMALL_UPDATE_1_0_3.md`。自动识别是所有官方模块的强制开发/验收要求，见仓库 `AGENTS.md` 及扩展指南；不保证任意未来代码或布局变化都兼容。
