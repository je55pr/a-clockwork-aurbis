extends SceneTree

func _initialize() -> void:
    call_deferred("_run")

func press(scene: Node, k: Key) -> void:
    var event := InputEventKey.new()
    event.keycode = k
    event.pressed = true
    scene._Input(event)

func _run() -> void:
    var scene: Node3D = load("res://scenes/eso_original_face_gallery.tscn").instantiate()
    root.add_child(scene)
    for i in range(4):
        await process_frame
    var names: Array[String] = [
        "Head_004", "Head_600", "Head_601",
        "Head_008", "Head_502", "Head_503"
    ]
    var counts: Array[int] = [404, 6530, 1443, 1721, 890, 866]
    var bindings: Array[int] = [6, 43, 38, 38, 35, 43]
    var figures: Array[Node3D] = []
    if scene.OriginalHeadCount != 6:
        push_error("Source face gallery failed to load all six original heads")
        quit(1)
        return
    for i in range(names.size()):
        var figure: Node3D = scene.get_node_or_null("OriginalFace_" + names[i])
        if figure == null or not figure.visible:
            push_error("Original ESO facial mesh not displayed: " + names[i])
            quit(1)
            return
        var skeleton: Skeleton3D = figure.get_node_or_null("OriginalCustomizationSkeleton")
        if skeleton == null:
            push_error("Original face lacks source customization skeleton")
            quit(1)
            return
        var face: MeshInstance3D = skeleton.get_node_or_null("OriginalSourceHead")
        if face == null or face.mesh == null or face.skin == null:
            push_error("Original ESO face geometry missing")
            quit(1)
            return
        if face.mesh.surface_get_array_len(0) != counts[i]:
            push_error("Source head vertex count unexpected: " + names[i])
            quit(1)
            return
        if face.skin.get_bind_count() != bindings[i]:
            push_error("Source head original skin weights/bindings missing")
            quit(1)
            return
        if i == 1 or i == 3:
            var hair: MeshInstance3D = skeleton.get_node_or_null("OriginalSourceHair_0029B2AF")
            if hair == null or hair.skin == null or hair.mesh == null:
                push_error("Original fully compatible face/hair assembly missing")
                quit(1)
                return
            if hair.mesh.surface_get_array_len(0) != 2841 or hair.skin.get_bind_count() != 12:
                push_error("Original ESO hair vertices or exact hair skin bindings lost")
                quit(1)
                return
            var material: ShaderMaterial = hair.material_override as ShaderMaterial
            if material == null or material.shader == null or \
                material.shader.code.find("ALPHA_SCISSOR_THRESHOLD") < 0:
                push_error("Original male hair source candidate alpha cutout missing")
                quit(1)
                return
            for channel in ["original_diffuse","original_normal","original_specular"]:
                if not (material.get_shader_parameter(channel) is Texture2D):
                    push_error("Original hair shared-authoring DDS channel missing: " + channel)
                    quit(1)
                    return
        figures.append(figure)

    if scene.SourceDdsHairCount != 2 or not scene.SourceDdsHairVisible:
        push_error("Original high-detail head DDS hair candidates absent")
        quit(1)
        return
    var original_hair: MeshInstance3D = figures[1].get_node(
        "OriginalCustomizationSkeleton/OriginalSourceHair_0029B2AF")
    var initial_material: Material = original_hair.material_override
    press(scene, KEY_T)
    if scene.SourceDdsHairVisible or original_hair.material_override == initial_material:
        push_error("Neutral original head hair comparison not enabled")
        quit(1)
        return
    press(scene, KEY_T)
    if not scene.SourceDdsHairVisible or original_hair.material_override != initial_material:
        push_error("Original head DDS hair candidate comparison not restored")
        quit(1)
        return
    var camera: Camera3D = scene.get_node_or_null("OriginalFaceCamera")
    if camera == null or not camera.current:
        push_error("Original face gallery camera missing")
        quit(1)
        return
    press(scene, KEY_2)
    for i in range(figures.size()):
        if figures[i].visible != (i == 1):
            push_error("Original high-detail face focus control failed")
            quit(1)
            return
    press(scene, KEY_0)
    for figure in figures:
        if not figure.visible:
            push_error("Compare-all original faces mode failed")
            quit(1)
            return
    var from: Vector3 = camera.position
    var wheel := InputEventMouseButton.new()
    wheel.button_index = MOUSE_BUTTON_WHEEL_UP
    wheel.pressed = true
    scene._Input(wheel)
    if camera.position.is_equal_approx(from):
        push_error("Face gallery orbit camera zoom control failed")
        quit(1)
        return
    print("PASS original ESO face construction UI: 6 true head assets, 6530-vertex high-detail face, 2 source-exact 2841-vertex hair skins with 12/12 binds, shared-authoring provisional original D/N/S hair textures, T toggle, focus and zoom")
    quit(0)
