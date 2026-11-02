extends SceneTree

func _initialize() -> void:
    call_deferred("_test")

func _test() -> void:
    var scene: Node3D = load("res://scenes/eso_original_hybrid_arms_lab.tscn").instantiate()
    root.add_child(scene)
    for _i in range(8):
        await process_frame
    if scene.OriginalArmMeshes != 2 or scene.AnimatedOriginalJointCount != 13 \
        or scene.AnimatedSkinBindings != 16 \
        or scene.UnmappedCustomizationSkinBindings != 8 \
        or scene.MaximumNativeWorldError > 0.003:
        push_error("Original hybrid arm animation assembly identity mismatch")
        quit(1)
        return
    var bones: Array[String] = ["ArmsLower_062","ArmsLower_061"]
    var vertices: Array[int] = [528,740]
    var skins: Array[Skeleton3D] = []
    var originals: Array[Transform3D] = []
    for i in range(2):
        var skeleton: Skeleton3D = scene.get_node_or_null(
            "Original_" + bones[i] + "_HybridRig")
        if skeleton == null or skeleton.get_bone_count() != 30:
            push_error("Original 30-joint source hybrid arm rig missing")
            quit(1)
            return
        var source: MeshInstance3D = skeleton.get_node_or_null(
            "Original_" + bones[i] + "_Skin")
        if source == null or source.mesh == null or source.skin == null \
            or source.mesh.surface_get_array_len(0) != vertices[i] \
            or source.skin.get_bind_count() != 12:
            push_error("Original Granny source arm mesh or skin changed")
            quit(1)
            return
        var custom: int = skeleton.find_bone("arm_4_L")
        if custom < 0:
            push_error("Actual arm_4_L customization control absent")
            quit(1)
            return
        skins.append(skeleton)
        originals.append(skeleton.get_bone_pose(custom))
    var pause := InputEventKey.new()
    pause.pressed = true
    pause.keycode = KEY_SPACE
    scene._Input(pause)
    var next := InputEventKey.new()
    next.pressed = true
    next.keycode = KEY_RIGHT
    var previous: int = scene.CurrentFrame
    scene._Input(next)
    if scene.CurrentFrame != (previous + 1) % 70:
        push_error("Original native walking frame stepping failed")
        quit(1)
        return
    for i in range(2):
        var custom: int = skins[i].find_bone("arm_4_L")
        if not skins[i].get_bone_pose(custom).is_equal_approx(originals[i]):
            push_error("Original customization arm control was remapped or animated")
            quit(1)
            return
    var cam: Camera3D = scene.get_node_or_null("OriginalHybridArmsCamera")
    if cam == null or not cam.current:
        push_error("Original source arm preview camera unavailable")
        quit(1)
        return
    var initial: Vector3 = cam.position
    var wheel := InputEventMouseButton.new()
    wheel.button_index = MOUSE_BUTTON_WHEEL_UP
    wheel.pressed = true
    scene._Input(wheel)
    if cam.position.is_equal_approx(initial):
        push_error("Original animated arm inspection camera zoom broken")
        quit(1)
        return
    print("PASS original ESO hybrid arm UI: 2 real source meshes; 13 true motion bones each; 8 animated and 4 untouched customization bindings each; native GEN_Walk step/pause, camera zoom, 30-bone skins")
    quit(0)
