# 游戏模块模板

复制本目录后：

1. 把 `game.example` 替换为发布后不再改变的游戏 ID。
2. 在 `module.json` 注册一个或多个编辑模块。
3. 为每个编辑器实现模块自己的 WPF 页面；可以替换脚手架使用的共享源码页面，不受固定模板限制。
4. 项目引用 `sdk/GameValueEditor.ModuleSdk`，ZIP 中不要携带 SDK DLL。
5. 为每个精确构建建立独立布局并校验三文件指纹。
6. Release 资产验证完成后再用 `-CatalogOnly` 写入根 `catalog.json` 的真实 SHA-256。

实现细节和验收要求见 `docs/MODULE_EXTENSION_GUIDE.md`。
