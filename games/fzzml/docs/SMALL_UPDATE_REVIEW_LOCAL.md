# 不问凡尘小更新兼容性检查（2026-10-09，仅检查）

用户所称“不问凡尘”对应既有模块 `game.fzzml`，其模块清单仍沿用“放置斩魔录”旧显示名称。本轮按照用户范围检查它与远征，不继续检查或改动万里仙途。本轮没有修改本模块源码、清单、包、游戏内存或存档。

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

本轮没有把该重构伪装成已完成，也没有以增加新哈希代替重构。尚未针对更新后的实际不问凡尘进行游戏内读写验证；本结论是当前源码静态检查结果，不表示目前正在运行的版本一定已经失效。
