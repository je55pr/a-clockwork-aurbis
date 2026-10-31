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
    # The same viewer should inspect genuine character-equipment skinning
    # information without pretending those mesh parts are complete NPCs.
    var skinned_button: Button = null
    for item in stage.find_children("*", "Button", true, false):
        if (item as Button).text == "Skinned helmet A":
            skinned_button = item as Button
            break
    if skinned_button == null:
        push_error("Character-equipment model quick-load button missing")
        quit(1)
        return
    skinned_button.pressed.emit()
    summary = ""
    for i in 450:
        await physics_frame
        summary = ""
        for label in stage.find_children("*", "Label", true, false):
            summary += (label as Label).text + "\n"
        if "0x0029A89F:" in summary and "Skinnable vertex streams:" in summary:
            break
    if not "0x0029A89F:" in summary or not "Skinnable vertex streams: 1/1 meshes" in summary:
        push_error("ESO equipment skinning survey failed: " + summary)
        quit(1)
        return
    if not "source bone bindings [5]" in summary:
        push_error("Original equipment skeleton binding metadata unavailable")
        quit(1)
        return
    print("PASS ESO bind-pose model and character-equipment skinning metadata")
    quit(0)
