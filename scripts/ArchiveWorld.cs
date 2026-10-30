using System;
using System.IO;
using System.Linq;
using Godot;

namespace EsoTest;

public partial class ArchiveWorld : Node3D
{
    private EsoArchive? _archive;
    private OodleDecoder? _decoder;

    public override void _Ready()
    {
        if(System.Environment.GetEnvironmentVariable("ESO_TEST_POPULATION_RAYCAST")=="1")
        {
            SourceGeometryRaycasterChecks.Run();
            NpcHoverChecks.Run();
            GetTree().Quit();
            return;
        }
        try
        {
            string root = System.Environment.GetEnvironmentVariable("ESO_ROOT") ?? "";
            if (string.IsNullOrWhiteSpace(root))
                root = "C:/ESO/The Elder Scrolls Online";

            _archive = new EsoArchive(Path.Combine(root, "depot", "eso.mnf"));
            _decoder = OodleDecoder.FromEnvironment(
                ProjectSettings.GlobalizePath("res://.research/ooz.dll"));

            GD.Print($"ESO index: {_archive.Count:N0} resources across {_archive.ArchiveCount} archives");
            if (_decoder == null)
            {
                AddMessage("Decoder unavailable. Configure ESO_OOZ_DLL or ESO_OODLE_DLL.");
                return;
            }

            // The label is a known file-table ID, not a reference to terrain.
            EsoRecord tableRecord = _archive.FindId(0x00FFFFFF).First();
            var names = ZosftTable.Parse(_archive.ReadDecoded(tableRecord, _decoder));
            GD.Print($"Resolved {names.Count:N0} resource filenames");

            var layer = new CanvasLayer();
            AddChild(layer);
            Control? referenceMap = null;
            try
            {
                referenceMap = AuridonPreview.Create(_archive, _decoder, names);
                layer.AddChild(referenceMap);
            }
            catch(Exception ex)
            {
                GD.PushWarning("2D Auridon map preview unavailable during depot update: "+ex.Message);
            }

            var bundles = TerrainBundleIndex.Discover(_archive);
            GD.Print($"Found {bundles.Count:N0} structurally identified land bundles");

            var browser = new TerrainBrowser();
            browser.Setup(_archive, _decoder, bundles, referenceMap);
            AddChild(browser);

            // Keep the authoritative source catalogue available even while
            // the user's depot update temporarily prevents 3D rendering.
            var catalogue = new EsoAssetBrowser();
            catalogue.Setup(_archive, _decoder, names, bundles, browser);
            AddChild(catalogue);
        }
        catch (Exception e)
        {
            AddMessage("Archive loading failed: " + e.Message);
            GD.PushError(e.ToString());
        }
    }

    public override void _ExitTree()
    {
        _decoder?.Dispose();
        _archive?.Dispose();
    }

    private void AddMessage(string message)
    {
        var layer = new CanvasLayer();
        AddChild(layer);
        var label = new Label { Position = new Vector2(22, 22), Text = message };
        label.AddThemeFontSizeOverride("font_size", 22);
        layer.AddChild(label);
    }
}
