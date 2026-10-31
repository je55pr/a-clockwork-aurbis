extends SceneTree

# Run with: Godot --headless --path . --script res://tests/Godot/first_person_smoke.gd
func _initialize() -> void:
    call_deferred("_check")

func _key(key: Key, pressed: bool) -> void:
    var event := InputEventKey.new()
    event.keycode = key
    event.physical_keycode = key
    event.pressed = pressed
    Input.parse_input_event(event)

func _check() -> void:
    var packed := load("res://scenes/model_animation_lab.tscn") as PackedScene
    if packed == null:
        push_error("Cannot load first-person lab scene")
        quit(1)
        return
    var lab := packed.instantiate()
    root.add_child(lab)
    var player := lab.get_node("Player") as CharacterBody3D
    for i in 25:
        await physics_frame
    if not player.is_on_floor():
        push_error("Player did not land on the test platform")
        quit(1)
        return
    var initial_z := player.global_position.z
    _key(KEY_W, true)
    for i in 24:
        await physics_frame
    _key(KEY_W, false)
    if initial_z - player.global_position.z < 0.7:
        push_error("Player did not move forward")
        quit(1)
        return
    _key(KEY_SPACE, true)
    for i in 5:
        await physics_frame
    _key(KEY_SPACE, false)
    if player.velocity.y <= 0.0 or player.global_position.y < 0.1:
        push_error("Space did not launch a jump")
        quit(1)
        return
    for i in 90:
        await physics_frame
    if not player.is_on_floor():
        push_error("Player did not land after jumping")
        quit(1)
        return
    print("PASS first-person collision, forward movement, jump and landing")
    quit(0)
