# ReExtractor v1.3.7

本版本修复流式贴图的全分辨率预览与导出。

## 修复

- 修复《怪物猎人：荒野》等游戏直接预览或导出 TEX 时，只读取 64×64 / 256×256 低清常驻贴图的问题。
- 贴图预览、单张导出、批量 PNG 导出及 CLI `tex2png` 统一优先读取 `streaming` 路径中的全分辨率资源。
- 导出 PNG 保持源 streaming TEX 的原始分辨率，不会将 2K / 4K 贴图缩小为预览尺寸。
- 对没有 streaming 副本的旧游戏和普通 TEX 自动回退到原路径。

## 发布包

- Windows x64：`ReExtractor-v1.3.7-win-x64.zip`
- ZIP 内仅包含 `ReExtractor-v1.3.7.exe`。
