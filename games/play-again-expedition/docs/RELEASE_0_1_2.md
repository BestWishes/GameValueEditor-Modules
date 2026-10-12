# 再刷一把：远征 0.1.2

2026-10-12 用户授权修复版本与发布问题后提交发布。详细设计及三遍方案/实施检查见 [交付与发布可靠性](../../../docs/DELIVERY_AND_RELEASE_RELIABILITY.md)。

## 变更与迁移

- 编号接续已交付本地 0.1.1，正式发布 0.1.2。0.0.6/0.0.7 与本地 0.0.7～0.1.1 均可正常升级，不需要先降级。
- 最低宿主仍 0.5.2 / Host API 8；建议主程序 0.5.3，修复旧精确包遮挡新版与动态构建手动回退。
- 材料、大转盘、掉落、命格、装备五页不改游戏逻辑；固定请求资源与正式 0.0.7 / 本地 0.1.1 保持一致。只读识别与实际调用点仍按当前游戏解析，不新增历史哈希白名单或启动参数。
- 旧正式和本地包不覆盖；其它三个游戏模块使用原正式资产。

## 验证及正式产物

源码阶段远征 UI 363 项、共享模块页面 186 项及 Node 回归通过；四个模块均编译 0 警告/0 错误，发布工具负向回归通过。未连接原游戏或修改存档；历史实机与保存证据继续按原范围保留。

正式包：`GameValueEditor.Module.PlayAgainExpedition-v0.1.2.zip`，221,893 字节。

- SHA-256：`BA49547B89EC5F634D3585654F3C925E2A3B471EB513AEAF144C45B49BC1F4F2`。
- 干净源码提交：`efe2dd2407ba0e953b35a60c6363f23c1c6bce0d`。
- ZIP 精确两项：模块 DLL（942,080 字节）、module.json（2,249 字节）。
- 嵌入 `Expedition.Requests.json` 的 SHA-256：`49556CCDD2D159EAE37EADD122DB4B8192D35B9C22794FFC62F72B4371139C2B`，与 0.0.7/本地 0.1.1 一致。
- 主程序 0.5.3 完整冒烟实际加载四个原正式模块包，共 14 页通过，远征自身 5 页通过；不是只比较清单或加载自编译临时 DLL。

## 线上与完整离线交付

- [0.1.2 Release](https://github.com/BestWishes/GameValueEditor-Modules/releases/tag/play-again-expedition-v0.1.2) 已通过代理上传，独立 VerifyOnly 验证远端大小/SHA-256 与本地一致；注释标签指向上述干净源码提交。
- 上传前 main 已有构建证据文档提交；上传后目录提交 `a6ce9b0` 再推进 main，重跑相同发布入口仍成功复用已上传资产。这次真实验证“标签属于 main 历史”的恢复约束，不再要求标签等于最新 main。
- 公开 raw 目录与本地完整结构相同；远征保留 0.1.2/0.0.7/0.0.6 三版。其余三个游戏目录条目完全不变，也各保留三版，没有删除它们的 Release/资产。
- `verify-release.ps1 -VerifyReleasedVersions` 全部通过：四模块及 LiveTests Release 编译、目录/Schema/工具回归、完整宿主冒烟、四个原正式 ZIP 共 14 页加载。不重打其它三个不可变版本。
- 完整离线包位于宿主 `D:/MyOtherProjects/GameValueEditor/dist/GameValueEditor-v0.5.3-202610121.zip`，99,505,851 字节；SHA-256 `E5DF370B125DCEFC2EA942C3DF30452B4CC40E3DC80905AFBBA6FC0132CBAF25`。包含主程序 0.5.3 / API 8、远征 0.1.2、Fzzml 2.1.5、WorldApart 1.3.5、Last Epoch 0.5.6；实际页面、文件散列、EXE 启动及独立短名 VerifyOnly 全通过，LatestStatus=Current。
- 本地冻结记录：宿主 `artifacts/offline-bundle-receipts/E5DF370B125DCEFC2EA942C3DF30452B4CC40E3DC80905AFBBA6FC0132CBAF25.json`。损坏 DLL / 错误页面登记两项负向集成通过。完整 ZIP 没有上传 GitHub，仅用于本地或 QQ 分发。

这次没有修改原游戏或存档、启动参数和用户当前安装，也没有做新的逐项实机写入。其它游戏模块及远征游戏规则请求保持原样。
