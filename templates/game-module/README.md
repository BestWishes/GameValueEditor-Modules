# 游戏模块模板

复制本目录后：

1. 把 `game.example` 替换为发布后不再改变的游戏 ID。
2. 在 `module.json` 注册一个或多个编辑模块。
3. 项目引用 `sdk/GameValueEditor.ModuleSdk`，ZIP 中不要携带 SDK DLL。
4. 为每个精确构建建立独立布局并校验三文件指纹。
5. 在根 `catalog.json` 增加下载地址与真实 SHA-256。

实现细节和验收要求见 `docs/MODULE_EXTENSION_GUIDE.md`。
