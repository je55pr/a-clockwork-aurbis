extends Node3D

# Source records are generated locally from SkyrimVR/Data/Skyrim.esm.
# Terrain: 25 exact LAND VHGT/VCLR cells. Placements: REFR/ACHR source coordinates.
# NIF building geometry is not reconstructed yet; only explicit diagnostic markers.

const DATA_FILE = "res://generated/riverwood.json"
const TERRAIN_SCALE = 0.01
const VILLAGE_LABELS = ["inn01.nif", "smith01.nif", "lumbermill01.nif", "farmhouse04.nif", "farmhouse02.nif", "farmhouse05.nif", "farmhouse06.nif"]
var data: Dictionary
var origin_x: float
var origin_y: float
var camera: Camera3D
var yaw := -0.25
var pitch := -0.35
var fly_speed := 28.0
var show_markers := true
var markers: Node3D
var hud: Label
var stats: Label
var resident_panel: PanelContainer
var terrain_by_cell: Dictionary = {}
var labels_count := 0

func _ready() -> void:
    Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
    var file = FileAccess.open(DATA_FILE, FileAccess.READ)
    if file == null:
        push_error("Run node skyrim/tools/extract-riverwood.js first: missing generated source data")
        return
    data = JSON.parse_string(file.get_as_text())
    if typeof(data) != TYPE_DICTIONARY:
        push_error("Riverwood source JSON is invalid")
        return
    origin_x = float(data["center"]["x"])
    origin_y = float(data["center"]["y"])
    for tile in data["terrain"]:
        terrain_by_cell[str(int(tile["x"])) + "," + str(int(tile["y"]))] = tile
    _setup_lighting()
    _build_landscape()
    _build_models()
    _build_source_markers()
    _build_hud()
    var px := 17700.0
    var py := -53400.0
    camera.position = _world(px, py, _height_at(px, py) + 4200.0)
    camera.look_at(_world(21650.0, -45900.0, -180.0), Vector3.UP)
    yaw = camera.rotation.y
    pitch = camera.rotation.x
    print("RIVERWOOD: %d source LAND cells, %d source REFR placements, %d exterior ACHR actors, %d interior residents, %d architectural labels." %
        [data["terrain"].size(), data["objects"].size(), data["actors"].size(), data["people"].size(), labels_count])
    if OS.get_environment("SKYRIM_CAPTURE") == "1":
        _capture_screenshot.call_deferred()

func _setup_lighting() -> void:
    var world := WorldEnvironment.new()
    var env := Environment.new()
    env.background_mode = Environment.BG_COLOR
    env.background_color = Color(0.52, 0.67, 0.81)
    env.ambient_light_source = Environment.AMBIENT_SOURCE_COLOR
    env.ambient_light_color = Color(0.75, 0.83, 0.91)
    env.ambient_light_energy = 0.7
    world.environment = env
    add_child(world)
    var sun := DirectionalLight3D.new()
    sun.light_energy = 1.25
    sun.rotation_degrees = Vector3(-60.0, 25.0, 0.0)
    add_child(sun)
    camera = Camera3D.new()
    camera.current = true
    camera.fov = 80.0
    camera.far = 1800.0
    add_child(camera)

func _world(x: float, y: float, z: float) -> Vector3:
    # Skyrim Z is up; Godot Y is up. Flip Skyrim horizontal Y to Godot Z.
    return Vector3((x - origin_x) * TERRAIN_SCALE, z * TERRAIN_SCALE, -(y - origin_y) * TERRAIN_SCALE)

func _height_at(x: float, y: float) -> float:
    var cell_x := int(floor(x / 4096.0))
    var cell_y := int(floor(y / 4096.0))
    var k := str(cell_x) + "," + str(cell_y)
    if not terrain_by_cell.has(k):
        return 0.0
    var tile: Dictionary = terrain_by_cell[k]
    var heights: Array = tile["heights"]
    var xx := clampf((x - cell_x * 4096.0) / 128.0, 0.0, 31.999)
    var yy := clampf((y - cell_y * 4096.0) / 128.0, 0.0, 31.999)
    var i := int(floor(xx))
    var j := int(floor(yy))
    var fx := xx - i
    var fy := yy - j
    var h00 := float(heights[j * 33 + i])
    var h01 := float(heights[j * 33 + i + 1])
    var h10 := float(heights[(j + 1) * 33 + i])
    var h11 := float(heights[(j + 1) * 33 + i + 1])
    return lerpf(lerpf(h00, h01, fx), lerpf(h10, h11, fx), fy)

