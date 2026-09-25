# RE2 RT animation regression audit

Uses a local RE2 RT installation and its matching path list. No game data is included.

```powershell
dotnet run --project tools/Re2AnimationRegressionTests -- "<game-directory>" "<RE2_RT_STM_Release.list>" "<output-directory>"
```

Exercises 62 real base movement clips on pl0000 at 17 samples per clip using the OpenGL viewport's actual local-transform evaluator. Checks finite skinned vertices and body bounds; excludes the known `dummy` placeholder. Verifies rejection of both figure-face and both weapon clips on the body skeleton, both export paths, and idle/face-rejection/walk/idle switching without residual pose or skeleton mutation. Figure-body world placement must remain unchanged. Writes audit.txt, sample pose geometry, and original-coordinate idle/walk GLBs.

These checks detect large deformation and mapping regressions. They do not establish exact agreement with every in-game pose or support split facial animation composition.

To audit all resolved pl00 player animation lists, append `--sweep`. This covers embedded motions in all 158 known lists, samples each accepted motion at nine times, and writes `sweep.json` plus status counts. External-link-only lists are reported separately; this tool does not resolve their motion-bank references. `PASS_SAMPLES` means finite vertices and bounded body size at sampled times, not exact in-game equivalence. Rejected partial rigs and placeholder clips require interpretation, not automatic classification as corrupt assets. Root movement is retained and does not count as deformation.

The focused audit also checks a real damage clip's non-unit `r_leg_side_muscle` scale against its original values, both viewport evaluators, actual skinned vertex displacement, and exact binary scale keys in both GLB export paths. Scale support is enabled for verified MOT 492 (RE RT) assets. The sweep includes scale-key counts and time ordering after this fix.
