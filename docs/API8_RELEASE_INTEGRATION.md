# Host API 8 配套发布

本次用户授权将此前本地修复与主程序 v0.5.1 一起正式发布。模块仍独立编号：WorldApart 1.3.4、Fzzml 2.1.4、Last Epoch 0.5.5；清单与项目版本同步，两仓 SDK 4.0.3，CLR AssemblyVersion 2.0.0.0 不变。

新版本要求 Host API 8 和最低主程序 0.5.1。旧 API 7 快照保留原来的 API/最低版本，不按当前清单重写。旧主程序从保留历史中选择兼容包；使用新模块的用户回退旧主程序前须先手动回退模块，宿主会阻止不兼容的直接回退，没有自动回退。

页面仍由模块自由实现，主题和间距由宿主提供。所有页面写入通过宿主按实际字段协调，整页读取通过模块快照；共享背包/人物/实体页及 Last Epoch 资源页全部接入。资源页仍使用现有分类，没有“全部”，选分类直接显示其资源项。稳定模块/编辑器 ID、语义字段键和游戏构建指纹不变。

编译三个模块、SDK 及全部测试项目；运行真实编译 DLL 中四类页面的 180 项读写/快照断言，使用隔离数据和宿主服务，不执行真实游戏写入。再由生产宿主创建三个模块的九个编辑页，并核对新旧 API 包。测试不证明游戏内属性或保存效果。

先提交已验证源码，从干净提交生成每模块仅 DLL + module.json 的不可变 ZIP，ProductVersion 跟踪标签源码提交。先发布主程序 API 8，再发布并核验三模块公开资产；全部完成后才使用 CatalogOnly 更新目录并单独提交推送。每模块最新三版本快照必须匹配资产大小/哈希，最后清理第四个旧 Release/资产、保留 Git 标签。完整离线包只在主程序仓库本地生成，不上传 GitHub。

提交前验证已通过：三个模块、SDK、实机测试项目零警告/错误；真实页面回归 180 项通过；生产宿主完整冒烟分别核对原 API 7 包及新 API 8 隔离源码夹具，均创建 2/2/5 共九页；身份/游戏构建声明不变，两仓 SDK 文件哈希相同，清单/主题验证及差异检查通过。没有连接真实游戏。源代码检查的 SkipCatalog 不代表发行完成。详细契约见[扩展规范](MODULE_EXTENSION_GUIDE.md)。

## 正式资产核验

正式三包从干净源码提交 `65942dd066e2314183dc9512fc6dbff400e79fea` 重建，对应注释标签均指向该提交。先发布主程序 v0.5.1 后，三模块各一次代理上传成功，并独立 VerifyOnly 确认公开资产大小和 SHA-256 一致。最终 ZIP 各仅 DLL + module.json，新包均为 API 8 / minimumHostVersion 0.5.1，生产宿主完整冒烟已加载三包并创建九页。

| 模块与 Release | 大小（字节） | ZIP SHA-256 |
| --- | --- | --- |
| [WorldApart 1.3.4](https://github.com/BestWishes/GameValueEditor-Modules/releases/tag/worldapart-v1.3.4) | 48701 | 3A327CD3E76F5FA4988B6552C68A8FDC913A45DDD8504F778B63D0A2CBE06A2A |
| [Fzzml 2.1.4](https://github.com/BestWishes/GameValueEditor-Modules/releases/tag/fzzml-v2.1.4) | 50749 | 6D4E0F4A1569FEE0B81852A055544319C6A4AAE1242B1CF87FB483F7CB257D63 |
| [Last Epoch 0.5.5](https://github.com/BestWishes/GameValueEditor-Modules/releases/tag/last-epoch-v0.5.5) | 73761 | 2A5DC65C72BD58D8EEDF3CFE77984B5CEB83A5A15E7C83DDB1A10CB2BC4CD7DE |

新目录各保留 3 个版本：WorldApart 1.3.4/1.3.3/1.3.2、Fzzml 2.1.4/2.1.3/2.1.2、Last Epoch 0.5.5/0.5.4/0.5.3。保留的 API 7 快照与上一提交逐项 JSON 对比一致；正式全目录复验 `verify-release.ps1 -VerifyReleasedVersions` 已通过，复用原始 ZIP，不重打包。目录推送及公开读回后才执行保留清理，Git 标签/源码保留。
