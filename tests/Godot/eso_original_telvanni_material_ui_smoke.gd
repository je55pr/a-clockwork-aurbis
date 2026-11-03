extends SceneTree

func _initialize() -> void:
    call_deferred("_run")

func _run() -> void:
    var scene: Node3D = load("res://scenes/eso_original_telvanni_material_lab.tscn").instantiate()
    root.add_child(scene)
    for i in range(6):
        await process_frame
    if scene.VerifiedOriginalMeshes != 4 or scene.SourceVertices != 2137 \
        or scene.OriginalMaterialBindings != 16 or scene.TintDebugChannel != -1:
        push_error("Original Telvanni native clothing parts or DDS mapping changed")
        quit(1)
        return
    var primary: Color = scene.OriginalSourcePrimaryTint
    var secondary: Color = scene.OriginalSourceSecondaryTint
    var accent: Color = scene.OriginalSourceAccentTint
    if not primary.is_equal_approx(Color8(128,120,112)) \
        or not secondary.is_equal_approx(Color8(89,89,89)) \
        or not accent.is_equal_approx(Color8(61,71,76)):
        push_error("ESO Granny original Telvanni authored tint RGB values were lost")
        quit(1)
        return
    var models: Array[String] = [
        "TelvanniLight_Helmet_B",
        "TelvanniLight_Helmet_A",
        "TelvanniLight_Belt_A",
        "TelvanniLight_Shoulders_A"
    ]
    var vertices: Array[int] = [510,680,163,784]
    var first_material: ShaderMaterial
    for i in range(models.size()):
        var mesh: MeshInstance3D = scene.get_node_or_null("Source_"+models[i])
        if mesh == null or mesh.mesh == null or mesh.mesh.surface_get_array_len(0) != vertices[i]:
            push_error("Missing original Granny Telvanni geometry: "+models[i])
            quit(1)
            return
        var m: ShaderMaterial = mesh.material_override as ShaderMaterial
        if m == null or m.shader == null \
            or m.shader.code.find("source_tint_debug_channel") < 0:
            push_error("Provisional original Telvanni D/N/S/T shader missing")
            quit(1)
            return
        if first_material == null:
            first_material=m
        for channel in ["original_diffuse","original_normal","original_specular","original_tint_mask"]:
            var img: Texture2D = m.get_shader_parameter(channel)
            if img == null or img.get_width() != 576 or img.get_height() != 256:
                push_error("Missing original Telvanni 576x256 DDS channel "+channel)
                quit(1)
                return
    var mode := InputEventKey.new()
    mode.pressed = true
    mode.keycode = KEY_M
    for expected in range(4):
        scene._Input(mode)
        if scene.TintDebugChannel != expected \
            or first_material.get_shader_parameter("source_tint_debug_channel") != expected:
            push_error("Original four-channel armour mask view did not cycle")
            quit(1)
            return
    scene._Input(mode)
    if scene.TintDebugChannel != -1:
        push_error("Original Telvanni diffuse view did not restore")
        quit(1)
        return
    var camera: Camera3D = scene.get_node_or_null("SourceTelvanniCamera")
    if camera == null or not camera.current:
        push_error("Original Telvanni source material camera missing")
        quit(1)
        return
    var before: Vector3 = camera.position
    var scroll := InputEventMouseButton.new()
    scroll.button_index = MOUSE_BUTTON_WHEEL_UP
    scroll.pressed = true
    scene._Input(scroll)
    if camera.position.is_equal_approx(before):
        push_error("Original source material preview zoom did not change")
        quit(1)
        return
    print("PASS four original Telvanni Light meshes / 2137 vertices / 16 DDS channels / original GR2 primary-secondary-accent tints / tint R/G/B/A cycle / source-only zoom; no runtime material assignment inferred")
    quit(0)
