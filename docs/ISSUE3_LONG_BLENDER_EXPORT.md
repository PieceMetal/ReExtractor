# Issue #3: long Blender batch conversions

Base: `25d168c` (v1.4.19). Issue: https://github.com/PieceMetal/ReExtractor/issues/3

## Change

`RunBlenderBatch` no longer kills Blender after a fixed 3,600,000 ms. A single
MotionList may legitimately exceed one hour. It now waits for the actual process
exit, with stdout and stderr drained concurrently. Existing progress parsing,
exit-code and completion-marker checks, conversion logs, and batch failure handling
are unchanged. No output cleanup or retry logic was added.

This applies to callers of the shared model/animation FBX conversion helper. The
separate 180-second preview helper is unchanged.

## Verification

- `dotnet build src/ReExtractor.Gui/ReExtractor.Gui.csproj -c Release`: passed,
  0 errors; one pre-existing CS0649 warning in REE-Lib.
- `dotnet run --project tools/BlenderBatchTests -c Release`: passed.
  Tests use a fake Blender executable, stream approximately 0.5 MB to each pipe,
  delay exit, and verify final progress, complete logs and preservation of an
  existing output file for success, missing completion marker and exit code 7.
- Source regression guard checks that the actual GUI conversion helper waits
  without the old elapsed-time kill. This is NOT a real one-hour Blender test.

## Remaining manual acceptance and limitations

No real game conversion lasting longer than 60 minutes has been run. Before a
release, use the original large MotionList and confirm FBX output continues beyond
60 minutes and eventually completes, checking the generated files in Blender.

The current UI has no export cancellation button. Progress updates only arrive
when the conversion script emits its existing per-item progress marker, so a large
single animation can look idle. A genuinely hung Blender process will now wait
indefinitely; this change is not a hang detector. Closing the application is not
a guaranteed child-process cancellation mechanism. If manual termination is
necessary, terminate only the specific export Blender process, not unrelated
Blender sessions; incomplete output may require re-export. The application will
then observe a conversion failure if it is still running. A dedicated cooperative
cancel/child-process lifecycle design is separate work, not silently added here.

No existing exports were touched. Initial repair was local only. The owner subsequently
authorized publication and an issue reply; see RELEASE_NOTES_v1.4.20.md for the release scope.
