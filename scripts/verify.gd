extends SceneTree

# Smoke test: direct read-only access to all four original archive families.
const EsoSource = preload("res://scripts/eso_source.gd")

func _initialize() -> void:
	var source = EsoSource.new()
	print("ESO source: ", source.root_path)
	var all_ok := true
	for archive in source.check_archives():
		var ok: bool = archive["available"]
		print("%s | %s | %s | %s DAT files" % ["OK" if ok else "ERROR", archive["name"], archive["reason"], archive["files"]])
		if not ok:
			all_ok = false
	quit(0 if all_ok else 1)
