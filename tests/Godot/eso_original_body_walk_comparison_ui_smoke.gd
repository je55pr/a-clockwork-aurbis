extends SceneTree

func _initialize() -> void:
    call_deferred("_run")

func send_key(scene: Node, code: Key) -> void:
    var input := InputEventKey.new()
    input.keycode = code
    input.pressed = true
    scene._Input(input)

func _run() -> void:
    var scene: Node3D = load("res://scenes/eso_original_body_walk_comparison.tscn").instantiate()
    root.add_child(scene)
    for i in range(4):
        await process_frame
    var labels: Array[String] = [
        "GEN_Walk", "1H_Walk", "1HShield_Walk",
        "2HS_Walk", "2HL_Walk", "Bow_Walk"
    ]
    if scene.OriginalFigureCount != labels.size():
        push_error("Not all six original ESO skinned walk figures loaded")
        quit(1)
        return
    var figures: Array[Node3D] = []
    for i in range(labels.size()):
        var fig: Node3D = scene.get_node_or_null("SourceWalk_" + labels[i])
        if fig == null or not fig.visible or scene.GetClipName(i) != labels[i]:
            push_error("Original ESO walk figure missing: " + labels[i])
            quit(1)
            return
        var rig: Skeleton3D = fig.get_node_or_null("Original202BoneRig")
        if rig == null or rig.get_bone_count() != 202:
            push_error("Native 202-bone ESO original source rig unavailable")
            quit(1)
            return
        var body: MeshInstance3D = rig.get_node_or_null("OriginalVoriplasticCorpse")
        if body == null or body.mesh == null or body.skin == null:
            push_error("Original ESO corpse geometry/skin not attached")
            quit(1)
            return
        if body.mesh.surface_get_array_len(0) != 8109 or body.skin.get_bind_count() != 81:
            push_error("Original ESO mesh/skin data mutated")
            quit(1)
            return
        var goo: MeshInstance3D = rig.get_node_or_null("OriginalVoriplasticGoo")
        if goo == null or goo.mesh == null or goo.skin == null or not goo.visible:
            push_error("Original ESO goo overlay missing from shared animated skeleton")
            quit(1)
            return
        if goo.mesh.surface_get_array_len(0) != 2195 or goo.skin.get_bind_count() != 59:
            push_error("Original ESO goo vertices or 59 native bindings changed")
            quit(1)
            return
        var label: Label3D = fig.get_node_or_null("OriginalAnimationLabel")
        if label == null or labels[i] not in label.text:
            push_error("Original animation identification missing")
            quit(1)
            return
        figures.append(fig)

    var camera: Camera3D = scene.get_node_or_null("OriginalBodyComparisonCamera")
    if camera == null or not camera.current:
        push_error("Comparison camera unavailable")
        quit(1)
        return
    send_key(scene, KEY_4)
    for i in range(figures.size()):
        if figures[i].visible != (i == 3):
            push_error("Original source animation focus is showing wrong mesh")
            quit(1)
            return
    send_key(scene, KEY_0)
    for figure in figures:
        if not figure.visible:
            push_error("All-six original source comparison failed to restore a figure")
            quit(1)
            return
    send_key(scene, KEY_G)
    if scene.OriginalGooVisible:
        push_error("G should hide the original goo layer")
        quit(1)
        return
    for figure in figures:
        if figure.get_node("Original202BoneRig/OriginalVoriplasticGoo").visible:
            push_error("Goo overlay did not hide across all six source rigs")
            quit(1)
            return
    send_key(scene, KEY_G)
    if not scene.OriginalGooVisible:
        push_error("G should restore the original goo layer")
        quit(1)
        return
    for figure in figures:
        if not figure.get_node("Original202BoneRig/OriginalVoriplasticGoo").visible:
            push_error("Goo overlay did not reappear across all six source rigs")
            quit(1)
            return

    send_key(scene, KEY_SPACE)
    var first_frame: int = scene.GetClipFrame(0)
    var original_hand: Vector3 = scene.GetClipBonePosition(0, "mshBn_hand_L")
    for i in range(4):
        await process_frame
    if scene.GetClipFrame(0) != first_frame:
        push_error("Pausing failed to freeze the original animation frame")
        quit(1)
        return
    for i in range(8):
        send_key(scene, KEY_RIGHT)
    if scene.GetClipFrame(0) == first_frame:
        push_error("Native walk stepping did not advance")
        quit(1)
        return
    if original_hand.is_equal_approx(scene.GetClipBonePosition(0, "mshBn_hand_L")):
        push_error("Native walk joint did not move when stepping")
        quit(1)
        return
    var initial_camera: Vector3 = camera.position
    var mouse := InputEventMouseButton.new()
    mouse.button_index = MOUSE_BUTTON_WHEEL_UP
    mouse.pressed = true
    scene._Input(mouse)
    if camera.position.is_equal_approx(initial_camera):
        push_error("Comparison camera zoom did not change view")
        quit(1)
        return
    print("PASS all six original ESO skinned walks: 6x 8109-vertex models, 6x 81 exact bindings, separate 202-bone rigs; focus/all, pause/frame step, camera zoom")
    quit(0)
