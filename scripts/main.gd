extends Node3D

# Runtime environment only. ArchiveWorld and TerrainBrowser own the content/UI.
func _enter_tree() -> void:
	var world := WorldEnvironment.new()
	var environment := Environment.new()
	environment.background_mode = Environment.BG_COLOR
	environment.background_color = Color(0.065, 0.090, 0.125)
	environment.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
	environment.ambient_light_color = Color(0.7, 0.75, 0.85)
	environment.ambient_light_energy = 0.55
	world.environment = environment
	add_child(world)

	var sun := DirectionalLight3D.new()
	sun.rotation_degrees = Vector3(-48.0, -22.0, 0.0)
	sun.light_energy = 1.6
	add_child(sun)

	var camera := Camera3D.new()
	camera.position = Vector3(160, 180, 300)
	camera.far = 50000.0
	add_child(camera)
	camera.look_at(Vector3.ZERO)
