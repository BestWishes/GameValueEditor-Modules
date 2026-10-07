# 游戏模块模板

复制本目录后：

1. 把 `game.example` 替换为发布后不再改变的游戏 ID。
2. 在 `module.json` 注册一个或多个编辑模块。
3. 为每个编辑器实现模块自己的 WPF 页面；可以替换脚手架使用的共享源码页面，不受固定模板限制，但必须使用 Host API 7 的宿主主题资源键、控件样式和间距。当前脚手架要求 API 8：实现 `ICoordinatedGameEditorPageProvider`，页面字段修改经 `context.Host` 的 `IGameEditorFieldOperations.WriteFieldAsync`，不得直接调用适配器绕过保存字段锁定协调。
   整页数据刷新使用 `IGameEditorSnapshotOperations.ReadSnapshotAsync<T>(read, apply)`；`read` 由宿主在后台执行，`apply` 只同步更新页面，不等待或调用游戏。发生协调写入重叠时丢弃快照并提示重新刷新，不自动重试；保留独立页面布局，不能回退为无协调读取。
4. 项目引用 `sdk/GameValueEditor.ModuleSdk`，ZIP 中不要携带 SDK DLL。
5. 为每个精确构建建立独立布局并校验三文件指纹。
6. 明确维护 Host API 和最低/最高主程序版本；版本号独立不代表天然兼容。
7. Release 资产验证完成后再用 `-CatalogOnly` 写入根 `catalog.json` 的真实大小和 SHA-256。

实现细节和验收要求见 `docs/MODULE_EXTENSION_GUIDE.md`。
