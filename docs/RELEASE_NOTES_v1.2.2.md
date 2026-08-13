# ReExtractor v1.2.2

本版本新增资源多选、批量贴图导出，并在导出角色模型时自动收集 MDF 引用的贴图。

## 主要更新

- 资源目录树和搜索结果支持 Ctrl 追加选择、Shift 连续范围选择。
- 右键菜单可将选中的多个 TEX 批量导出为 PNG。
- 贴图输出保留原资源目录结构，避免同名文件相互覆盖。
- 导出角色模型 FBX 时，自动解析所有已加载分件的 MDF，并导出其引用的有效 TEX 贴图。
- 模型相关贴图导出不需要事先点击“加载贴图”。
- 自动跳过 NullWhite、NullBlack 等占位贴图，单张失败会记录到日志并继续处理其他贴图。

## 下载与环境

- 发布包：`ReExtractor-v1.2.2-win-x64.zip`
- 支持 Windows 10 / 11 x64。
- 压缩包内仅包含 `ReExtractor.Gui.exe`。
- FBX 导出需要单独安装 Blender；直接批量导出 TEX 为 PNG 不需要 Blender。
