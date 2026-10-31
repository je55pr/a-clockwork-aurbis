extends SceneTree

func _initialize() -> void:
    call_deferred("_run")

func _key(node: Node, code: Key) -> void:
    var event := InputEventKey.new()
    event.keycode = code
    event.pressed = true
    node._Input(event)

func _run() -> void:
    var scene: Node3D = load("res://scenes/source_rig_lab.tscn").instantiate()
    root.add_child(scene)
    for i in range(4):
        await process_frame
    var faces: Node3D = scene.get_node_or_null("CloseFaceComparison")
    var body: Node3D = scene.get_node_or_null("BodyDisplay")
    var camera: Camera3D = scene.get_node_or_null("RigLabCamera")
    if faces == null or body == null or camera == null:
        push_error("Rig lab missing face gallery/body/camera")
        quit(1)
        return
    if not faces.visible or body.visible:
        push_error("Rig lab should initially show the close-up faces")
        quit(1)
        return
    var visible_count := 0
    for variant in faces.get_children():
        if variant.visible:
            visible_count += 1
    if visible_count != 3:
        push_error("Expected all three face presets, got %d" % visible_count)
        quit(1)
        return
    var narrow: Node3D = faces.get_node("FaceVariant_1")
    var normal: Node3D = faces.get_node("FaceVariant_2")
    var broad: Node3D = faces.get_node("FaceVariant_3")
    for variant in [narrow, normal, broad]:
        if variant.get_node_or_null("ProvisionalFacialFeatures") == null:
            push_error("Face variant missing provisional features")
            quit(1)
            return
    var narrow_rig: Skeleton3D = narrow.get_node("Skeleton_000E2CFF")
    var normal_rig: Skeleton3D = normal.get_node("Skeleton_000E2CFF")
    var broad_rig: Skeleton3D = broad.get_node("Skeleton_000E2CFF")
    var n := narrow_rig.find_bone("H")
    var c := normal_rig.find_bone("H")
    var w := broad_rig.find_bone("H")
    if not (narrow_rig.get_bone_pose_scale(n).x < normal_rig.get_bone_pose_scale(c).x
            and broad_rig.get_bone_pose_scale(w).x > normal_rig.get_bone_pose_scale(c).x):
        push_error("Three source skull shape presets are not distinct")
        quit(1)
        return
    _key(scene, KEY_2)
    if not normal.visible or narrow.visible or broad.visible:
        push_error("Single-face focus failed")
        quit(1)
        return
    _key(scene, KEY_0)
    if not narrow.visible or not normal.visible or not broad.visible:
        push_error("Face comparison restore failed")
        quit(1)
        return
    _key(scene, KEY_B)
    if faces.visible or not body.visible:
        push_error("B did not switch to body and gait")
        quit(1)
        return
    _key(scene, KEY_SPACE)
    for i in range(7):
        await process_frame
    var torso: Skeleton3D = body.get_node("Skeleton_000DEF03")
    var thigh := torso.find_bone("defBn_thigh_L")
    var current := torso.get_bone_pose_rotation(thigh)
    var rest := torso.get_bone_rest(thigh).basis.get_rotation_quaternion()
    if current.angle_to(rest) < 0.005:
        push_error("Gait does not move an anatomical thigh hinge")
        quit(1)
        return
    _key(scene, KEY_SPACE)
    var reset := torso.get_bone_pose_rotation(thigh)
    if reset.angle_to(rest) > 0.001:
        push_error("Stopping the gait does not restore the bind pose")
        quit(1)
        return
    _key(scene, KEY_F)
    if not faces.visible or body.visible:
        push_error("F did not return to close-up faces")
        quit(1)
        return
    print("PASS ESO rig lab: close-up three-face gallery, focused faces, body mode, upright gait and reset")
    quit(0)
