# Last Epoch 0.5.7 发行记录

日期：2026-10-12。用户授权修复、提交和发布；范围仅 `game.last-epoch`，不修改或重发宿主和其它游戏模块。

## 正式模块

- [GitHub Release](https://github.com/BestWishes/GameValueEditor-Modules/releases/tag/last-epoch-v0.5.7)
- 源码提交：`c00e3262529e29b541986b0fef6cdff6c29449ce`
- 带注释标签：`last-epoch-v0.5.7`，标签对象 `4cdbad90767061a9c08d80f65e4376d70a56ca31`；包内 DLL 的 ProductVersion 追溯到上述源码提交。
- 资产：`GameValueEditor.Module.LastEpoch-v0.5.7.zip`，73,707 字节，只含模块 DLL 与包含贡献者元数据的 `module.json`。
- SHA-256：`384C945C1CA986A95389B7FCC7F4D8F86AEED841CB50C7ECE80F9FB8549E3B64`
- Host API 8，最低宿主 0.5.1，五页不变；建议宿主 0.5.3。

正式 ZIP 从干净源码提交生成，四个真实模块包的宿主加载/14 页及完整 SmokeTests 再验通过。发布经本机代理单次上传成功，随后独立 `-VerifyOnly` 确认线上大小与 SHA-256；校验成功后才推进目录。目录保留 0.5.7、0.5.6、0.5.5 三个快照，其它三个模块的快照不变。Release 清理预演仅指向 0.5.4，实际清理在目录推送确认后执行，保留源码标签。

两种离线入口、可逆修改及三遍方案/代码复核的具体证据和未测试边界见 [方案与验收](OFFLINE_SESSION_0_5_7.md)。普通离线不需要自行加参数或使用新启动器；联网登录/聊天不等于正在联机。旧进程曾用过未带角色门闩的旧版倍率时，正常重启一次即可清除旧钩子，不修改游戏文件。

## 完整离线包（仅本地交付）

文件：主程序仓库 `dist/GameValueEditor-v0.5.3-202610122.zip`，99,508,978 字节；不上传 GitHub。

SHA-256：`6A107D2D3904222D11E2D120633D0806A0D1E93EBC7637F9FB17EBB2CA6475A2`

使用原主程序 0.5.3 正式 ZIP，宿主源码提交为 `e102ee3afeb2919c389a18277bd866eb178a1787`，标准 ZIP 摘要为 `35DAD35A0F6C2ADFEDD9B43F4AD69D6AB730DB69D12153F61E80CE06F8A8582F`；没有重打或上传同版本主程序资产。

| 内含模块 | 版本 |
|---|---|
| 放置斩魔录 `game.fzzml` | 2.1.5 |
| 不问凡尘 `game.worldapart` | 1.3.5 |
| 最后纪元 `game.last-epoch` | 0.5.7 |
| 再刷一把：远征 `game.play-again-expedition` | 0.1.2 |

统一构建器验证原宿主 ZIP 与发行索引，校验四个模块包摘要、清单及真实页面，实际启动打包 EXE 后才提升候选 ZIP。离线包回归 112 项和真实页面验证器的坏 DLL/坏页面注册两个负例通过。私人存档、备份、研究探针、游戏库和地址缓存均未打入包；完整包凭证只保存在本机 `artifacts/offline-bundle-receipts/<SHA256>.json`，不随包发布。安装好的模块无需 GitHub 即可加载；查新/下载后续版本仍需要网络，本次不宣称修复别人电脑上的 GitHub DNS 错误。
