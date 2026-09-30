# Animation usage audit

Local-only audit of the user's game resources. Game data is not committed or distributed.
Run from the repository root. Input is a JSON array of objects with `Name`,
`GameDirectory`, `ListPath`, and `Output` (see local artifacts for this run).

`dotnet run --project tools/AnimationInventoryAudit -- <inputs.json> --inventory-only`
enumerates resolved PAK paths. Omit the flag to decode embedded motions and banks;
`--headers-only` checks list/MOT headers without decoding curve or event payloads.
Each complete list is a JSONL checkpoint. Do not run two writers on the same output.
Record header-only, error, external reference, and unresolved path coverage separately.
Use a bounded heap for full scans of unfamiliar formats; malformed resources can
consume excessive memory in the third-party parser. A failed parse is not a bad-animation verdict.

RE4 configuration evidence is produced with
`dotnet run --project tools/Re4AnimationAudit -- --fsm-sweep`.
Then run `extract_player_usages.py` and `build_usage_registry.py` with Python in UTF-8 mode.
They currently use the local `artifacts/preview-fix/full-audit/RE4` input directory.
`summarize_audit.py` and `write_report.py` summarize this run, including gaps.

The product registry covers possible uses in the audited ch0a0z0 player context:
ordered prefab layers -> enabled FSM actions -> explicit bank IDs -> cha0 motbanks
-> exact resource, motion number, and name. The generator allows direct AppPlayMotion,
explicit optional layers, the audited weapon put-out selection, and hand overwrite.
It does not infer opaque custom actions or dynamic bank aliases. Each entry retains
its bank/FSM/node evidence. Override does not mean complete-body animation.
Multiple layer uses become ContextDependent; uncovered records remain Unknown.
Names with `_add` are hints, not configuration confirmation.

The usage registry does not add preview pairings, masks, runtime weights, IK, or
exported baked composition. Live RE4 player previews resolve pairings from the loaded
PAK through `Re4AnimationLayerResolver`; the embedded pair registry is an offline
regression fixture. Do not extend a motion-number rule to another list or game.

The GUI regression suite checks resource isolation, internal usage classification,
removal of usage labels, raw-only lists, filtering and export indices. Native skinning checks
cover representative RE4 combinations only, not the entire inventory.
# 同状态配对提取

`python tools/AnimationInventoryAudit/build_pair_registry.py` 从已完成的RE4资源审查提取当前wp4000H列表配对。读取同一启用状态的两层动作和祖先节点遮罩，核对动作库及内嵌动作；生成17组预览记录和76条逐项审查结果。不要根据相邻编号或名称猜测新增配对。

## r10 跨列表验证（2026-09-29）

实际预览读取玩家 Prefab、启用的共用上半身 FSM、动作库引用和 JointMap，按当前资源的完整路径与动作编号解析配对，不回退到单列表快照。选择支持的主动作时自动启用配对附加层，仍可选择“不叠加”。

`dotnet run --project tools/Re4AnimationAudit -- --runtime-pairs` 使用本地 RE4 安装及路径表，检查配置引用列表与配对的全帧混合数据。当前验证47份相关列表，30份含可读取配对，共214组，0个配对错误。输入目录目前在该工具的 `Program.cs` 中配置；运行前须改为本机合法安装及路径表位置。

`dotnet run --project tools/AnimationPreviewUiTests` 检查筛选、默认配对、原始轨道与失败恢复；需要本地审查生成的 `artifacts/re4-audit/motions.json`。附加 `--session-dropdowns <session.json>` 可经实际加载入口遍历列表，附加 `--session <session.json>` 可检查完整三分件的逐帧蒙皮与加载顺序。会话字段见 GUI 的 `MainWindow.PreviewSession.cs`，需要本地游戏和模型资源。

wp4100h、wp4200h、general2 三份列表共298条主动作通过实际加载检查；其中 wp4100h 和 general2 共32组通过完整模型逐帧检查。仅代表画面完成离屏查看，214组的数值检查不等于逐组视觉验收。

范围限定 RE4 共用玩家上半身配置。跨列表基础动作继承、外部动作引用、移动基础层、手型覆写、动态权重与 IK 尚未完整支持；其他角色和游戏不在本次验证范围。RSZ 元数据依赖 REE-Lib 缓存或下载，干净机器离线运行未验收。本次为本地测试版本，尚未完成正式发布回归。
