# Rise animation regression

Integration checks for the actual `GlViewport` pose evaluator and merged export path. Requires a local Monster Hunter Rise installation and its matching path list; no game files or generated assets are included in the repository.

```powershell
dotnet run --project tools/RiseAnimationRegressionTests -c Release -- --game-dir "<Rise directory>" --list "<MHR list path>" --out artifacts/rise-regression
```

Checks male/female `pl004` equipment against the full player skeleton, individual parts, alternate model order, source inverse-bind preservation and eight successive dual-blade clips. A failure returns a nonzero exit code. `--baseline` records original failures without failing the process; use it only when collecting a pre-fix comparison.

The output includes expected viewport vertices at frame 92, assembled animated GLBs, static model GLBs and animation-export bridge files. Use `--unity` when generating matching Unity-axis model and animation bridge files.

```powershell
blender -b --factory-startup --python-exit-code 1 --python tools/RiseAnimationRegressionTests/verify_export.py -- artifacts/rise-regression
```

This imports the GLBs into Blender and compares evaluated skin vertices with the viewport samples. Unbound importer bone-shape meshes are excluded.

For a separate FBX round trip, run the audit with `--unity`, convert its `m-model.glb` and `m-animation/*.glb` using `tools/export_models_fbx.py` and `tools/export_animations_fbx.py` with `REEXTRACTOR_FBX_TARGET=unity`, then run:

```powershell
blender -b --factory-startup --python-exit-code 1 --python tools/RiseAnimationRegressionTests/verify_fbx.py -- "<Unity audit output>" "<model.fbx>" "<animation.fbx>"
```

The FBX check requires identical model/animation bone paths, one model mesh, no animation meshes, and a matching sampled skin pose. These checks do not replace an actual Unity import test.