func _build_landscape() -> void:
    var material := StandardMaterial3D.new()
    material.vertex_color_use_as_albedo = true
    material.roughness = 1.0
    material.cull_mode = BaseMaterial3D.CULL_DISABLED
    for tile in data["terrain"]:
        var arrays := []
        arrays.resize(Mesh.ARRAY_MAX)
        var points := PackedVector3Array()
        var colors := PackedColorArray()
        var indices := PackedInt32Array()
        var heights: Array = tile["heights"]
        var raw_colors := PackedByteArray()
        if tile["vertexColors"] != null:
            raw_colors = Marshalls.base64_to_raw(tile["vertexColors"])
        var cx := int(tile["x"])
        var cy := int(tile["y"])
        for j in range(33):
            for i in range(33):
                var idx := j * 33 + i
                var px := cx * 4096.0 + i * 128.0
                var py := cy * 4096.0 + j * 128.0
                points.append(_world(px, py, float(heights[idx])))
                if raw_colors.size() >= idx * 3 + 3:
                    var c := Color(raw_colors[idx * 3] / 255.0,
                        raw_colors[idx * 3 + 1] / 255.0,
                        raw_colors[idx * 3 + 2] / 255.0)
                    colors.append(c)
                else:
                    colors.append(Color(0.55, 0.56, 0.44))
        for j in range(32):
            for i in range(32):
                var a := j * 33 + i
                var b := a + 1
                var c := a + 33
                var d := c + 1
                # Winding for the flipped horizontal Y coordinate.
                indices.append_array(PackedInt32Array([a, c, b, b, c, d]))
        arrays[Mesh.ARRAY_VERTEX] = points
        arrays[Mesh.ARRAY_COLOR] = colors
        arrays[Mesh.ARRAY_INDEX] = indices
        var mesh := ArrayMesh.new()
        mesh.add_surface_from_arrays(Mesh.PRIMITIVE_TRIANGLES, arrays)
        var inst := MeshInstance3D.new()
        inst.name = "LAND_%d_%d" % [cx, cy]
        inst.mesh = mesh
        inst.material_override = material
        add_child(inst)

func _pin(position: Vector3, label_text: String, color: Color, tall: bool) -> void:
    var parent := Node3D.new()
    parent.position = position
    markers.add_child(parent)
    var mesh := CylinderMesh.new()
    mesh.top_radius = 0.09 if tall else 0.035
    mesh.bottom_radius = 0.12 if tall else 0.05
    mesh.height = 1.6 if tall else 0.8
    var vis := MeshInstance3D.new()
    vis.mesh = mesh
    vis.position.y = mesh.height / 2.0
    var material := StandardMaterial3D.new()
    material.albedo_color = color
    material.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
    vis.material_override = material
    parent.add_child(vis)
    var lbl := Label3D.new()
    lbl.text = label_text
    lbl.font_size = 40
    lbl.pixel_size = 0.006
    lbl.billboard = BaseMaterial3D.BILLBOARD_ENABLED
    lbl.no_depth_test = true
    lbl.position.y = mesh.height + 0.5
    parent.add_child(lbl)
    labels_count += 1

func _build_models() -> void:
    var index_file := FileAccess.open("res://generated/model-index.json", FileAccess.READ)
    if index_file == null:
        push_warning("Riverwood NIF cache missing: run node tools/convert-riverwood-models.js")
        return
    var parsed = JSON.parse_string(index_file.get_as_text())
    if typeof(parsed) != TYPE_DICTIONARY:
        return
    var catalog: Dictionary = parsed["models"]
    var instances_by_type: Dictionary = {}
    for obj in data["objects"]:
        if obj["nif"] == null:
            continue
        var key := "meshes\\" + str(obj["nif"]).to_lower().replace("/", "\\")
        if not catalog.has(key):
            continue
        if not instances_by_type.has(key):
            instances_by_type[key] = []
        instances_by_type[key].append(obj)
    var loaded := 0
    var placed := 0
    for key in instances_by_type:
        var source_path: String = catalog[key]["path"]
        var glb := GLTFDocument.new()
        var state := GLTFState.new()
        var err := glb.append_from_file(ProjectSettings.globalize_path(source_path), state)
        if err != OK:
            push_warning("Could not load source GLB " + key + ": " + str(err))
            continue
        var model := glb.generate_scene(state)
        if model == null:
            continue
        loaded += 1
        for ref in instances_by_type[key]:
            var item := model.duplicate()
            var pos: Array = ref["xyz"]
            var rot: Array = ref["rot"]
            item.position = _world(float(pos[0]), float(pos[1]), float(pos[2]))
            var rx := float(rot[0])
            var ry := float(rot[1])
            var rz := float(rot[2])
            item.basis = Basis(Vector3.UP, rz) * Basis(Vector3(0, 0, -1), ry) * Basis(Vector3.RIGHT, rx)
            add_child(item)
            placed += 1
        model.free()
    print("RIVERWOOD NIF: %d original mesh types, %d source-placed instances" % [loaded, placed])

func _build_source_markers() -> void:
    markers = Node3D.new()
    markers.name = "SourcePlacementMarkers"
    add_child(markers)
    var architecture := {}
    for obj in data["objects"]:
        var nif := str(obj["nif"]).to_lower()
        var name := str(obj["editor"])
        var base := nif.get_file()
        if base in VILLAGE_LABELS:
            architecture[str(obj["ref"])] = {"name": base.trim_suffix(".nif").capitalize(), "obj": obj}
    for item in architecture.values():
        var p: Array = item["obj"]["xyz"]
        _pin(_world(float(p[0]),float(p[1]),float(p[2])),
            str(item["name"]), Color(1.0, 0.77, 0.36), true)
    for actor in data["actors"]:
        var p: Array = actor["xyz"]
        var label := str(actor["editor"])
        _pin(_world(float(p[0]),float(p[1]),float(p[2])),
            label, Color(0.42, 0.88, 0.98), false)

