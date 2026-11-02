extends SceneTree

func _initialize() -> void:
    call_deferred("_check")

func _check() -> void:
    var scene: Node3D = load("res://scenes/eso_original_humanoid_601_assembly.tscn").instantiate()
    root.add_child(scene)
    for _i in range(4):
        await process_frame
    if scene.SourcePartCount != 8 or scene.SourceVertexCount != 6751 \
        or scene.MergedBoneCount != 110 \
        or scene.ExactSharedBoneReferences != 98 \
        or not scene.IsOriginalCustomizationRest:
        push_error("Original ESO eight-piece source-rest assembly metrics invalid")
        quit(1)
        return
    var skel: Skeleton3D = scene.get_node_or_null("OriginalMergedCustomizationSkeleton")
    if skel == null or skel.get_bone_count() != 110:
        push_error("Source 601 merged customization skeleton unavailable")
        quit(1)
        return
    var names: Array[String] = [
        "ArmsLower_601", "ArmsUpper_601", "Hands_601", "Torso_601",
        "Head_601", "LegsLower_601", "LegsUpper_601", "Feet_601"
    ]
    var counts: Array[int] = [90,218,554,1462,1443,230,2328,426]
    var binds: Array[int] = [4,10,14,22,38,10,8,6]
    for i in range(names.size()):
        var mesh: MeshInstance3D = skel.get_node_or_null("Original601_" + names[i])
        if mesh == null or mesh.mesh == null or mesh.skin == null:
            push_error("Missing original ESO 601 body component " + names[i])
            quit(1)
            return
        if mesh.mesh.surface_get_array_len(0) != counts[i] \
            or mesh.skin.get_bind_count() != binds[i]:
            push_error("Source 601 body geometry or native bind count changed " + names[i])
            quit(1)
            return
        if mesh.skeleton != NodePath("..") or mesh.material_override == null:
            push_error("Original 601 body component no longer attaches to common source rig")
            quit(1)
            return
        for bind in range(binds[i]):
            var bone_index: int = mesh.skin.get_bind_bone(bind)
            if bone_index < 0 or bone_index >= skel.get_bone_count():
                push_error("Original 601 part bound to invalid merged bone index")
                quit(1)
                return
            var delta: Transform3D = skel.get_bone_global_pose(bone_index) * mesh.skin.get_bind_pose(bind)
            if delta.origin.length() > 0.0002:
                push_error("Original 601 part lost exact source inverse rest matrix")
                quit(1)
                return
    if scene.OriginalHairVertexCount != 2491 or scene.OriginalHairBindCount != 10:
        push_error("Original native-compatible 601 hairstyle missing")
        quit(1)
        return
    var hair: MeshInstance3D = skel.get_node_or_null("OriginalHair_0029A87F")
    if hair == null or hair.mesh == null or hair.skin == null \
        or hair.mesh.surface_get_array_len(0) != 2491 \
        or hair.skin.get_bind_count() != 10:
        push_error("Source-haired original ESO humanoid failed to assemble")
        quit(1)
        return
    for bind in range(hair.skin.get_bind_count()):
        var bone: int = hair.skin.get_bind_bone(bind)
        if bone < 0 or bone >= skel.get_bone_count() \
            or (skel.get_bone_global_pose(bone) * hair.skin.get_bind_pose(bind)).origin.length() > 0.0002:
            push_error("Authentic hairstyle-to-head inverse rest became incompatible")
            quit(1)
            return
    if not scene.HasOriginalHairDdsCandidate or not scene.OriginalHairDdsVisible:
        push_error("Original source hairstyle candidate DDS material missing")
        quit(1)
        return
    var original_material: ShaderMaterial = hair.material_override as ShaderMaterial
    if original_material == null or original_material.shader == null \
        or original_material.shader.code.find("ALPHA_SCISSOR_THRESHOLD") < 0:
        push_error("Original ESO hairstyle alpha-cutout shader not active")
        quit(1)
        return
    for channel in ["original_diffuse","original_normal","original_specular"]:
        if not (original_material.get_shader_parameter(channel) is Texture2D):
            push_error("Original ESO hairstyle DDS missing: " + channel)
            quit(1)
            return
    var neutral_key := InputEventKey.new()
    neutral_key.keycode = KEY_T
    neutral_key.pressed = true
    scene._Input(neutral_key)
    if scene.OriginalHairDdsVisible or hair.material_override == original_material:
        push_error("Provisional original hair candidate did not toggle off")
        quit(1)
        return
    scene._Input(neutral_key)
    if not scene.OriginalHairDdsVisible or hair.material_override != original_material:
        push_error("Provisional original hair candidate did not restore")
        quit(1)
        return
    var camera: Camera3D = scene.get_node_or_null("Original601Camera")
    if camera == null or not camera.current:
        push_error("Original 601 native body camera missing")
        quit(1)
        return
    var previous: Vector3 = camera.position
    var wheel := InputEventMouseButton.new()
    wheel.button_index = MOUSE_BUTTON_WHEEL_UP
    wheel.pressed = true
    scene._Input(wheel)
    if camera.position.is_equal_approx(previous):
        push_error("Original humanoid 601 rest inspection zoom broken")
        quit(1)
        return
    print("PASS original ESO 601 full humanoid: 8 real body parts / 6751 vertices / original 2491-vertex hair (10/10 binds) / 110 merged customization bones / 98 identical source rests and parents; native skins; zoom; no guessed animation")
    quit(0)
