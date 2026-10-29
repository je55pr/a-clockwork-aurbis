extends RefCounted

# No ESO resource is copied or converted. All files are accessed directly from
# the installed game using absolute paths and FileAccess.READ.
const DEFAULT_ROOT := "C:/ESO/The Elder Scrolls Online"
var root_path: String

func _init() -> void:
	root_path = OS.get_environment("ESO_ROOT")
	if root_path.is_empty():
		root_path = DEFAULT_ROOT
	root_path = root_path.replace("\\", "/").trim_suffix("/")


func check_archives() -> Array[Dictionary]:
	return [
		_check_archive("World depot", "depot/eso.mnf", "depot/eso0000.dat", "depot", "eso"),
		_check_archive("Client data", "game/client/game.mnf", "game/client/game0000.dat", "game/client", "game"),
		_check_archive("English voices", "vo_en/esoaudioen.mnf", "vo_en/esoaudioen0000.dat", "vo_en", "esoaudioen"),
		_check_archive("Soundsets", "vo_soundsets/esoaudiosoundsets.mnf", "vo_soundsets/esoaudiosoundsets0000.dat", "vo_soundsets", "esoaudiosoundsets"),
	]


func _check_archive(title: String, manifest_relative: String, data_relative: String, folder: String, data_prefix: String) -> Dictionary:
	var manifest_path := root_path.path_join(manifest_relative)
	var data_path := root_path.path_join(data_relative)
	var result := {
		"name": title,
		"manifest": manifest_relative,
		"available": false,
		"files": 0,
		"bytes": 0,
		"reason": ""
	}

	var manifest := FileAccess.open(manifest_path, FileAccess.READ)
	if manifest == null:
		result["reason"] = "Missing/unreadable manifest"
		return result
	var manifest_signature := manifest.get_buffer(4).get_string_from_ascii()
	result["bytes"] = manifest.get_length()
	manifest.close()

	var data_file := FileAccess.open(data_path, FileAccess.READ)
	if data_file == null:
		result["reason"] = "Missing/unreadable first DAT file"
		return result
	var data_signature := data_file.get_buffer(4).get_string_from_ascii()
	data_file.close()

	if manifest_signature != "MES2" or data_signature != "PES2":
		result["reason"] = "Unexpected signatures: %s / %s" % [manifest_signature, data_signature]
		return result

	var directory := DirAccess.open(root_path.path_join(folder))
	if directory != null:
		for entry in directory.get_files():
			if entry.begins_with(data_prefix) and entry.get_extension().to_lower() == "dat":
				result["files"] += 1

	result["available"] = true
	result["reason"] = "MES2 manifest / PES2 archive"
	return result
