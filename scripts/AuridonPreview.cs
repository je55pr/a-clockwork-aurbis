using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace EsoTest;

// A visual smoke test for the in-memory asset pipeline. This is ESO's existing
// 2D Auridon map, NOT the 3D world. Terrain rendering is a separate milestone.
public static class AuridonPreview
{
    public static Control Create(EsoArchive archive, OodleDecoder decoder, Dictionary<uint, string> names)
    {
        var container = new VBoxContainer
        {
            Position = new Vector2(640, 30),
            CustomMinimumSize = new Vector2(520, 600)
        };
        var title = new Label { Text = "Auridon | Original ESO map assets" };
        title.AddThemeFontSizeOverride("font_size", 20);
        container.AddChild(title);
        var detail = new Label { Text = "2D reference preview. Runtime-loaded DDS, no extracted files." };
        detail.AddThemeFontSizeOverride("font_size", 13);
        container.AddChild(detail);

        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 0);
        grid.AddThemeConstantOverride("v_separation", 0);
        var mapRoot=new Control {CustomMinimumSize=new Vector2(512,512)};
        container.AddChild(mapRoot);
        mapRoot.AddChild(grid);

        var pathIndex = names.ToDictionary(kv => kv.Value.ToLowerInvariant(), kv => kv.Key);
        int loaded = 0;
        for (int i = 0; i < 4; i++)
        {
            string name = $"/art/maps/auridon/auridon_base_{i}.dds";
            var tile = new TextureRect
            {
                CustomMinimumSize = new Vector2(256, 256),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.Scale
            };
            grid.AddChild(tile);
            if (!pathIndex.TryGetValue(name, out uint fileId))
                continue;
            foreach (var record in archive.FindId(fileId))
            {
                if (record.UncompressedSize < 128 || record.UncompressedSize > 20_000_000)
                    continue;
                byte[] bytes = archive.ReadDecoded(record, decoder);
                if (bytes.Length < 4 || bytes[0] != 'D' || bytes[1] != 'D'
                    || bytes[2] != 'S' || bytes[3] != ' ')
                    continue;
                using var img = new Image();
                if (img.LoadDdsFromBuffer(bytes) != Error.Ok || img.IsEmpty())
                    continue;
                using var texture = ImageTexture.CreateFromImage(img);
                tile.Texture = texture;
                loaded++;
                GD.Print($"Auridon map tile {i}: {img.GetWidth()}x{img.GetHeight()} from file ID {fileId:X8}");
                break;
            }
        }
        mapRoot.AddChild(new AuridonPopulationOverlay {Name="PopulationMapDots"});
        detail.Text += $" ({loaded}/4 tiles loaded)";
        var annotation=new Label {Text="Gold = named source-place anchor. Positions approximate, not NPC coordinates."};
        annotation.AddThemeFontSizeOverride("font_size",11);
        container.AddChild(annotation);
        return container;
    }
}
