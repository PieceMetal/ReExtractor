# ReExtractor

轻量级 RE 引擎资源提取器：解包 → 预览 → 导出 UE5 可用的 FBX。

**定位**：不做 Mod、不做编辑。只做"从 PAK 里把模型/动画/贴图拿出来，变成 DCC/UE 能用的文件"。
交互形态对标 ree-pak-gui（轻、快、专注提取），解析核心复用 REE-Content-Editor 的底层库 RE-Engine-Lib。

## 首个服务目标

《鬼武者：剑之道》DEMO（OWOTS）：
- 武藏 `natives/stm/art/model/character/ch0/ch001_00/`
- 佐佐木 `natives/stm/art/model/character/ch0/ch033_00/` + 动画 `natives/stm/motion/enemy/em503/`
- 产出：带骨骼+动画的 FBX，可直接导入 UE5。

## 技术栈

| 层 | 选型 | 说明 |
|---|---|---|
| 解析核心 | RE-Engine-Lib（kagenocookie，MIT） | PAK / .mesh / .tex / .motlist 解析，不重复逆向 |
| 语言/框架 | C# / .NET 10（REE-Lib 需 C# 14 语法） | 本机另装 .NET 10 SDK 于 ~/.dotnet10 |
| UI（二期） | Avalonia + 3D 视口 | 轻量壳：PAK拖入→文件树→预览→一键导出 |
| 导出 | FBX（模型+骨骼+动画）；贴图 PNG/DDS | 动画链 motlist→glTF→FBX（Blender headless 兜底） |

## 里程碑

- [x] M0：解决方案骨架 + CLI 实读 OWOTS PAK（含 patch 优先级、list 路径还原）
- [x] M1：.tex 预览与 PNG 导出（CLI tex2png，BC1-7+非压缩格式，真实贴图验证通过）
- [x] M2：.mesh 解析 + glTF 静态导出（CLI mesh2glb，武藏4.6万顶点验证通过）
- [x] M3：骨骼+蒙皮导出（GLB含骨架+蒙皮，Blender验证267骨/22蒙皮网格）
- [x] M4：.motlist 动画解析 + 动画 glTF 导出（CLI anim2glb，佐佐木basemove 1116帧验证通过；FBX可经Blender/UE直接转换）
- [x] M5：Avalonia GUI（加载PAK/搜索/tex图片预览/mesh+动画Blender渲染预览/一键导出，dark主题）

## 结构

```
libs/RE-Engine-Lib   # 解析核心（浅克隆，含 1 处 C#14→经典写法兼容补丁：BhvtFile.cs NodeName）
src/ReExtractor.Core # PakService：多PAK优先级 + list路径还原 + 提取
src/ReExtractor.Cli  # M0 验证工具：stats / find / extract
```

## CLI 用法

```
dotnet run --project src/ReExtractor.Cli -- --game-dir "E:\Steam\steamapps\common\OnimushaWotS_Demo" \
  --list "D:\OnimushaWotS_Demo_Tools\REasy_v0.7.3\resources\data\lists\ONIWOTS_STM.list" stats
```
