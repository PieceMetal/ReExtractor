# ReExtractor

![ReExtractor 图标](src/ReExtractor.Gui/Assets/AppIcon.png)

ReExtractor 是一个面向 Windows 的 RE Engine 资源工作台，用于加载游戏 PAK，配合路径 list 浏览资源，并预览、提取、合并和导出模型、贴图、骨架与动画。

项目定位是解包、预览和导出。它不提供 Mod 编辑、PAK 重新打包或游戏资源传播服务。

## [⬇ 查看 GitHub Releases 构建包](https://github.com/PieceMetal/ReExtractor/releases)

> 请在 Releases 页面选择所需版本的 Windows x64 构建包，解压后运行 `ReExtractor.Gui.exe`。压缩包内只有一个 EXE，运行数据会自动生成在程序旁。

当前版本：`v1.2.2`

## v1.2.2 更新

- 资源目录树和搜索结果支持 Ctrl 追加选择、Shift 连续范围选择，并显示当前选中数量。
- 右键菜单新增批量导出选中 TEX 贴图为 PNG，保留原资源目录结构，避免同名贴图覆盖。
- 导出当前角色模型 FBX 时，自动解析所有已加载模型分件的 MDF，并导出其引用的有效 TEX 贴图，无需事先加载贴图。
- 自动跳过 NullWhite、NullBlack 等占位贴图，并在日志中记录单张导出失败。

## v1.2.1 更新

- 修复合并或右键叠加多个模型分件后播放动画不同步、身体变形的问题：改用场景中骨骼最完整的分件作为全局姿势驱动，同名骨骼复用主模型动画。
- 修复加载贴图后视口仍无贴图的问题：支持 DMC5 的 `natives/x64` 资源根、`.tex.11` 纹理版本，以及 `BaseMetalMap` / `BaseShiftMap` 材质通道。
- 修复头发远景稀疏、胡子呈灰色颗粒、眼睛无贴图等材质显示问题：毛发类（头发/胡子/眉毛/睫毛等）alpha 裁切阈值统一为 0.1；对眼球材质强制底色 alpha 为不透明并关闭 cutout；隐藏泪膜/角膜等无底色的眼部 overlay 层；无底色的睫毛改用 BaseColor 生成不透明黑色兜底。
- 修复窗口缩小后顶部工具栏向右越界、动画下拉过宽的布局问题。
- 切换到不同动画时自动从第 0 帧重新播放。

## v1.2.0 更新

- 新增类似 Unreal Engine 的骨骼树：按真实父子关系显示完整骨架，支持名称搜索、全部展开、全部折叠和双击取景。
- 选中骨骼后可查看索引、父级、子级数量、蒙皮类型、源局部位置和缩放，并在视口中以黄色高亮相邻骨骼与三轴标记。
- 骨骼树会区分蒙皮骨、非蒙皮辅助骨和导出补全 Root，便于在导出前确认最终 FBX / UE 层级。
- FBX 导出保留完整源骨架，包括不参与蒙皮的 Root；修复部分街霸 6 角色导出后 Root 丢失、人物躺倒或前向不一致的问题。
- 合并多个模型分件时会综合各分件的父子关系补全 `Root → C_Hip`，不再由首个加载分件决定最终根层级。
- 对确实没有 Root 的源骨架，模型和动画统一补充不改变静止姿势与蒙皮的真实根骨，保持 X 向右、Y 向前、Z 向上的 UE 坐标约定。
- 优化右侧模型组 / 骨骼树页签字号与间距，并启用新版应用图标。

## v1.1.1 更新

- 搜索结果右键菜单新增“在目录树中定位”，可自动清空搜索、展开完整父目录并选中对应资源。
- 改善街霸 6 等资源数量较多游戏的目录浏览体验，便于确认搜索结果所在的真实资源路径。

## v1.1.0 更新

- 新增多 MotionList 批量导出：支持在资源搜索结果中使用 Ctrl / Shift 多选，或将全部搜索结果一次加入导出队列。
- 批量导出已整合到原有“动画导出”区域，每个 MotionList 中的动画仍保持单独 FBX 输出，并显示列表、动画和总任务进度。
- 单个 MotionList 导出失败时会记录到日志并继续处理后续列表。
- 优化动画导出队列布局，明确区分添加来源、队列内容和移除操作，并显示已加入的列表数量。
- 优化模型组操作：增加可见数量提示、整行勾选，并统一恢复默认、全部显示、全部隐藏、仅显示选中组和取景选中组的布局。
- 移除左侧重复的“打开 PAK 文件”大按钮，仍可通过文件菜单、拖放 PAK 或游戏目录扫描加载。

## v1.0.0 更新

- 统一视口与导出坐标约定：X 轴向右、Y 轴向前、Z 轴向上，并让左下角世界坐标轴随镜头同步。
- 调整默认透视观察角度、全部取景居中、鼠标环绕方向及空视口操作。
- 修复资源列表部分区域无法双击或右键、右键菜单命中范围不完整的问题。
- 修复 Blender 5.x 下动画导出读取 `Action.fcurves` 导致的报错。
- 修复模型与动画 FBX 的骨骼轴向、蒙皮对应、UE 导入方向和缩放。
- 修复动画根位移被重复转换后落到负 Z、在 UE 中表现为向下移动的问题。
- 模型导出包含当前预览中的可见合并分件；动画可选择导出当前项或整个 MotionList，并保持逐动画独立输出。
- 不再预置特定游戏的路径 list，首次启动保持干净，可在列表管理器中按需下载或导入。
- 发布包精简为单个 EXE，list、设置、日志、缓存和导出目录均在运行后统一生成到程序旁。

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
- 支持导出当前预览模型 FBX，以及当前动画、当前 MotionList 或多个 MotionList 的全部动画 FBX。
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

1. 在 GitHub Releases 下载最新的 Windows x64 压缩包；压缩包内只有 `ReExtractor.Gui.exe`。
2. 将 EXE 解压到一个可写目录并运行，程序会在同级目录自动生成运行所需文件夹。
3. 首次启动时检查环境；FBX 导出需要安装 Blender，并在设置中选择 `blender.exe`。
4. 在路径列表中选择或下载对应游戏的 list。
5. 选择游戏文件夹扫描 PAK，或直接拖入 / 打开 PAK 文件。
6. 双击资源进行预览。
7. 使用右键菜单把模型加入合并器，或把 MotionList 加入动画导出队列。
8. 使用右侧 FBX 导出面板导出模型、单个动画或批量动画。

## FBX 导出说明

- FBX 转换依赖 Blender，请在设置中选择 Blender 可执行文件。
- “导出预览模型 FBX”会导出当前预览中可见的全部模型分件。
- “导出动画 FBX”可导出当前动画、当前 MotionList 或批量列表中的全部动画；每个动画单独输出，不包含模型网格。
- 单列表导出使用源动画帧率和有效帧范围；多 MotionList 批量导出统一按 60 FPS 烘焙。
- 当前 FBX 坐标约定为 X 轴向右、Y 轴向前、Z 轴向上，并已针对 Unreal Engine 的模型、骨架和根运动流程进行修正。
- 不同 RE Engine 游戏和资源版本仍可能存在特殊骨架或动画数据，导入 DCC 或 Unreal Engine 后建议进行一次抽样检查。
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
