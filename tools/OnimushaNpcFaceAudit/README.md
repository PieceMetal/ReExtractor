# OniWS facial asset regressions

Requires the installed OnimushaWotS assets and `OWOTS_STM_Release.list` at the path in Program.cs. No game files are bundled.

Run sequentially from the repository root:

1. `dotnet run --project tools/OnimushaNpcFaceAudit -- --inventory`
2. `dotnet run --project tools/OnimushaNpcFaceAudit -- --mapping`
3. `dotnet run --project tools/OnimushaNpcFaceAudit -- --full`
4. `dotnet run --project tools/OnimushaNpcFaceAudit -- --regression`
5. `dotnet run --project tools/OnimushaNpcFaceAudit -- --exports`

Use Blender in background mode with `render_full.py` for representative start/middle/end views. It uses inspection cameras and untextured heads, not in-game shots. To verify FBX, run the production `tools/export_animations_fbx.py` on `artifacts/onimusha-all-faces/route-main` with output `route-fbx` and FPS 60, then run `check_routes.py`. Require the final JSON result, not only Blender's exit code (Blender can return zero after a Python assertion).

`--full` samples the first nonempty list per character and category (cinematic, talk, common), checking every embedded motion in those lists. It does not claim exhaustive coverage of all game motions. Character/enemy pairing comes from actual prefab mesh references. `plw_` clips are player reactions and are tested separately as absolute tracks.

See `docs/ONIMUSHA_NPC_FACE_FIX_20260918.md` for exact coverage and exclusions. `--others`, `--bones`, and `--dumps` are diagnostic modes; their earlier broad warnings are not final pass/fail results.
