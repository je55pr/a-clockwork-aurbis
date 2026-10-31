extends SceneTree

# Live-data smoke test of the first-person scene against installed ESO archives.
# Requires local ignored .research decoders, metadata and population preview.
func _initialize() -> void:
    call_deferred("_run")

func _key(code: Key, pressed: bool) -> void:
    var event := InputEventKey.new()
    event.keycode = code
    event.physical_keycode = code
    event.pressed = pressed
    Input.parse_input_event(event)

func _run() -> void:
    var scene := load("res://scenes/first_person_lab.tscn") as PackedScene
    if scene == null:
        push_error("First-person launcher scene missing")
        quit(1)
        return
    var world := scene.instantiate()
    root.add_child(world)
    var archive := world.get_node("ArchiveWorld")
    var browser: Node = null
    for node in archive.get_children():
        if node.has_node("FullResolutionTerrainDetail"):
            browser = node
            break
    if browser == null:
        push_error("Launcher did not load ESO Auridon terrain")
        quit(1)
        return
    var terrain := browser.get_node("FullResolutionTerrainDetail")
    if terrain.get_node_or_null("FullSection_233") == null:
        push_error("Source terrain section 233 not rendered")
        quit(1)
        return
    if terrain.get_node_or_null("SourceTerrainCollision") == null:
        push_error("Source terrain collider missing")
        quit(1)
        return
    if terrain.get_node_or_null("SourcePlacedProps") == null:
        push_error("Existing source prop renderer not attached")
        quit(1)
        return
    if terrain.get_node_or_null("AuridonNpcPlaceholders") == null:
        push_error("Existing NPC preview is unavailable")
        quit(1)
        return
    var player := browser.get_node_or_null("FirstPersonPlayer") as CharacterBody3D
    if player == null:
        push_error("First-person player not attached to Auridon")
        quit(1)
        return
    var camera := player.get_node("Head/Camera3D") as Camera3D
    if not camera.current:
        push_error("Player camera is not current")
        quit(1)
        return
    for i in 65:
        await physics_frame
    if not player.is_on_floor():
        var cast := PhysicsRayQueryParameters3D.create(player.global_position + Vector3.UP * 80.0, player.global_position - Vector3.UP * 80.0)
        cast.exclude = [player.get_rid()]
        var hit := player.get_world_3d().direct_space_state.intersect_ray(cast)
        var terrain_mesh := (terrain.get_node("FullSection_233") as MeshInstance3D).mesh
        print("GROUND DEBUG player=", player.global_position, " velocity=", player.velocity, " ray=", hit, " mesh_aabb=", terrain_mesh.get_aabb())
        push_error("Player failed to land on actual ESO terrain; position=" + str(player.global_position))
        quit(1)
        return

    var head := player.get_node("Head") as Node3D
    var original_yaw: float = player.rotation.y
    var original_pitch: float = head.rotation.x
    Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
    var motion := InputEventMouseMotion.new()
    motion.relative = Vector2(76, -28)
    # Headless Godot does not deliver synthetic mouse motions to a captured OS cursor.
    # Call the same input callback used by native window events.
    player._input(motion)
    await process_frame
    if absf(player.rotation.y - original_yaw) < 0.01 or absf(head.rotation.x - original_pitch) < 0.01:
        push_error("Mouse movement did not change first-person camera yaw and pitch")
        quit(1)
        return

    var first := player.global_position
    _key(KEY_W, true)
    for i in 20:
        await physics_frame
    _key(KEY_W, false)
    if (player.global_position - first).length() < 0.5:
        push_error("Player did not walk across real terrain")
        quit(1)
        return

    for i in 10:
        await physics_frame
    if not player.is_on_floor():
        push_error("Player not grounded before jump")
        quit(1)
        return
    _key(KEY_SPACE, true)
    var launched := false
    for i in 12:
        await physics_frame
        if player.velocity.y > 0.0:
            launched = true
    _key(KEY_SPACE, false)
    if not launched:
        push_error("Player did not jump on real terrain")
        quit(1)
        return
    for i in 110:
        await physics_frame
    if not player.is_on_floor():
        push_error("Player failed to land after jumping")
        quit(1)
        return

    print("PASS Auridon source terrain, props, NPC preview, collision, mouse-look, walking and jumping")
    quit(0)
