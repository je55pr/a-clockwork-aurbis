extends SceneTree

func _initialize() -> void:
    call_deferred("_check")

func key(scene: Node3D, code: Key) -> void:
    var event := InputEventKey.new()
    event.keycode = code
    event.pressed = true
    scene._Input(event)

func _check() -> void:
    var scene: Node3D = load("res://scenes/eso_original_creature_crowd_lab.tscn").instantiate()
    root.add_child(scene)
    for _i in range(4):
        await process_frame
    if scene.TotalCreatureInstances != 300 or scene.UniqueCreatureMeshes != 3 \
        or scene.SharedOriginalMeshCopies != 297:
        push_error("Creature crowd geometry sharing / instance count invalid")
        quit(1)
        return
    var names: Array[String] = ["Clannfear","Daedroth","Guar"]
    var bone_counts: Array[int] = [104,102,113]
    var primary: Array[MeshInstance3D] = []
    for i in range(names.size()):
        var name := names[i]
        var source: Node3D = scene.get_node_or_null("SourceCreature_" + name)
        if source == null:
            push_error("Missing primary source creature " + name)
            quit(1)
            return
        var source_mesh: MeshInstance3D = source.get_node_or_null("NativeCreatureSkeleton/OriginalCreatureMesh")
        primary.append(source_mesh)
        for copy in [1,2,5,30,99]:
            var clone: Node3D = scene.get_node_or_null("OriginalCrowd_" + name + "_%03d" % copy)
            if clone == null or not clone.visible:
                push_error("Missing visible source creature copy: " + name + str(copy))
                quit(1)
                return
            var rig: Skeleton3D = clone.get_node_or_null("NativeCreatureSkeleton")
            var mesh: MeshInstance3D = clone.get_node_or_null("NativeCreatureSkeleton/OriginalCreatureMesh")
            if rig == null or mesh == null or rig.get_bone_count() != bone_counts[i] \
                or mesh.mesh != source_mesh.mesh or mesh.skin != source_mesh.skin:
                push_error("Original creature crowd not sharing source mesh/skin: " + name + str(copy))
                quit(1)
                return
    var camera: Camera3D = scene.get_node_or_null("CreatureGalleryCamera")
    if camera == null or not camera.current:
        push_error("Crowd camera unavailable")
        quit(1)
        return
    key(scene,KEY_3)
    if scene.get_node("OriginalCrowd_Clannfear_001").visible \
        or not scene.get_node("OriginalCrowd_Guar_001").visible:
        push_error("Crowd focus did not hide other creature species")
        quit(1)
        return
    key(scene,KEY_0)
    if not scene.get_node("OriginalCrowd_Clannfear_001").visible:
        push_error("Crowd all-species focus failed")
        quit(1)
        return
    if FileAccess.file_exists("res://.research/npc-rigs/creature-texture-candidates.tsv"):
        var copy: MeshInstance3D = scene.get_node("OriginalCrowd_Guar_001/NativeCreatureSkeleton/OriginalCreatureMesh")
        var source_mat: Material = copy.material_override
        if source_mat == null or source_mat != primary[2].material_override:
            push_error("Crowd did not share source DDS candidate materials")
            quit(1)
            return
        key(scene,KEY_T)
        if copy.material_override == source_mat:
            push_error("T did not toggle source DDS material across crowd")
            quit(1)
            return
        key(scene,KEY_T)
        if copy.material_override != source_mat:
            push_error("T did not restore source DDS material across crowd")
            quit(1)
            return
    print("PASS original ESO crowd: 300 independent native creature rigs, 3 shared meshes/skins, 297 source geometry copies; camera, species focus and source DDS toggles")
    quit(0)
