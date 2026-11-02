extends SceneTree

func _initialize() -> void:
    call_deferred("_run")

func key(scene: Node3D, code: Key) -> void:
    var event := InputEventKey.new()
    event.keycode = code
    event.pressed = true
    scene._Input(event)

func _run() -> void:
    var scene: Node3D = load("res://scenes/eso_original_creature_walk_gallery.tscn").instantiate()
    root.add_child(scene)
    for frame in range(4):
        await process_frame
    var names: Array[String] = ["Clannfear", "Daedroth", "Guar"]
    var vertex_counts: Array[int] = [2424, 7716, 4876]
    var binding_counts: Array[int] = [54, 52, 52]
    var bone_counts: Array[int] = [104, 102, 113]
    if scene.OriginalCreatureCount != 3:
        push_error("Source gallery did not load all three original creatures")
        quit(1)
        return
    var creatures: Array[Node3D] = []
    for i in range(3):
        if scene.GetCreatureName(i) != names[i]:
            push_error("Wrong original creature identity at index " + str(i))
            quit(1)
            return
        var creature: Node3D = scene.get_node_or_null("SourceCreature_" + names[i])
        if creature == null or not creature.visible:
            push_error("Original creature missing in gallery: " + names[i])
            quit(1)
            return
        var skeleton: Skeleton3D = creature.get_node_or_null("NativeCreatureSkeleton")
        var mesh: MeshInstance3D = creature.get_node_or_null("NativeCreatureSkeleton/OriginalCreatureMesh")
        if skeleton == null or mesh == null or mesh.mesh == null or mesh.skin == null:
            push_error("Original creature skeleton/skin missing: " + names[i])
            quit(1)
            return
        if skeleton.get_bone_count() != bone_counts[i] \
            or mesh.skin.get_bind_count() != binding_counts[i] \
            or mesh.mesh.surface_get_array_len(0) != vertex_counts[i]:
            push_error("Original source mesh, native weights or bones changed: " + names[i])
            quit(1)
            return
        creatures.append(creature)
    # Optional local provenance manifest is deliberately ignored by Git.
    # When present, the UI must actually decode source DDS for the mapped
    # species and let us compare it with the neutral diagnostic material.
    if FileAccess.file_exists("res://data/eso_creature_texture_candidates.tsv"):
        if scene.CandidateMaterialCount != 2 or scene.HasCandidateMaterial(0) \
            or not scene.HasCandidateMaterial(1) or not scene.HasCandidateMaterial(2):
            push_error("Local original creature DDS mapping was not loaded correctly")
            quit(1)
            return
        var guar_mesh: MeshInstance3D = creatures[2].get_node("NativeCreatureSkeleton/OriginalCreatureMesh")
        var source_material: StandardMaterial3D = guar_mesh.material_override
        if source_material.albedo_texture == null or not source_material.normal_enabled \
            or source_material.normal_texture == null:
            push_error("Original source Guar DDS diffuse/packed normal did not load")
            quit(1)
            return
        key(scene, KEY_T)
        if scene.TextureCandidatesVisible or guar_mesh.material_override == source_material:
            push_error("Texture candidate toggle did not return to diagnostic material")
            quit(1)
            return
        key(scene, KEY_T)
        if not scene.TextureCandidatesVisible or guar_mesh.material_override != source_material:
            push_error("Texture candidate toggle did not restore original DDS")
            quit(1)
            return
    var camera: Camera3D = scene.get_node_or_null("CreatureGalleryCamera")
    if camera == null or not camera.current:
        push_error("Original creature gallery camera missing")
        quit(1)
        return
    key(scene, KEY_2)
    for i in range(3):
        if creatures[i].visible != (i == 1):
            push_error("Original creature focus selection wrong")
            quit(1)
            return
    key(scene, KEY_0)
    for creature in creatures:
        if not creature.visible:
            push_error("Original creature gallery compare-all failed")
            quit(1)
            return
    key(scene, KEY_SPACE)
    var frame_before: int = scene.GetFrame(0)
    var bone_before: Vector3 = scene.GetWorldBone(0, "mshBn_head")
    for _f in range(5):
        await process_frame
    if scene.GetFrame(0) != frame_before:
        push_error("Pausing original creature animation failed")
        quit(1)
        return
    for _i in range(5):
        key(scene, KEY_RIGHT)
    if scene.GetFrame(0) == frame_before:
        push_error("Stepping native creature animation failed")
        quit(1)
        return
    if bone_before.is_equal_approx(scene.GetWorldBone(0, "mshBn_head")):
        push_error("Native Clannfear head did not move in sampled Walk clip")
        quit(1)
        return
    var camera_before: Vector3 = camera.position
    var zoom := InputEventMouseButton.new()
    zoom.button_index = MOUSE_BUTTON_WHEEL_UP
    zoom.pressed = true
    scene._Input(zoom)
    if camera.position.is_equal_approx(camera_before):
        push_error("Original creature camera zoom failed")
        quit(1)
        return
    print("PASS original ESO creature gallery UI: 3 native source meshes, exact skin bindings, focus/all, pause/frame stepping, moving head and camera zoom")
    quit(0)
