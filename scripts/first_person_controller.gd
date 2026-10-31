extends CharacterBody3D

@export var walk_speed := 5.5
@export var sprint_speed := 8.5
@export var jump_height := 1.25
@export var mouse_sensitivity := 0.0024
@export var ground_acceleration := 22.0
@export var air_acceleration := 5.0

@onready var head: Node3D = $Head
@onready var camera: Camera3D = $Head/Camera3D

var _gravity := 19.6
var _jump_held := false

func _ready() -> void:
    _gravity = float(ProjectSettings.get_setting("physics/3d/default_gravity"))
    floor_snap_length = 0.3
    Input.mouse_mode = Input.MOUSE_MODE_CAPTURED

func _unhandled_input(event: InputEvent) -> void:
    if event is InputEventMouseMotion and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED:
        rotate_y(-event.relative.x * mouse_sensitivity)
        head.rotate_x(-event.relative.y * mouse_sensitivity)
        head.rotation.x = clampf(head.rotation.x, deg_to_rad(-85.0), deg_to_rad(85.0))
        get_viewport().set_input_as_handled()
    elif event is InputEventKey and event.pressed and not event.echo and event.keycode == KEY_ESCAPE:
        Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
        get_viewport().set_input_as_handled()
    elif event is InputEventMouseButton and event.pressed and event.button_index == MOUSE_BUTTON_LEFT:
        if Input.mouse_mode == Input.MOUSE_MODE_VISIBLE:
            Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
            get_viewport().set_input_as_handled()

func _physics_process(delta: float) -> void:
    if not is_on_floor():
        velocity.y -= _gravity * delta
    elif velocity.y < 0.0:
        velocity.y = 0.0

    var jumping := Input.is_physical_key_pressed(KEY_SPACE)
    if jumping and not _jump_held and is_on_floor():
        velocity.y = sqrt(2.0 * _gravity * jump_height)
    _jump_held = jumping

    var movement := Vector2(
        float(Input.is_physical_key_pressed(KEY_D)) - float(Input.is_physical_key_pressed(KEY_A)),
        float(Input.is_physical_key_pressed(KEY_S)) - float(Input.is_physical_key_pressed(KEY_W))
    ).normalized()
    var direction := (global_transform.basis * Vector3(movement.x, 0.0, movement.y)).normalized()
    var running := Input.is_physical_key_pressed(KEY_SHIFT)
    var speed := sprint_speed if running else walk_speed
    var acceleration := ground_acceleration if is_on_floor() else air_acceleration
    velocity.x = move_toward(velocity.x, direction.x * speed, acceleration * delta)
    velocity.z = move_toward(velocity.z, direction.z * speed, acceleration * delta)
    move_and_slide()
