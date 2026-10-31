extends SceneTree

# This test requires a local ESO depot and ignored .research decoder libraries.
func _initialize() -> void:
    call_deferred("_check")

func _check() -> void:
    var lab := (load("res://scenes/model_animation_lab.tscn") as PackedScene).instantiate()
    root.add_child(lab)
    var stage := lab.get_node("SourceModelStage")
    var loaded := false
    var summary := ""
    for i in 500:
        await physics_frame
        var mesh_root := stage.get_node("ImportedMeshes")
        loaded = mesh_root.get_child_count() > 0
        var labels := stage.find_children("*", "Label", true, false)
        summary = ""
        for label in labels:
            summary += (label as Label).text + "\n"
        if loaded and "Native survey:" in summary:
            break
    if not loaded:
        push_error("Failed to render original ESO model")
        quit(1)
        return
    var model_mesh := stage.get_node("ImportedMeshes").get_child(0) as MeshInstance3D
    var material := model_mesh.mesh.surface_get_material(0) as StandardMaterial3D
    if material == null or material.albedo_texture == null:
        push_error("Original diffuse texture was not applied to the source mesh")
        quit(1)
        return
    if "2 skeleton" in summary or "4 skeleton" in summary:
        push_error("Unexpected skeleton count")
        quit(1)
        return
    if not "Native survey: 1 skeleton(s)" in summary:
        push_error("Native animation details unavailable: " + summary)
        quit(1)
        return
    if not "Open 1.33s (4 tracks)" in summary or not "Close 0.33s (4 tracks)" in summary:
        push_error("Native animation durations/tracks wrong: " + summary)
        quit(1)
        return
    print("PASS ESO bind-pose mesh rendering and source animation metadata")
    quit(0)
