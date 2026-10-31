extends SceneTree

func _initialize() -> void:
    call_deferred("_run")

func press(scene: Node, keycode: Key) -> void:
    var key := InputEventKey.new()
    key.pressed = true
    key.keycode = keycode
    scene._Input(key)

func _run() -> void:
    var scene: Node3D = load("res://scenes/eso_walk_comparison_lab.tscn").instantiate()
    root.add_child(scene)
    for i in range(3):
        await process_frame
    var names: Array[String] = [
        "GEN_Walk", "1H_Walk", "1HShield_Walk",
        "2HS_Walk", "2HL_Walk", "Bow_Walk"
    ]
    var nodes: Array[Node3D] = []
    for name in names:
        var part: Node3D = scene.get_node_or_null("SourceClip_" + name)
        if part == null or not part.visible:
            push_error("Missing authentic ESO walk figure: " + name)
            quit(1)
            return
        var renderer: MultiMeshInstance3D = part.get_node_or_null("OriginalSourceBones")
        if renderer == null or renderer.multimesh.instance_count != 21:
            push_error("Missing native source bone segments for " + name)
            quit(1)
            return
        var caption: Label3D = part.get_node_or_null("AnimationLabel")
        if caption == null or name not in caption.text:
            push_error("Original source clip label is missing: " + name)
            quit(1)
            return
        nodes.append(part)

    var camera: Camera3D = scene.get_node_or_null("ComparisonCamera")
    if camera == null or not camera.current:
        push_error("Missing comparison camera")
        quit(1)
        return
    press(scene, KEY_4)
    for i in range(6):
        if nodes[i].visible != (i == 3):
            push_error("Focus mode is displaying the wrong source clip")
            quit(1)
            return
    press(scene, KEY_0)
    for part in nodes:
        if not part.visible:
            push_error("Compare-all mode did not restore all original clips")
            quit(1)
            return
    press(scene, KEY_SPACE)
    var initial_frame: int = scene.GetDisplayedFrame(0)
    var original_foot: Vector3 = scene.GetDisplayedBonePosition(0, "mshBn_foot_L")
    for i in range(4):
        await process_frame
    if scene.GetDisplayedFrame(0) != initial_frame:
        push_error("Paused source animation continued changing its source frame")
        quit(1)
        return
    for i in range(3):
        press(scene, KEY_RIGHT)
    if scene.GetDisplayedFrame(0) <= initial_frame:
        push_error("Frame-step control did not advance the original source pose")
        quit(1)
        return
    if scene.GetDisplayedBonePosition(0, "mshBn_foot_L").is_equal_approx(original_foot):
        push_error("Stepping source frames did not move the original left foot")
        quit(1)
        return
    var prior: Vector3 = camera.position
    var wheel := InputEventMouseButton.new()
    wheel.button_index = MOUSE_BUTTON_WHEEL_UP
    wheel.pressed = true
    scene._Input(wheel)
    if camera.position.is_equal_approx(prior):
        push_error("Camera zoom control failed")
        quit(1)
        return
    print("PASS original ESO six-clip comparison: names, native 21-segment anatomies, focus, pause, source pose frame stepping, camera zoom")
    quit(0)
