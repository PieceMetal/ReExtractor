# Skeleton merge regression tests

Run from the repository root with a .NET 10 SDK:

```powershell
dotnet run --project tools/MergeRegressionTests -c Release
```

The console runner calls the public `ViewportMesh.Merge` API with synthetic skinned
triangles. It checks bone subsets and additional bones, reordered source bone and
deform indices, case-insensitive names, restored parent chains, unchanged geometry
and skin weights, identity skinning at bind pose, and static mesh merging. It also
checks that conflicting binds or named parents are rejected, including conflicts
between the second and third parts that the first part cannot reveal.

The process exits with `0` when every case passes and `1` when any case fails.
No game assets, GUI, FBX converter, or extra test packages are required. These
synthetic cases cover the merge contract; they do not replace exports with actual
Street Fighter 6 or Monster Hunter Wilds assets.