func _build_hud() -> void:
    var layer := CanvasLayer.new()
    add_child(layer)
    var panel := PanelContainer.new()
    panel.position = Vector2(18, 18)
    panel.custom_minimum_size = Vector2(420, 0)
    layer.add_child(panel)
    var stack := VBoxContainer.new()
    panel.add_child(stack)
    var title := Label.new()
    title.text = "A CLOCKWORK AURBIS  ·  RIVERWOOD"
    title.add_theme_font_size_override("font_size", 21)
    stack.add_child(title)
    stats = Label.new()
    stats.text = "%d original terrain cells  ·  %d static references\n%d exterior actors  ·  %d interior residents  ·  %d landmarks" % [
        data["terrain"].size(), data["objects"].size(), data["actors"].size(), data["people"].size(), labels_count]
    stack.add_child(stats)
    hud = Label.new()
    hud.text = "WASD fly  ·  Mouse look  ·  Q/E descend/ascend\nShift faster  ·  Tab markers  ·  N residents  ·  Esc release\nOriginal NIF meshes and diffuse textures; terrain materials not yet decoded"
    stack.add_child(hud)
    var label := Label.new()
    label.position = Vector2(20, 670)
    label.text = "SKYRIM VR  •  SOURCE-DERIVED TERRAIN & PLACEMENTS"
    layer.add_child(label)
    _build_resident_directory(layer)

func _build_resident_directory(layer: CanvasLayer) -> void:
    resident_panel = PanelContainer.new()
    resident_panel.position = Vector2(835, 18)
    resident_panel.custom_minimum_size = Vector2(420, 0)
    resident_panel.visible = false
    layer.add_child(resident_panel)
    var roster := RichTextLabel.new()
    roster.custom_minimum_size = Vector2(410, 560)
    roster.bbcode_enabled = true
    roster.scroll_active = true
    var lines := PackedStringArray()
    lines.append("[b]RIVERWOOD  ·  SOURCE NPC RECORDS[/b]")
    lines.append("Starting interiors and authored AI packages")
    lines.append("")
    for resident in data["people"]:
        lines.append("[b]" + str(resident["name"]) + "[/b]  ·  " + str(resident["startingInterior"]).trim_prefix("Riverwood"))
        var pack_list: Array = resident["packages"]
        if not pack_list.is_empty():
            lines.append("  " + str(pack_list[0]) + ("  (+" + str(pack_list.size() - 1) + " packages)" if pack_list.size() > 1 else ""))
        else:
            lines.append("  No direct AI package links")
        lines.append("")
    roster.text = "\n".join(lines)
    resident_panel.add_child(roster)

func _process(delta: float) -> void:
    if camera == null:
        return
    var forward := -camera.global_basis.z
    var right := camera.global_basis.x
    var motion := Vector3.ZERO
    if Input.is_key_pressed(KEY_W): motion += forward
    if Input.is_key_pressed(KEY_S): motion -= forward
    if Input.is_key_pressed(KEY_A): motion -= right
    if Input.is_key_pressed(KEY_D): motion += right
    if Input.is_key_pressed(KEY_E): motion += Vector3.UP
    if Input.is_key_pressed(KEY_Q): motion += Vector3.DOWN
    if motion.length_squared() > 0.001:
        camera.position += motion.normalized() * fly_speed * (3.0 if Input.is_key_pressed(KEY_SHIFT) else 1.0) * delta

func _unhandled_input(event: InputEvent) -> void:
    if event is InputEventMouseMotion and Input.mouse_mode == Input.MOUSE_MODE_CAPTURED:
        yaw -= event.relative.x * 0.0035
        pitch = clampf(pitch - event.relative.y * 0.0035, -1.52, 1.52)
        camera.rotation = Vector3(pitch, yaw, 0.0)
    elif event is InputEventKey and event.pressed and not event.echo:
        if event.keycode == KEY_ESCAPE:
            Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
        elif event.keycode == KEY_N:
            resident_panel.visible = not resident_panel.visible
        elif event.keycode == KEY_TAB:
            show_markers = not show_markers
            markers.visible = show_markers
    elif event is InputEventMouseButton and event.pressed:
        if event.button_index == MOUSE_BUTTON_LEFT:
            Input.mouse_mode = Input.MOUSE_MODE_CAPTURED
        elif event.button_index == MOUSE_BUTTON_WHEEL_UP:
            fly_speed *= 1.18
        elif event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
            fly_speed = maxf(1.0, fly_speed / 1.18)

func _capture_screenshot() -> void:
    await get_tree().create_timer(4.0).timeout
    var image := get_viewport().get_texture().get_image()
    var path := ProjectSettings.globalize_path("res://generated/riverwood-preview.png")
    var code := image.save_png(path)
    print("RIVERWOOD CAPTURE ", path, " status ", code)
    get_tree().quit()
