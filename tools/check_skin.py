import bpy, sys
argv = sys.argv[sys.argv.index("--") + 1:]
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=argv[0])
arms = [o for o in bpy.data.objects if o.type == 'ARMATURE']
meshes = [o for o in bpy.data.objects if o.type == 'MESH']
print(f"[skin] armatures={len(arms)} meshes={len(meshes)}")
for a in arms:
    print(f"[skin] armature bones={len(a.data.bones)}")
skinned = sum(1 for m in meshes if any(mod.type == 'ARMATURE' for mod in m.modifiers))
print(f"[skin] skinned_meshes={skinned}")
