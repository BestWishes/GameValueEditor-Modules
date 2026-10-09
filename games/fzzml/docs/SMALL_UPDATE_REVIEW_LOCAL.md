# 放置斩魔录小更新兼容性检查（2026-10-09，历史检查）

纠正此前游戏对应关系写错：`game.fzzml` 是放置斩魔录，不问凡尘是 `game.worldapart`。以下保留的是重构前的静态检查结果；当时没有修改本模块源码、清单或玩家数据。后续本地修复已经实现当前元数据定位，见[两个游戏修复记录](../../../docs/SMALL_UPDATE_TWO_GAMES_LOCAL.md)，不要再把以下旧限制当作当前 2.1.5 实现。

## 结论

存在类似“游戏小更新后整包不支持”的限制，而且不止包清单一层：

1. `module.json` 的 `compatibleBuilds` 仅列出三个三文件哈希组合，没有声明当前运行时解析新构建的能力。
2. `FzzmlGameAdapter.Supports` 要求 EXE、GameAssembly、metadata 全部命中历史组合；任一分量变动即返回不支持。
3. `ResolveLayout` 再次计算三文件哈希并查找静态 `BuildLayout`，未登记构建直接抛错。因此仅改清单或 Supports 没有用。
4. 人物属性还有独立的 GameAssembly/metadata 哈希限制，即使背包可以用，也不代表人物属性可以用。
5. 这些限制背后是 SaveManager、保存方法、主线程执行入口、人物配置方法及字段布局的固定 RVA/偏移，并非只需按名字寻找对象。直接删掉哈希后继续套用旧地址，会把“不可用”变成调用错误地址的风险。

证据：`src/FzzmlGameAdapter.cs` 的 SupportedBuilds、Supports、ResolveLayout 和 Session；`src/FzzmlCharacterAttributes.cs` 的 CharacterBuildAssembly、CharacterBuildMetadata、SupportsCharacterAttributes、CharacterSession 固定方法 RVA 与 AttributeLayout。

## 后续正确方向

游戏名称用于身份识别，不应由版本文件决定是不是同一个游戏。模块自身应通过当前 IL2CPP 元数据/API 按程序集、类型、字段、方法及语义签名解析现有对象，主线程调用与存档流程也必须重新定位；地址只缓存于对应运行实例。背包与人物属性分别报告能力，不能一项未适配就把整个游戏判断为另一个游戏。

这份历史检查没有执行重构，也没有新增哈希代替重构；当时尚未针对更新后的放置斩魔录进行游戏内读写验证。后续状态以新版实现文档及本地修复记录为准。
