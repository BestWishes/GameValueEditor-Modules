# Deskrawl（桌面破坏神）专属模块

稳定模块 ID：`game.deskrawl`。模块提供六个编辑器：

- `人物属性`：金币、当前经验、可用天赋点、可用巅峰点、基础主属性，以及力量、敏捷、智力、金币发现、物品发现、生命药水发现、材料发现和获得经验值。百分比属性按游戏面板单位显示；天赋与巅峰是两套独立数据。
- `装备生成`：新生成装备的随机词条原本在基础值的 `85%～115%` 之间取值；可在本次运行中固定为游戏合法上限 `115%`。
- `掉落规则`：可覆盖怪物每次掉落尝试产出装备的概率；另行提供 `OfflineTradableDropMultiplier` 对应的特殊物品权重倍率，因为稀有鱼和部分特殊掉落路径不经过人物物品发现。
- `钓鱼`：可覆盖最终钓鱼成功率。原生钓鱼成功后固定加入 1 个物品，没有独立数量参数。
- `材料`：从完整 `ItemData` 定义中枚举全部材料并显示游戏中文本地化名称；缺少库存行的材料也显示为 `0`。
- `背包物品`：只显示当前拥有且可以堆叠的非材料物品，并显示中文名称；可通过游戏自己的入库/移除和保存路径修改总数。不可堆叠的宝箱或装备不列入数量编辑器。

金币、经验、天赋点、巅峰点、基础主属性、材料和背包物品会调用游戏保存链。最终人物派生属性使用最终属性消费者覆盖，不会被装备重算清除；装备最高词条、掉落规则和钓鱼成功率是本次运行覆盖，关闭对应页面时自动恢复。

模块使用 Host API 8，最低主程序版本为 `0.5.1`。当前精确构建的三文件指纹记录在 `module.json`；正式版 `1.0.2` 仅启用该已验证构建，各能力仍分别检查所需结构。

构建与实机验证：

```powershell
dotnet build .\games\deskrawl\GameValueEditor.Modules.Deskrawl.csproj -c Release -p:Platform=x64
pwsh -NoProfile -File .\scripts\validate-modules.ps1 -SkipCatalog
dotnet run --project .\tests\GameValueEditor.Modules.LiveTests\GameValueEditor.Modules.LiveTests.csproj -c Release -- --game=deskrawl
```

权威对象、游戏公式、保存路径、写入边界和当前验证状态见 `docs/README.md`。
