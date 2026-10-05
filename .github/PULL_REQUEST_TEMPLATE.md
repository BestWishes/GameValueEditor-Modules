## 游戏与构建

- 游戏模块 ID：
- 游戏版本：
- EXE SHA-256：
- GameAssembly SHA-256：
- metadata SHA-256：

## 数据链路

- 权威读取来源：
- 稳定实体/字段身份：
- 写入线程与调用链：
- 刷新或保存流程：
- 最终面板、消费或结算证据：
- 持久化或 `SessionOnly`：

## 安全与验证

- [ ] 未知构建安全拒绝，没有通配构建或猜测偏移。
- [ ] 每次操作重新定位对象，不保存跨会话指针。
- [ ] `module.json`、项目版本、页面注册和字段策略一致。
- [ ] `./scripts/validate-modules.ps1 -SkipCatalog` 通过。
- [ ] `./scripts/verify-release.ps1 -SkipCatalog` 使用当前宿主加载了真实模块包。
- [ ] 已记录最小影响的实机验证；任何测试写入均已恢复原值。
- [ ] 未提交游戏本体、存档、内存转储或私有资源。

`contributors.generated.json` 由维护者在合并后更新，请勿手工修改。
