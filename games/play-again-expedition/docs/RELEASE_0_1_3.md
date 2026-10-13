# 远征 0.1.3：区分未执行拒绝与不确定写入

## 详细方案（2026-10-13）

范围仅为远征模块的结果分类，不改主程序、SDK、游戏识别、小更新兼容或材料/概率/物品生成算法。五个内容页继续共用模块自己的串行请求与会话记录，不增加用户操作步骤，不自动重发业务请求。

1. 固定请求在发送实际修改前记录阶段。读取当前数据、检查旧回执、发现忙碌状态时发生的拒绝，返回有版本、请求名称和有限错误码的 `not-executed` 回执。不携带路径、存档或任意异常文本。
2. 材料后端只在首次赋值前标记拒绝；概率、命格、装备会话只在首次改钩子/设置前标记拒绝。已经尝试赋值、安装/移除钩子、保存或修改设置后发生的错误，不允许冒充“未执行”。
3. C# 收到匹配当前固定请求的回执，先复核同一接口，再把会话记为 `rejected`，显示原因并允许用户刷新后重新操作。不会把拒绝当成修改成功，也不会自动重试。
4. 超时、断线、格式错误、请求名称不符、写后校验失败仍记为 `uncertain`。旧 `pending` / `uncertain` 文件不自动解锁，仍只读到正常重启游戏；不能从历史普通错误文字反推“从未写入”。
5. 更新模块清单和项目默认版本为 0.1.3，保持 Host API 8 / 最低主程序 0.5.2。代码与使用说明一起提交；构建不可变 ZIP、核验上传摘要，再单独更新目录并执行最新三个包保留规则。主程序不升版、不重发。

## 编码前三遍检查

第一遍：逐一找到材料首次赋值、概率/命格/装备首次规则修改；写前与写后使用相同错误码也不能决定分类，必须依据实际阶段。`rejected-before-save` 已尝试赋值，不纳入未执行。

第二遍：回执由固定模块代码生成，绑定请求名称；C# 不依据本地化提示解锁。接口身份复核、落盘失败和旧不确定记录保持保守；仅新增终态 `rejected`，不清除历史保护。

第三遍：只读请求行为不变；没有新启动参数、自动重试、自动恢复或主程序游戏专属代码。旧版已应用规则保持可读/可恢复，未知旧控制器错误不能被新协议误判。测试必须覆盖五页的拒绝、拒绝后的正常操作及真正不确定结果。

## 编码后三遍检查与发布证据

第一遍（功能）：远征全部 Node 测试及 16 个打包固定请求测试合计 426 项通过。覆盖五页的旧回执/忙碌拒绝、刷新后继续、恢复规则、同值零写入、材料赋值后回滚/保存不确定、规则应用后读取失败与回执丢失。后两类仍不是 `not-executed`，不会重试。

第二遍（边界）：新增协议与会话文件检查 74 项，通过现有模块测试项目执行，不另建小工具。九种修改请求的拒绝记为 `rejected` 而非成功；错误协议、请求不匹配、额外/重复字段、无效错误码和普通异常文字不能解锁。旧 pending/uncertain 保持原样；互斥租约与键边界保持。未应用的旧缓存在刷新时升级，已应用的旧控制器保持同一对象和原规则、可显式恢复。未知旧控制器的写后错误仍保守处理。

第三遍（集成）：远征现有 `--ui-only` 测试 437 项通过（包含上述 74 项），仅 fake client，无游戏或网络操作。四个模块 Release 编译均 0 警告/0 错误；模块真实页面 249 项、兼容检查 35 项、包装 14 项、构建约束 19 项、发布基础设施 20 项、目录约束 8 项及 0～5 版保留模拟通过。现有主程序完整冒烟通过；其隔离模块验证器实际加载本轮本地预检查 ZIP 并创建远征 5 页。主程序仓库无源码改动。

可重复检查命令：

```powershell
node --test 'experiments/play-again-expedition/tests/*.test.cjs' games/play-again-expedition/tests/fixed-requests.test.cjs
dotnet run --project games/play-again-expedition/tests/ModuleTests/GameValueEditor.Modules.PlayAgainExpedition.Tests.csproj -c Release -- --ui-only
./scripts/validate-modules.ps1 -SkipCatalog
```

没有连接原游戏、修改内存/存档、抽奖、战斗、保存或操作屏幕；本轮不宣称新增实机功能验证。

## 正式产物与线上复验

- 干净源码提交：`d2d9f77022f03caa1076c94873838cf0eb49fde9`；注释标签 `play-again-expedition-v0.1.3` 指向该提交。
- [0.1.3 Release](https://github.com/BestWishes/GameValueEditor-Modules/releases/tag/play-again-expedition-v0.1.3)：正式 ZIP `GameValueEditor.Module.PlayAgainExpedition-v0.1.3.zip`，227,066 字节。
- SHA-256：`B9D5E8184AC277A2E40249267E5DC38D749553C235A161A266B1A0D93AD1B9C0`。ZIP 仅含 DLL（966,144 字节）和 module.json（2,249 字节）；API 8、最低主程序 0.5.2、五页和贡献者信息核验通过。
- 使用 `http://127.0.0.1:7897` 代理上传，独立 `-VerifyOnly` 确认线上大小与摘要一致；正式 ZIP 被当前主程序实际加载，远征五页通过。
- 上传验证后才更新目录；目录提交 `3b374c4` 已推送。公开 raw 目录完整结构与本地一致，其余三个游戏条目没有变化。
- `verify-release.ps1 -VerifyReleasedVersions` 通过：所有模块及 LiveTests 编译、发布工具/Schema/目录回归、宿主完整冒烟、四个原正式 ZIP 共 14 页。首次运行只因本地集成缓存缺少现有 Last Epoch 0.5.7 ZIP 停止；从 dist 原正式包校验大小/摘要并补齐缓存后通过，没有重打或重新上传该模块。
- 目录和 GitHub Release 都只保留远征 0.1.3、0.1.2、0.0.7。按已有最新三个包策略删除 0.0.6 的旧 Release 和附件，保留其 Git 标签以及本地 `dist/GameValueEditor.Module.PlayAgainExpedition-v0.0.6.zip`，没有删除其它游戏资产。

## 完整离线包（仅本地）

`D:/MyOtherProjects/GameValueEditor/dist/GameValueEditor-v0.5.4-20261013.zip`，99,515,190 字节。

- SHA-256：`CB71AB0AC167C9CF772AE49E3577056F14F69DBDEB1267CB7B818162DB48C053`。
- 主程序 0.5.4（沿用原正式标准包，源码提交 `7c767f70df4b89bd12cdc0a8ea4d5b23cbfe0ff5`）；Fzzml 2.1.5、WorldApart 1.3.5、Last Epoch 0.5.7、远征 0.1.3。
- 主程序标准包 SHA-256：`ECD1A61EACFE9F0EBAE92B722F348B4EF9B648AE9161657E7992AFDC942E34D6`。没有修改、提交或重发主程序。
- 使用现有完整离线构建器；完整文件集/原始散列、真实模块 14 页和 EXE 启动通过。独立 VerifyOnly 返回 IntegrityVerified / ModulesVerified / StartupVerified=true、LatestStatus=Current；离线工具 112 项及错误 DLL/错误页面登记两项负向集成通过。
- 冻结证据保存在主程序 `artifacts/offline-bundle-receipts/CB71AB0AC167C9CF772AE49E3577056F14F69DBDEB1267CB7B818162DB48C053.json`。完整包未上传 GitHub，不覆盖旧完整包或用户安装。
