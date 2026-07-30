# ReExtractor

![ReExtractor 图标](src/ReExtractor.Gui/Assets/AppIcon.png)

ReExtractor 是一个面向 Windows 的 RE Engine 资源工作台，用于加载游戏 PAK，配合路径 list 浏览资源，并预览、提取、合并和导出模型、贴图、骨架与动画。

项目定位是解包、预览和导出。它不提供 Mod 编辑、PAK 重新打包或游戏资源传播服务。

## 主要功能

- 加载单个或多个 PAK；选择某个 PAK 时，会一并加载同目录下的其他 PAK。
- 选择游戏文件夹后自动扫描目录内全部 PAK。
- 选择路径 list 后浏览和定位资源路径；部分 list 会尝试自动匹配本机游戏目录。
- 支持 TEX 贴图预览，并可导出 PNG / DDS。
- 支持 Mesh 模型、骨架、材质、贴图和 MotionList 动画预览。
- 模型默认先快速加载几何；需要材质贴图时，可在预览区点击“加载贴图”。
- 支持多个模型分件叠加预览，也支持同骨骼模型合并预览。
- 支持播放 MotionList 动画，手动拖动进度条后会自动暂停。
- 动画时间显示包含秒数、当前帧、总帧数和源帧率。
- 支持导出当前预览模型 FBX，以及当前动画或全部动画 FBX。
- 动画导出按源动画速度和有效帧范围输出，避免尾部重复静止帧。
- 默认导出文件夹位于程序 EXE 同级目录，可在主界面、环境窗口和设置中直接打开。
- 首次启动会弹出环境窗口；未配置 Blender 时执行 FBX 导出也会提示配置环境。

## 当前支持状态

| 游戏 / 能力 | 状态 |
| --- | --- |
| 《鬼武者：剑之道》DEMO | 已在样本上验证 PAK、贴图、模型、骨架、动画预览与 FBX 导出工作流 |
| 《街头霸王 6》 | 已在样本上验证 PAK、list、模型基础材质与贴图读取；动画仍属于实验性支持 |
| 其他 RE Engine 游戏 | 取决于 PAK 版本、路径 list、Mesh / TEX / MDF / MotionList 格式版本 |

新游戏并不是只更换 list 就能完整支持。PAK 解包、模型、贴图、材质和动画格式都可能需要针对真实样本适配。

## 下载与使用

1. 在 GitHub Releases 下载最新的 Windows x64 压缩包。
2. 解压到一个可写目录，运行 `ReExtractor.Gui.exe`。
3. 首次启动时检查环境；FBX 导出需要安装 Blender，并在设置中选择 `blender.exe`。
4. 在路径列表中选择或下载对应游戏的 list。
5. 选择游戏文件夹扫描 PAK，或直接拖入 / 打开 PAK 文件。
6. 双击资源进行预览。
7. 使用右键菜单把模型加入合并器或叠加到当前预览。
8. 使用右侧 FBX 导出面板导出模型或动画。

## FBX 导出说明

- FBX 转换依赖 Blender，请在设置中选择 Blender 可执行文件。
- “导出预览模型 FBX”会导出当前预览中可见的全部模型分件。
- “导出动画 FBX”可导出当前动画或 MotionList 内全部动画；每个动画单独输出，不包含模型网格。
- 动画导出使用源动画帧率和有效帧范围，不再提供手动 30 / 60 FPS 选项。
- 导入 DCC 或 Unreal Engine 前，仍建议检查骨架方向、根运动、材质贴图和缩放。
- RE Engine 材质不能通过 FBX 完整还原，FBX 主要用于模型、骨架和动画交换。

## 目录结构

默认导出目录位于程序 EXE 同级，其他运行数据位于 `ReExtractor-tools` 目录：

```text
ReExtractor/
├─ ReExtractor.Gui.exe
├─ output/                  # 默认导出目录
└─ ReExtractor-tools/
   ├─ filelists/            # 本地路径 list
   ├─ temp/                 # 临时文件
   ├─ data/                 # 用户设置
   ├─ logs/                 # 运行日志
   └─ tools/                # 内置转换脚本缓存
```

## 源码构建

环境要求：

- Windows 10 / 11
- .NET SDK
- Blender，只有 FBX 导出时需要

```powershell
git clone --recurse-submodules https://github.com/PieceMetal/ReExtractor.git
cd ReExtractor
dotnet build ReExtractor.sln
dotnet run --project src/ReExtractor.Gui/ReExtractor.Gui.csproj
```

## 解析核心与更新

项目使用 [kagenocookie/RE-Engine-Lib](https://github.com/kagenocookie/RE-Engine-Lib) 作为内置解析核心。它是源码子模块，不是用户可以随意替换的 Noesis 插件。

当 CAPCOM 发布新游戏或升级资源格式时，需要先合并解析核心更新，再在本项目中完成适配和样本验证，最后随 ReExtractor 新版本整体发布。

## 第三方项目

- RE-Engine-Lib：MIT License
- Avalonia：MIT License
- Silk.NET：MIT License
- Blender：由用户独立安装，并遵守 Blender 自身许可证

详细信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 免责声明

本项目与 CAPCOM 及其关联公司无关。游戏名称、商标和资源归各自权利人所有。请仅处理你合法拥有的游戏文件，并遵守所在地法律与游戏服务条款。
