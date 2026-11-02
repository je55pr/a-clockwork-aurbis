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
    if FileAccess.file_exists("res://.research/npc-rigs/creature-texture-candidates.tsv"):
        if scene.CandidateMaterialCount != 3 or not scene.HasCandidateMaterial(3):
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
