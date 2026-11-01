extends SceneTree

func _initialize() -> void:
    call_deferred("_run")

func press(scene: Node,keycode: Key) -> void:
    var event := InputEventKey.new()
    event.keycode=keycode
    event.pressed=true
    scene._Input(event)

func _run() -> void:
    var scene: Node3D=load("res://scenes/eso_original_skinned_body_lab.tscn").instantiate()
    root.add_child(scene)
    for i in range(4):
        await process_frame
    var rig: Skeleton3D=scene.get_node_or_null("ESOOriginal202BoneRig")
    if rig==null or rig.get_bone_count()!=202:
        push_error("Original 202-bone native ESO humanoid skeleton missing")
        quit(1)
        return
    var mesh: MeshInstance3D=rig.get_node_or_null("ESOOriginalVoriplasticCorpse_0015D6CD")
    if mesh==null or mesh.mesh==null or mesh.skin==null:
        push_error("Complete source-authored ESO skin/mesh missing")
        quit(1)
        return
    if mesh.mesh.surface_get_array_len(0)!=8109 or mesh.skin.get_bind_count()!=81:
        push_error("Source ESO geometry or binding table changed")
        quit(1)
        return
    if scene.NativeBindingCount!=81 or scene.OriginalVertexCount!=8109:
        push_error("Godot read incorrect native skin/mesh metadata")
        quit(1)
        return
    var camera: Camera3D=scene.get_node_or_null("SourceSkinnedBodyCamera")
    if camera==null or not camera.current:
        push_error("Missing original body research camera")
        quit(1)
        return
    press(scene,KEY_SPACE)
    var paused: int=scene.CurrentFrame
    for i in range(3):
        await process_frame
    if scene.CurrentFrame!=paused:
        push_error("Pause did not hold native source animation frame")
        quit(1)
        return
    press(scene,KEY_RIGHT)
    if scene.CurrentFrame==paused:
        push_error("Original native animation frame did not step")
        quit(1)
        return
    var original_camera: Vector3=camera.position
    var wheel := InputEventMouseButton.new()
    wheel.button_index=MOUSE_BUTTON_WHEEL_UP
    wheel.pressed=true
    scene._Input(wheel)
    if camera.position.is_equal_approx(original_camera):
        push_error("Original skinned-body zoom unavailable")
        quit(1)
        return
    print("PASS complete original ESO skinned body UI: 8109 original vertices, 81 exact native binds, 202-bone animation, pause/step, zoom")
    quit(0)
