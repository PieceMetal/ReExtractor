# Base motion-list inheritance regression

Run the synthetic regression without game files:

```powershell
dotnet run --project tools/ExternalMotionTests
```

Run the real Onimusha: Way of the Sword case with a local installation and its matching `OWOTS_STM_Release.list`:

```powershell
dotnet run --project tools/ExternalMotionTests -- --real "<game-directory>"
```

The real test reproduces the empty embedded-MOT result in `em101_00_01_damage.motlist.1036`. This list has 148 null slots and explicitly inherits `em100_00_01_damage` through `BaseMotListPath`. The variant's `em101_00.motbank.4` registers 17 lists; it does not contain the motion payloads.

Checks include ID and switch matching, retained source indices, local overrides, multi-level inheritance, missing/cyclic/corrupt/ambiguous dependencies, and stream cleanup. The real regression compares every inherited damage clip's keyframes and timing with its original base clip on the ch101 skeleton, exports and reloads all 148 GLBs, and lists the 16 non-tree battle lists in the variant. Game files are not bundled; generated files go to `artifacts/oni-external-motion/`.

The GUI regression uses the existing preview-session JSON format (`ListPath`, `GameDirectory`, `MeshPaths`, `MotionListPath`, `BaseId`):

```powershell
dotnet run --project tools/ExternalMotionUiTests -- "<preview-session.json>"
```

It exercises the actual GUI loading and motion-selection methods without opening a desktop window. It checks 148 dropdown entries, four selections, and twelve finite skinned poses. This is numerical and headless integration coverage, not visual playback acceptance or agreement with every in-game pose.

This fix resolves `BaseMotListPath` inheritance. Arbitrary motion trees, `MotFileLink` payloads and motpack references remain outside this change.
