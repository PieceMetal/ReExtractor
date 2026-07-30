# ReExtractor

![ReExtractor 图标](src/ReExtractor.Gui/Assets/AppIcon.png)

ReExtractor 是一款面向 Windows 的 RE Engine 资源工作台，专注于将游戏 PAK 中的模型、动画和贴图提取出来，并整理为 DCC 软件与 Unreal Engine 可继续使用的文件。

> 项目定位：解包、预览和导出。不提供 Mod 编辑、PAK 重打包或游戏资源传播服务。

## 主要功能

- 加载单个或多个 PAK，也可选择游戏目录自动扫描。
- 按基础包与 Patch PAK 顺序合并，使新资源覆盖旧资源。
- 本地与在线 list 管理，支持路径还原、模糊搜索与资源提取。
- TEX 贴图预览与 PNG/DDS 输出。
- Mesh 模型、材质、骨骼和动画预览。
- 角色分件合并，合并后同步播放动画。
- 模型 FBX 与动画 FBX 分开导出，支持 30 FPS / 60 FPS。
- 便携式目录管理：list、设置、日志、临时文件和默认输出均位于 EXE 附近。

## 当前支持状态

| 游戏 / 能力 | 状态 |
| --- | --- |
| 《鬼武者：剑之道》DEMO | 已验证 PAK、贴图、模型、骨骼、动画与 FBX 工作流 |
| 《街头霸王 6》 | PAK 解包与 list 路径还原可用；模型材质与动画为实验性支持 |
| 其他 RE Engine 游戏 | 取决于 PAK 版本、文件 list 与资源格式版本 |

新游戏并不是仅更换 list 就能完整支持。PAK 解包、Mesh、Tex、MDF 和 Motlist 都可能需要新的格式适配与真实样本验证。

## 下载与使用

1. 在 GitHub Releases 下载 ReExtractor-v0.1.0-win-x64.zip。
2. 解压到一个可写目录，运行 ReExtractor.Gui.exe。
3. 打开“设置”，选择 Blender 可执行文件与默认导出目录。
4. 在 list 管理器中选择或下载对应游戏的 list。
5. 拖入 PAK，或选择游戏目录后扫描全部 PAK。
6. 双击资源进行预览，使用右键菜单提取、合并或加载动画。

### FBX 导出说明

- FBX 转换需要 Blender，请在设置中选择 blender.exe。
- “导出模型”会导出当前预览中可见的合并模型。
- “导出动画”可选择当前动画或 MotionList 内全部动画，每个动画单独输出，不携带模型网格。
- 导入 UE 前仍建议检查骨骼方向、材质贴图与根运动。RE Engine 材质不能被 FBX 完整还原。

## 便携目录结构

~~~text
ReExtractor/
├─ ReExtractor.Gui.exe
├─ filelists/       # 本地路径 list
├─ output/          # 默认导出目录
├─ temp/            # 临时文件
├─ data/            # 用户设置
└─ logs/            # 运行日志
~~~

## 源码构建

环境：Windows 10/11、.NET 10 SDK；如需 FBX 转换，另行安装 Blender。

~~~powershell
git clone --recurse-submodules https://github.com/PieceMetal/ReExtractor.git
cd ReExtractor
dotnet build ReExtractor.sln
dotnet run --project src/ReExtractor.Gui/ReExtractor.Gui.csproj
~~~

## 解析核心与更新

项目使用 [kagenocookie/RE-Engine-Lib](https://github.com/kagenocookie/RE-Engine-Lib) 作为内置解析核心。它是源码子模块，不是用户可随意替换的 Noesis 插件。

当卡普空发布新游戏或升级资源格式时，需要先合并解析核心更新，再完成本项目适配与样本回归，最后随 ReExtractor 新版本整体发布。不建议用户直接覆盖解析 DLL。

## 第三方项目

- RE-Engine-Lib：MIT License。
- Avalonia：MIT License。
- Silk.NET：MIT License。
- Blender：由用户独立安装，遵循 Blender 自身许可证。

详细信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 免责声明

本项目与 CAPCOM 及其关联公司无关。游戏名称、商标和资源归各自权利人所有。请仅处理你合法拥有的游戏文件，并遵守所在地区法律与游戏服务条款。