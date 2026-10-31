extends SceneTree

# Integration test using the same installed ESO assets and Auridon viewer as
# the user-facing -FirstPersonLab launcher. Never modifies the source depot.
func _initialize() -> void:
    call_deferred("_run")

func _run() -> void:
    var world = (load("res://scenes/first_person_lab.tscn") as PackedScene).instantiate()
    root.add_child(world)
    var archive = world.get_node("ArchiveWorld")
    var terrain: Node = null
    for child in archive.get_children():
        if child.has_node("FullResolutionTerrainDetail"):
            terrain = child.get_node("FullResolutionTerrainDetail")
            break
    if terrain == null:
        push_error("Auridon source terrain unavailable")
        quit(1)
        return
    var population = terrain.get_node_or_null("AuridonNpcPlaceholders")
    if population == null:
        push_error("Source-backed Auridon NPC population unavailable")
        quit(1)
        return
    var ready: Node = null
    for i in 6000:
        await process_frame
        ready = population.get_node_or_null("ApproximateNPCStandIns/OriginalESOHumanoidParts")
        if ready != null and ready.get_child_count() >= 16:
            break
        if i > 0 and i % 1200 == 0:
            print("NPC integration: waiting for world placements and native humanoid meshes (frame ", i, ")")
    if ready == null or ready.get_child_count() < 16:
        push_error("First-person Auridon NPC population never received real ESO humanoid models")
        quit(1)
        return
    var root_models = population.get_node("ApproximateNPCStandIns")
    var fallback = root_models.get_node_or_null("FallbackMannequins")
    if fallback != null and not fallback.is_queued_for_deletion():
        push_error("Completed source characters did not replace temporary mannequins")
        quit(1)
        return
    var instances := 0
    for part in ready.get_children():
        if part is MultiMeshInstance3D:
            instances += (part as MultiMeshInstance3D).multimesh.instance_count
    if instances < 50:
        push_error("Too few source-backed humanoid instances appeared: " + str(instances))
        quit(1)
        return
    print("PASS integrated Auridon NPC replacement using genuine source body meshes (", instances, " anatomy-part instances)")
    quit(0)
