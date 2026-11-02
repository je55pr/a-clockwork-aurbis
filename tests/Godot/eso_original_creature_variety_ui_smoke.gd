extends SceneTree

func _initialize() -> void:
    call_deferred("_check")

func send_key(scene: Node3D, code: Key) -> void:
    var event := InputEventKey.new()
    event.keycode = code
    event.pressed = true
    scene._Input(event)

func _check() -> void:
    var scene: Node3D = load("res://scenes/eso_original_creature_variety_lab.tscn").instantiate()
    root.add_child(scene)
    for _f in range(4):
        await process_frame
    if scene.OriginalCreatureCount != 13 or scene.TotalCreatureInstances != 312 \
        or scene.UniqueCreatureMeshes != 13 or scene.SharedOriginalMeshCopies != 299:
        push_error("Original curated creature variety geometry sharing invalid")
        quit(1)
        return
    var types: Array[String] = [
        "Clannfear", "Daedroth", "Guar",
        "ClannfearArmored", "ClannfearChampion", "ClannfearBasicCH",
        "ClannfearBoss", "DaedrothBasic", "DaedrothArmoredGreen",
        "GuarBasic", "GuarVariantC", "GuarScalecaller", "GuarPeryite"
    ]
    for name in types:
        var creature: Node3D = scene.get_node_or_null("SourceCreature_" + name)
        if creature == null:
            push_error("Missing original source creature type: " + name)
            quit(1)
            return
        var mesh: MeshInstance3D = creature.get_node_or_null(
            "NativeCreatureSkeleton/OriginalCreatureMesh")
        if mesh == null or mesh.mesh == null or mesh.skin == null \
            or mesh.mesh.surface_get_array_len(0) < 100 \
            or mesh.skin.get_bind_count() < 10:
            push_error("Curated creature type has incomplete original source mesh/skin: " + name)
            quit(1)
            return
    var variant: Node3D = scene.get_node_or_null("OriginalCrowd_ClannfearArmored_023")
    if variant == null or not variant.visible:
        push_error("Missing original armoured Clannfear crowd variant")
        quit(1)
        return
    var primary: MeshInstance3D = scene.get_node(
        "SourceCreature_ClannfearArmored/NativeCreatureSkeleton/OriginalCreatureMesh")
    var clone: MeshInstance3D = variant.get_node("NativeCreatureSkeleton/OriginalCreatureMesh")
    if primary.mesh != clone.mesh or primary.skin != clone.skin:
        push_error("Curated creature variant did not share original geometry and skin")
        quit(1)
        return
    if FileAccess.file_exists("res://data/eso_creature_texture_candidates.tsv"):
        if scene.CandidateMaterialCount != 9 or scene.CandidateSpecularCount != 8:
            push_error("Nine original DDS candidate assignments were not loaded")
            quit(1)
            return
        for mapped in [1,2,3,6,8,9,10,11,12]:
            if not scene.HasCandidateMaterial(mapped):
                push_error("Missing original DDS material candidate at catalogue index " + str(mapped))
                quit(1)
                return
            var candidate: MeshInstance3D = scene.get_node(
                "SourceCreature_" + types[mapped] +
                "/NativeCreatureSkeleton/OriginalCreatureMesh")
            var actual: Material = candidate.material_override
            var expects_normal: bool = mapped != 1 and mapped != 8
            var expects_specular: bool = mapped != 12
            if expects_specular:
                var source_shader: ShaderMaterial = actual as ShaderMaterial
                if source_shader == null or source_shader.shader == null:
                    push_error("Original specular DDS shader missing at index " + str(mapped))
                    quit(1)
                    return
                if not (source_shader.get_shader_parameter("original_diffuse") is Texture2D) \
                    or not (source_shader.get_shader_parameter("original_specular") is Texture2D):
                    push_error("Original source diffuse/specular DDS not bound " + str(mapped))
                    quit(1)
                    return
                if source_shader.get_shader_parameter("has_original_normal") != expects_normal:
                    push_error("Packed-normal classification wrong for " + str(mapped))
                    quit(1)
                    return
                if expects_normal and not (source_shader.get_shader_parameter("original_normal") is Texture2D):
                    push_error("Source packed-normal DDS absent " + str(mapped))
                    quit(1)
                    return
            else:
                var standard: StandardMaterial3D = actual as StandardMaterial3D
                if standard == null or standard.albedo_texture == null or \
                    standard.normal_enabled != expects_normal:
                    push_error("Original source diffuse/normal material missing " + str(mapped))
                    quit(1)
                    return
        if not scene.HasCandidateMaterial(3):
            push_error("Original Clannfear armoured DDS candidate was not loaded")
            quit(1)
            return
        var orig: Material = primary.material_override
        send_key(scene, KEY_T)
        if primary.material_override == orig or clone.material_override == orig:
            push_error("Source DDS candidate toggle did not reach Clannfear variants")
            quit(1)
            return
        send_key(scene, KEY_T)
        if primary.material_override != orig or clone.material_override != orig:
            push_error("Clannfear source texture candidate toggle failed to restore")
            quit(1)
            return
    send_key(scene, KEY_PAGEDOWN)
    if scene.get_node("SourceCreature_Daedroth").visible \
        or not scene.get_node("SourceCreature_Clannfear").visible:
        push_error("Creature variety next-species focus failed")
        quit(1)
        return
    send_key(scene, KEY_0)
    for name in types:
        if not scene.get_node("SourceCreature_" + name).visible:
            push_error("Creature variety all-species restoration failed")
            quit(1)
            return
    print("PASS source-driven creature variety: 13 original ESO meshes, 312 independent rigs, 299 shared source mesh/skin clones; material toggles and page focus")
    quit(0)
