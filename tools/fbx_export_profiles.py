import bpy,math,os
from mathutils import Matrix

def bake_unity_axes():
    # Rotate scene coordinates from Blender Z-up/-Y-forward to Y-up/+Z-forward.
    basis=Matrix.Rotation(-math.pi/2,4,'X')
    objects=[o for o in bpy.context.scene.objects if o.type in {'MESH','ARMATURE'}]
    worlds={o:o.matrix_world.copy() for o in objects}
    for o in objects:
        o.parent=None
        o.matrix_world=basis @ worlds[o]
    bpy.ops.object.select_all(action='DESELECT')
    for o in objects:o.select_set(True)
    if objects:
        bpy.context.view_layer.objects.active=objects[0]
        bpy.ops.object.transform_apply(location=True,rotation=True,scale=True)
    for o in objects:
        if o.type=='MESH':
            arms=[m.object for m in o.modifiers if m.type=='ARMATURE' and m.object]
            if arms:o.parent=arms[0]


def export_target():
    target = os.environ.get("REEXTRACTOR_FBX_TARGET", "ue").lower()
    if target not in {"ue", "unity"}:
        raise ValueError("Unknown FBX export target: " + target)
    return target


def prepare_export_axes():
    if export_target() == "unity":
        bake_unity_axes()
        # Unity strips a sole top-level armature from animation-only imports.
        # A shared, non-rendering sibling preserves identical track paths in
        # model and animation files without embedding any mesh in animations.
        marker = bpy.data.objects.new("AnimationHierarchy", None)
        bpy.context.scene.collection.objects.link(marker)
        marker.select_set(True)


def axis_options():
    return {"axis_forward": "-Z" if export_target() == "unity" else "Y",
            "axis_up": "Y" if export_target() == "unity" else "Z",
            "use_space_transform": False}
