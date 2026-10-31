extends Node3D

# Standalone engine sandbox. No population, treasury or terrain simulation runs here.
@onready var player: CharacterBody3D = $Player
@onready var status: Label = $Hud/Status

func _ready() -> void:
    _make_box(Vector3(0.0, -0.25, 0.0), Vector3(40.0, 0.5, 40.0), Color("#506d65"))
    _make_box(Vector3(-3.0, 0.35, -4.5), Vector3(1.5, 0.7, 1.5), Color("#a9b7aa"))
    _make_box(Vector3(-0.6, 0.65, -5.0), Vector3(1.5, 1.3, 1.5), Color("#c4a879"))
    _make_box(Vector3(3.0, 0.2, -4.5), Vector3(2.5, 0.4, 2.5), Color("#8da4ac"))
    _make_box(Vector3(0.0, 1.5, 11.0), Vector3(28.0, 3.0, 0.5), Color("#7f9188"))
    _make_box(Vector3(-14.0, 1.5, 0.0), Vector3(0.5, 3.0, 28.0), Color("#7f9188"))
    _make_box(Vector3(14.0, 1.5, 0.0), Vector3(0.5, 3.0, 28.0), Color("#7f9188"))
    player.global_position = Vector3(0.0, 0.05, 3.0)

func _process(_delta: float) -> void:
    var mode := "grounded" if player.is_on_floor() else "airborne"
    status.text = "WASD move   Shift sprint   Space jump   Mouse look   Esc release mouse   Click to recapture\n%s   y=%.2f   speed=%.2f m/s" % [
        mode, player.global_position.y, Vector2(player.velocity.x, player.velocity.z).length()
    ]

func _make_box(position: Vector3, size: Vector3, tint: Color) -> void:
    var body := StaticBody3D.new()
    body.position = position
    add_child(body)
    var collision := CollisionShape3D.new()
    var shape := BoxShape3D.new()
    shape.size = size
    collision.shape = shape
    body.add_child(collision)
    var visual := MeshInstance3D.new()
    var mesh := BoxMesh.new()
    mesh.size = size
    visual.mesh = mesh
    var material := StandardMaterial3D.new()
    material.albedo_color = tint
    visual.material_override = material
    body.add_child(visual)
