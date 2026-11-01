extends SceneTree

func _initialize() -> void:
    call_deferred("_run")

func key(scene: Node, key_code: Key) -> void:
    var event := InputEventKey.new()
    event.keycode = key_code
    event.pressed = true
    scene._Input(event)

func _run() -> void:
    var scene: Node3D = load("res://scenes/eso_hybrid_head_lab.tscn").instantiate()
    root.add_child(scene)
    for i in range(4):
        await process_frame

    var skeleton: Skeleton3D = scene.get_node_or_null("OriginalHybridESOHeadRig")
    if skeleton == null or skeleton.get_bone_count() != 54:
        push_error("Original 54-bone hybrid ESO source skeleton was not built")
        quit(1)
        return
    var head: MeshInstance3D = skeleton.get_node_or_null("OriginalESOHead_000E2D77")
    if head == null or head.mesh == null or head.skin == null:
        push_error("Original ESO hybrid skin/mesh missing")
        quit(1)
        return
    if head.skin.get_bind_count() != 9 or scene.GetOriginalBindingCount() != 9:
        push_error("Original hybrid mesh bind table not preserved")
        quit(1)
        return
    if scene.GetAnimatedSourceBoneCount() != 43:
        push_error("Original hybrid 43 animation-bone mappings missing")
        quit(1)
        return
    var arrays: Array = head.mesh.surface_get_arrays(0)
    if arrays[Mesh.ARRAY_VERTEX].size() != 404:
        push_error("Original 404 head vertices unavailable")
        quit(1)
        return
    var camera: Camera3D = scene.get_node_or_null("NativeHeadCamera")
    if camera == null or not camera.current:
        push_error("Original head inspection camera unavailable")
        quit(1)
        return
    var native_neck: MeshInstance3D = scene.get_node_or_null("OriginalSourceNeckPositions/Native_mshBn_neck")
    var rest_neck: MeshInstance3D = scene.get_node_or_null("OriginalSourceNeckPositions/Rest_mshBn_neck")
    if native_neck == null or rest_neck == null or not rest_neck.visible:
        push_error("Actual source neck and original rest neck references missing")
        quit(1)
        return
    if native_neck.position.distance_to(rest_neck.position) < 0.25:
        push_error("Original GEN_Walk neck displacement not reproduced")
        quit(1)
        return
    key(scene, KEY_R)
    if rest_neck.visible:
        push_error("Original rest-reference toggle did not hide original neck position")
        quit(1)
        return
    key(scene, KEY_R)
    if not rest_neck.visible:
        push_error("Original rest-reference toggle did not restore neck position")
        quit(1)
        return
    key(scene, KEY_N)
    if native_neck.get_parent().visible:
        push_error("Original bone-guide visibility toggle failed")
        quit(1)
        return
    key(scene, KEY_N)
    if not native_neck.get_parent().visible:
        push_error("Original neck-guide visibility toggle failed to restore")
        quit(1)
        return
    key(scene, KEY_SPACE)
    var frame0: int = scene.GetOriginalFrameIndex()
    for i in range(3):
        await process_frame
    if scene.GetOriginalFrameIndex() != frame0:
        push_error("Pause did not stop native ESO source frame selection")
        quit(1)
        return
    key(scene, KEY_RIGHT)
    var after: int = scene.GetOriginalFrameIndex()
    if after == frame0:
        push_error("Step did not advance original ESO source frame")
        quit(1)
        return
    var before_zoom: Vector3 = camera.position
    var mouse := InputEventMouseButton.new()
    mouse.button_index = MOUSE_BUTTON_WHEEL_UP
    mouse.pressed = true
    scene._Input(mouse)
    if camera.position.is_equal_approx(before_zoom):
        push_error("Source head camera zoom unavailable")
        quit(1)
        return
    print("PASS original ESO hybrid head UI: 404 vertices, 9 native skin binds, 43 animated original bones, source pose pause/step, orbit camera")
    quit(0)
