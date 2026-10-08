# 再刷一把：远征 · 安全实时接入研究

日期：2026-10-07。仅本地研究，不提交、不发布。用户负责游戏启动、退出和画面操作；助手不操作屏幕。

## 当前结论

最新扩展：用户已授权实现全部材料数量，新的固定全材料后端和 List/Set 驱动覆盖当前远征存档 29 项堆叠字段。220 项自动回归、精确纯 Engine 的 116 个目标案例通过；原游戏新启动实例已成功只读列出 29 项，与原生保存数量一致。尚未因此宣称所有材料实机写入或长期稳定。旧强制接入路线仍禁用；范围、上限和后续小值测试见 [全材料设计](ALL_MATERIALS_LOCAL_DESIGN.md)。

2026-10-08：用户另外授权的合成保护卷、招募券、深渊挑战券三项逐项加一已完成完整单字段保存和重新读取，其余 26 项数量未变。用户确认画面正常，正常重启另获得一张深渊挑战券离线收益；新实例进入原存档后读取确认 1、559、133999116 且身份一致。恢复仅移除测试增量，逐项保存并再读为 0、558、133999115，保留正常收益及其他 26 项数量，没有覆盖旧完整存档。用户确认删除参数后再次正常重启数量正常；系统核验新原构建实例无 inspector 参数、59321 无监听、游戏仍运行，旧 13 个备份哈希未变。这三项受控闭环及参数清理完成，最后数量来自用户反馈，未强行重启接口。不能据此宣称 29 项全部实机通过、正式模块已交付、长期稳定或旧崩溃根因已修复。

装备保护卷的逻辑字段和游戏保存路径已确认。用户授权的新构建启动式 Node inspector 试验已完成入口、协议、固定只读、修订版 2→3 提交、正常重启后 3、独立恢复 2、正常重启后 2 和临时参数/监听清理；当前原数量为 2。本项在精确 0.116.70 的受控闭环通过，但没有正式模块交付，也没有所有材料或长期稳定的结论。不能把 73 项自动测试或一次受控闭环写成旧崩溃根因已修复。

旧方法通过运行中进程的 Node 原生调试入口接入，随后在 inspector.close 清理附近发生崩溃。没有调用栈，根因未定；旧驱动继续禁用，不能重试，不能为了排查降低 Windows 安全设置。

## 新构建和隔离证据

- 游戏 0.116.70，Electron 44.2.0 x64；EXE SHA-256：`A79F66A6F236B237E25C00DD9F8BA763F9BAD13E16CD3269DE2348EA7FFEF396`。
- ASAR SHA-256：`5A4F4E414BD06FC248CFE2AD30E7D9EE3B110968DB888FDB733CD57F78A6B294`。
- main.cjs.jsc SHA-256：`be1e2155ab30204e9929d8cc636a23d4b08e9a519035eb961a4eb317fb040d81`。
- 已保存单个只读原 ASAR 副本，逐一验证原文件复制前后和副本哈希一致；没有再制作或启动游戏副本界面。
- 在匹配的 Electron RunAsNode 独立进程中读取上述主脚本字节码；核验 V8 magic、版本和只读快照头，只有匹配时才执行隔离 VM。
- Electron、文件系统、游戏依赖、IPC、窗口和事件全部替换为记录用假对象；ready 永不完成，事件回调不执行，真实网络/存档/Steam 依赖均不加载。游戏的文件读请求被明确阻止，实际游戏进程从未被连接。
- 冷启动主脚本在 ready 前实际调用了 removeSwitch，依次移除 `remote-debugging-port`、`remote-debugging-pipe`、`inspect`、`inspect-brk`。这比仅看到字符串更强，但仍不是正常 Electron 浏览器进程的启动验证。
- 启动阶段注册的 IPC 是时钟采样、时钟联网、启动/运行诊断、屏幕模式和分辨率，没有材料读写 IPC。静态查看 api-service 字节码得到排行榜/本地身份相关路由，未发现可用材料编辑入口；不向排行榜提交数据或利用它执行代码。

研究脚本：`C:/Users/19654/Documents/Codex/2026-10-05/gamevalueeditor-next-review/research/play-again-expedition/trace-startup-isolated.cjs`。

