# ch004 完整角色面部回归

实际调用 GlViewport 的蒙皮计算，不启动或控制用户窗口。

- `dotnet run --project tools/MergedFacePreviewTests -c Release`
- 导出 GLB 后运行 `tools/export_animations_fbx.py`，输入 `artifacts/merged-face-proof/glb`，输出 `artifacts/merged-face-proof/fbx`，60 FPS。
- 用 Blender 执行 `tools/MergedFacePreviewTests/check_export.py`，要求进程失败码检测及 export-check.json 结果存在。

样本：ch004 的 00、10、20 三个分件，npc004_00_common 第 0 段 mot_100。
预览检查 62 个时间点；GLB 和 FBX 对照单独头部的 0、2.52、22.3、44.59 秒顶点最近距离；通过静止眼骨与 Head 定义固定坐标基准、按静止眼距归一化，阈值为眼距的 0.2%（不按动画帧重新对齐）。
测试需要本地正版游戏文件，不随包分发资源。最近距离比较不覆盖骨骼名称、拓扑和其他角色。
