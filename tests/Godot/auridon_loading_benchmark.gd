extends SceneTree

# Measures warm-cache render installation separately from process launch.
# Run with --headless --script and compare before/after using the same depot.
func _initialize() -> void:
    call_deferred("_run")

func _run() -> void:
    var start := Time.get_ticks_msec()
    var scene := load("res://scenes/first_person_lab.tscn") as PackedScene
    if scene == null:
        push_error("Missing first-person scene")
        quit(1)
        return
    var world := scene.instantiate()
    root.add_child(world)
    var archive := world.get_node("ArchiveWorld")
    var terrain: Node = null
    for child in archive.get_children():
        if child.has_node("FullResolutionTerrainDetail"):
            terrain = child.get_node("FullResolutionTerrainDetail")
            break
    if terrain == null:
        push_error("No Auridon terrain")
        quit(1)
        return
    var props := terrain.get_node("SourcePlacedProps")
    var npc := terrain.get_node("AuridonNpcPlaceholders")
    print("BENCH SCENE: %.3fs" % [(Time.get_ticks_msec()-start)/1000.0])
    var props_time := -1.0
    var npc_time := -1.0
    for frame in 6000:
        await process_frame
        var elapsed := (Time.get_ticks_msec()-start)/1000.0
        if props_time < 0 and props.get("HasPreparedGeometry"):
            props_time = elapsed
            print("BENCH PROPS_READY: %.3fs frame %d" % [elapsed,frame])
        var people := npc.get_node_or_null("ApproximateNPCStandIns/OriginalESOHumanoidParts")
        if npc_time < 0 and people != null and people.get("HasOriginalBodies"):
            npc_time = elapsed
            print("BENCH HUMANOIDS_READY: %.3fs frame %d" % [elapsed,frame])
        if props_time >= 0 and npc_time >= 0:
            print("BENCH TOTAL_READY: %.3fs props=%.3fs humanoids=%.3fs" % [elapsed,props_time,npc_time])
            quit(0)
            return
    push_error("Loading timed out")
    quit(1)
