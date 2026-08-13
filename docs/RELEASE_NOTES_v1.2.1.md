# ReExtractor v1.2.1

本版本重点改善多分件模型的动画播放、DMC5 贴图与特殊材质预览，并补全 Blender 自动探测。

## 主要更新

- 修复合并或叠加多个模型分件后，动画不同步和身体变形的问题。
- 支持 DMC5 `natives/x64` 资源根、`.tex.11` 纹理版本及 `BaseMetalMap` / `BaseShiftMap` 材质通道。
- 改善头发、胡须、眉毛、睫毛、眼球及眼部覆盖层的预览效果。
- 修复小窗口下工具栏越界和动画选择器过宽的布局问题。
- 切换动画时自动从第 0 帧重新播放。
- 首次运行或已配置路径失效时，自动从 Steam 库、Program Files、Microsoft Store 和 `PATH` 中查找 Blender。

## 下载与环境

- 发布包：`ReExtractor-v1.2.1-win-x64.zip`
- 支持 Windows 10 / 11 x64。
- 压缩包内仅包含 `ReExtractor.Gui.exe`；程序首次运行后会在同级目录生成运行数据。
- FBX 导出仍需单独安装 Blender。
