# RE引擎解包工具 - 项目长期笔记

## 项目身份（重要：勿混淆）
- 本项目 = **ReExtractor**：自研 RE 引擎解包工具，.NET 10 + Avalonia 11.3.2 GUI，
  入口 `src/ReExtractor.Gui`，构建用 `C:/Users/zzjhuang/.dotnet10/dotnet.exe`。
- **REEasy / seifhassine/REasy 是第三方工具**，仅借用其 `ONIWOTS_STM.list`（PAK 索引）。
  GUI 预览、贴图解码(TexService)、模型加载(ViewportMesh) 全是我们自己的代码，不是 REEasy。
- 易错点：曾两次把 ReExtractor 的 BUG 误说成 REEasy 的随机 bug。调试/修复时务必指 ReExtractor 源码。

## 关键文件
- `src/ReExtractor.Core/TexService.cs` — 贴图解码核心（**全部 BC1-7 统一 CreateIterator+BCnEncoder**；WIC 死路径已删）
- `src/ReExtractor.Core/ViewportMesh.cs` — 模型网格/viscon 加载（LoadMesh 含 viscon 组过滤）
- `src/ReExtractor.Gui/GlViewport.cs` — **GPU 3D 视口（当前唯一预览实现，ANGLE ES 3.0）**；Viewport3D 软件光栅已废弃
- `tools/texprobe*.cs` — .NET 10 文件级离线探针（`dotnet run tools/xxx.cs`），免开 GUI 直接从 PAK 验证 Core 链路
- 诊断子项目：`src/DiagTex` `src/DiagSkinning` `src/DiagMdf2`

## 钉死的技术事实（勿再反复推翻）
- **BC 系贴图解码**：BCnEncoder 2.3.0（BC1-7 含 BC7）对本作数据**逐像素正确**（与 Noesis 原生 DDS 解码 0.0% 差异验证过）。`System.Drawing.Bitmap` 本机**没有 DDS codec**，任何 DDS 都抛异常 → WIC 路线永远走不通，别再写。
- **预览贴图必须 streaming 优先**：`natives/stm/streaming/art/...`=2048 完整版；`natives/stm/art/...`=256 stub。stub 的 BC 颗粒在预览缩放下渲染成高频彩噪（2026-07-23 彩噪案终审结论）。fmt_RE_MESH 的路径解析就是 streaming 优先。同时软件光栅必须带 **trilinear mip 过滤**（每三角形常量 λ + 双 mip 混合），否则远景颗粒依旧。
- **viscon 组语义**：MeshGroup.groupId 是可见性状态组；同材质、面位置大比例重复的组 = 替身壳（ch001 组 9/10/250）。预览应做材质覆盖最小组集过滤（<128 为主）+ ≥128 组仅带新材质才留。**注意**：此过滤与彩噪无关（当时证伪），但语义本身正确；副作用是可能误丢共享材质的小部件（如组1 的 21 面），小部件缺失时先查这里。
- **渲染异常诊断顺序（实锤有效）**：别猜，建离线复刻探针逐级排除：①纯色模式干净→几何/深度没事 ②棋盘格强制贴图干净→采样器/UV 没事 ③面心 UV 平涂干净→面→贴图映射没事 ④解码与 Noesis 逐像素对比 → 剩下的就只有贴图内容/过滤。
- **启动坑**：XAML 里 `SelectedIndex="0"` 会在 InitializeComponent 触发 SelectionChanged，后置 x:Name 字段尚未接线 → NRE 静默退出（窗口不弹）。处理器必须空值守卫。
- **OpenGL ES 钉死**：这台机器 Avalonia 走的是 **OpenGL ES 3.0（ANGLE）**，shader 必须用 `#version 300 es` + `precision highp float;`，绝对不能写 `#version 330 core`——写了就白屏且 gl_log.txt 报 `'core' : invalid version directive`。
- **给用户看效果**：改完代码必须 build + PowerShell `Start-Process` 重启 GUI（bash 后台/nohup 会被 SIGHUP 带走）。

## 角色 ID 映射（鬼武者 WotS DEMO）
- 武藏 = 模型 `ch001_00` + 动画 `motion/gimmick/ch001/`
- 佐佐木 = 模型 `ch033_00` + 动画 `em503`