证据目录：`D:/MyOtherProjects/GameValueEditor-Modules/artifacts/play-again-expedition/20261007-cold-startup-trace`，其中 startup-trace.json 是隔离记录，app-0.116.70.original.asar 是未修改的原包快照。该目录是本地研究产物，不进入发布包或 Git。

## 接入方案筛选

1. **现有官方/游戏桥接**：当前没有找到可用的材料操作契约，不把时钟、屏幕或排行榜接口当成编辑接口。
2. **Chromium 远程调试启动参数**：游戏主动移除这些参数；不能直接承诺给 Steam 加一个端口就能连接。
3. **NODE_OPTIONS 加载桥接**：Electron 对打包应用限制 NODE_OPTIONS，`--require` 不属于允许项，不作为可靠启动方案。[Electron 环境变量文档](https://www.electronjs.org/docs/latest/api/environment-variables#node_options)
4. **启动时显式 Node inspector**：已通过下文的入口/只读检查，并在用户另外授权后完成保护卷的受控提交、画面/正常重启/恢复和临时设置清理。Electron 文档支持 `--inspect=127.0.0.1:<port>`。它与运行中强行开启调试入口不同。removeSwitch 操作 Chromium 命令行而不修改 process.argv，不能仅凭隔离结果断言已提前初始化的 Node inspector 一定关闭。当前不能承诺长期稳定，不开放广泛材料写入，也不承诺“不调用 close 就永远不会崩溃”。[Electron 调试参数](https://www.electronjs.org/docs/latest/api/command-line-switches#--inspecthostport)、[CommandLine.removeSwitch](https://www.electronjs.org/docs/latest/api/command-line#commandlineremoveswitchswitch)
5. **可逆安装桥接或原生注入**：涉及修改游戏启动/安装或新的注入风险，不在本轮默默实施。需要用户明确同意，不能恢复之前被拒绝的独立 WPF/替代游戏路线。

Bytenode 官方列有调试器/跨上下文相关已知限制；这只是风险依据，不足以认定本次 Windows 崩溃就是同一个原因。[Bytenode 已知限制](https://github.com/bytenode/bytenode#known-issues-and-limitations)

## 下一次实机验证的门槛

用户已正常保存并退出，并明确要求不再创建备份、沿用已有备份。本轮只读检查遵从该要求：原备份保留不动，不用旧完整 JSON 或旧数据库覆盖当前进度。已经只读校验现有恢复前备份的 13 个文件，哈希仍与原验证记录一致；这不代表它包含退出前的最新进度。助手准备启动方式，用户负责启动；保留正常 Steam 授权和原游戏，不修改 ASAR、授权、排行榜或安全设置。

首轮只验证启动参数传递、监听是否仅在 127.0.0.1、进程/构建身份，以及只读连接的稳定性。不得自动升级为写入测试，不发送 Debugger.enable、不打开开发者工具、不执行旧 _debugProcess 或 inspector.close，不结束游戏进程。未知状态立即停止连接，不自动重试写入。只读成功仍不等于后续写入安全；仍需独立的小值测试、游戏观察、正常保存退出、重启和恢复闭环。

### 本次用户启动步骤

- 退出检查：游戏及便携启动器进程均为 0；退出后 NSIS 清理了原临时运行目录，不能复用之前的临时 EXE 路径或 PID。
- Steam 安装构建仍为 25777966，原安装入口仍存在；本机端口 59321 当前没有监听者。
- 用户在 Steam 的游戏属性 → 通用 → 启动选项中，保留原有内容并在末尾临时添加 `--inspect=127.0.0.1:59321`，不要使用会在首行暂停的 inspect-brk，也不添加 Chromium 远程调试参数。
- 用户仍从 Steam 正常启动原游戏，先停在标题菜单、不进入存档。助手只确认原启动器是否传递参数、实际游戏构建、监听所有者/地址，以及只读目标描述；启动失败或端口不存在时不强制开启调试。
- 准备此步骤时尚无当前游戏的实机证据；不能只凭 Electron 参数说明或 electron-builder 启动器模板认定成功。随后已通过下文的实际参数传递检查，整体稳定性仍不能据此保证。[Electron 参数说明](https://www.electronjs.org/docs/latest/api/command-line-switches#--inspecthostport)、[标准便携启动器模板](https://github.com/electron-userland/electron-builder/blob/master/packages/app-builder-lib/templates/nsis/portable.nsi)
- 用户正常退出验证游戏后，在 Steam 删除仅本次追加的参数，保留原有启动设置；助手不执行 inspector.close 或强行结束游戏。

### 原游戏启动入口实测（2026-10-07）

用户从 Steam 启动后，后台只读检查确认：

- 原安装便携启动器和实际 Electron 主进程均收到 `--inspect=127.0.0.1:59321`；没有打开游戏副本，没有强行调用原生调试入口。
- EXE、ASAR 与上文 0.116.70 的已检查指纹完全一致；Steam 构建仍为 25777966。
- 系统 TCP 监听信息显示 59321 仅绑定 127.0.0.1，归属于该原游戏主进程；没有公网/任意地址监听。
- `/json/version`、`/json/list`、`/json/protocol` 可只读访问。实际 Node 版本为 v24.20.0，目标类型 node，标题 electron/js2c/browser_init；这不是 renderer 页面目标。
- 从实际协议描述核验 `Runtime.getIsolateId` 后，建立一次 WebSocket 连接，两次请求返回同一个 isolate ID。随后正常关闭客户端 WebSocket；没有关闭游戏内 inspector 服务。
- 客户端断开后原主进程、renderer 和调试监听均仍存在。游戏运行日志显示标题阶段 boot、entered=false、帧数持续增长；当前没有观察到这一步引发崩溃。
- 本轮没有发送 `Debugger.enable` 或 `Runtime.evaluate`，没有执行游戏脚本或材料 API，没有打开当前存档数据库、修改材料、创建新备份、提交或发布。

结论限于“原游戏启动参数传递和只读协议连接/客户端断开通过”。它不能证明旧崩溃根因已经修复、所有调试操作稳定、材料修改安全、保存成功或正常退出生命周期已验证。

该协议阶段结束后，用户进入自己的原存档并确认装备保护卷数量为 2，随后完成下文独立的只读材料对象验证。不得把检查自动升级为写入；旧直接研究驱动继续禁用。调试参数仍是临时验证设置，全部验证结束后由用户正常退出并删除新增参数。

### 当前只读材料检查方案与三轮设计复核

用户已进入原存档，并再次确认装备保护卷数量为 2。本次检查只允许独立的只读函数：读取 getUi、engine.state.materials.protectionScrolls、platform.read 和该 origin 的主存档键。只返回模式、格式、数量和唯一匹配的键名，不导出完整存档，不创建会话全局变量，不调用 serialize、save、flush、材料 setter 或任意写入命令。

连接脚本从当前原游戏命令行发现主进程，重新检查安装启动器、EXE/ASAR 指纹、仅本机监听及所有者，动态发现唯一 Node 目标；不保存 PID/临时路径作为长期身份。主进程验证同一 PID、路径、Electron/Node 版本、原 userData、唯一游戏窗口、原 app.getAppPath 包路径及隔离配置，才通过原生异步调用在既有世界 999 执行固定只读函数。若当前接口暴露 preload 路径也必须匹配；当前构建没有暴露此字段，不能以此推断隔离失效或取消其他身份检查。检查完成只关闭客户端 WebSocket，不关闭 inspector 服务；超时不自动重试或升级写入。

1. 方向复核：只有装备保护卷的语义字段，区分 ui.gameMode、engine.mode 和 origin 存档键；不按数值 2/4 进行堆地址猜写。
2. 边界复核：独立只读代码没有写入分支；测试冻结运行时/平台/存储，并让 serialize、save、storage setter 一旦被调用即失败。未知模式、格式、数量范围、歧义存档或数量不一致直接拒绝。
3. 验证复核：用户界面 2 与逻辑字段、platform.read JSON、唯一主存档键分别比较；该结果只证明本次读取，不能宣称写入或落盘成功。旧驱动继续禁用，原备份保留不动，本次不新建备份、不发布。

### 固定只读材料实测与三轮代码复核

首两次请求均在主进程的隔离配置校验处停止，未到达 renderer 或材料函数。原因是本地校验错误地要求 getLastWebPreferences 返回 preload 路径，而实测它只返回了 contextIsolation=true、nodeIntegration=false，没有该字段。修正为原包路径和隔离配置必须匹配；仅当 preload 字段存在时进一步校验其路径。新增缺失字段、错误原包、错误隔离和暴露错误路径的测试，没有移除构建、存档或世界身份检查。

修正后的实机请求成功：

- 用户画面数量：2；engine.state.materials.protectionScrolls：2；platform.read 的保存 JSON 数量：2。
- getUi().gameMode=expedition，engine.mode=guardian；存档格式 38，唯一对应的主键 zseb-expedition-v1。
- 原包路径、原 profile、contextIsolation=true、nodeIntegration=false 均通过；在既有世界 999 读取，没有切换到世界 0 或创建替代引擎。
- 客户端正常断开后，原主进程和 renderer 仍是同一启动实例，59321 仍只监听 127.0.0.1。后续日志在北京时间 22:33:34～22:34:49 的帧数由 54995 增至 58191，entered=true、phase=idle。本次短期观察没有发现读取/客户端断开导致的崩溃。
- 没有调用材料写入、serialize、save、flush、inspector.close，没有打开原 LevelDB，没有新建备份；不表示游戏自身的正常自动保存被禁止。

三轮代码复核：

1. 实现边界：固定只读函数和单一参数驱动没有写入分支、任意表达式参数、强行开启/关闭 inspector 或全局状态缓存；失败/超时不自动重试。旧直接驱动仍在所有运行时操作前拒绝执行。
2. 自动验证：原交易/桥接 32 项、固定只读函数 11 项、隔离配置契约 4 项，合计 47 项通过。冻结对象、禁写 API、歧义存档、数量不一致、换存档、格式/模式/字段异常均有拒绝测试；PowerShell 语法检查通过。这些测试不替代实机安全验证。
3. 实机与后续观察：构建、进程、监听、原包/profile/隔离配置、语义字段及主存档唯一身份依次核验；读取数量与用户报告一致，客户端断开后的游戏日志仍继续增加。这里只证明一次只读调用，未验证新入口写入、正常保存退出、重启持久化、数量恢复或长期稳定。

代码位于 src/material-readonly.cjs、src/read-context-contract.cjs 及对应测试。驱动位于 `C:/Users/19654/Documents/Codex/2026-10-05/gamevalueeditor-next-review/research/play-again-expedition/read-startup-materials.ps1`；不作为正式模块或发行包。该只读阶段没有自动继续写入。用户随后明确回复“继续”，受控写入的详细方案、三轮复核、第一次失败和修正后的实机结果见 [启动式单字段试验](STARTUP_PROTECTION_WRITE_TRIAL_LOCAL.md)。用户已分别正常重启确认 3 和恢复后的 2；后台确认最后的新启动实例无临时参数、59321 无监听、原游戏继续运行，没有再次强行接入。不把 flush 请求或 origin 保存验证单独视为重启持久化证据。[Electron flushStorageData](https://www.electronjs.org/docs/latest/api/session#sesflushstoragedata)

## 三轮复核

1. 方向：问题分为权威字段、保存和接入安全三部分；目前未解决的是接入安全，不扩大材料范围或再次开发独立界面。
2. 隔离：匹配构建与快照头，依赖白名单，ready/事件不执行，文件/窗口/网络能力阻止；独立助手进程已退出，当前游戏继续正常运行。
3. 证据：原包复制哈希一致；冷启动记录没有错误并到达假 ready；该冷启动阶段旧交易/桥接 32 项测试通过，当时尚无新版本实时材料证据。随后入口协议和一次固定材料只读检查通过，见各自章节；没有新入口写入证据。此次未改游戏文件或当前存档，未提交、上传或发布。

启动入口阶段的三项复核另行确认：构建/进程/监听所有者一致；只发送协议查询、没有游戏脚本和存档访问；客户端断开后的后续日志帧数继续增加，原主进程和监听仍在。该短期观察不证明长期稳定，也不替代材料读写或正常退出验证。
