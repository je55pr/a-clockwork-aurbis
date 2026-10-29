using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>
/// Archive-sector coordinate map: IDs have a 61-column stride.
/// This is an observed archive indexing scheme, NOT a verified geographical map.
/// Click a sector to load its original landscape data.
/// </summary>
public partial class TerrainAtlas : Control
{
    public const int WidthInSectors = 61;
    private const int Cell = 10;
    private const int Left = 12;
    private const int Top = 45;
    private Dictionary<uint, int> _indexes = new();
    private int _rows;
    private uint _selectedId;
    private Label _caption = null!;
    public Action<int>? OnSectorSelected;

    public void Configure(IReadOnlyList<TerrainBundle> bundles)
    {
        _indexes = bundles.Select((bundle, i) => (bundle, i))
            .GroupBy(x => x.bundle.DescriptorId)
            .ToDictionary(g => g.Key, g => g.First().i);
        _rows = Math.Max(1, (int)(_indexes.Keys.Max() / WidthInSectors) + 1);
        CustomMinimumSize = new Vector2(640, Top + _rows * Cell + 40);
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        _caption = new Label
        {
            Position = new Vector2(Left, 7),
            Text = "ESO archive sectors | X + 61 × Y"
        };
        _caption.AddThemeFontSizeOverride("font_size", 17);
        AddChild(_caption);
        var hint = new Label
        {
            Position = new Vector2(Left, Top + _rows * Cell + 4),
            Text = "Click a coloured cell to view its terrain. Positions are archive coordinates."
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.Modulate = new Color(0.75f, 0.81f, 0.88f);
        AddChild(hint);
        QueueRedraw();
    }

    public void Highlight(uint id)
    {
        _selectedId = id;
        QueueRedraw();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.08f, 0.12f, 0.17f, 0.90f), true);

        int selectedX = (int)(_selectedId % WidthInSectors);
        int selectedY = (int)(_selectedId / WidthInSectors);
        for (int y = 0; y < _rows; y++)
        for (int x = 0; x < WidthInSectors; x++)
        {
            uint id = (uint)(x + y * WidthInSectors);
            var position = new Vector2(Left + x * Cell, Top + y * Cell);
            var cell = new Rect2(position, new Vector2(Cell - 1, Cell - 1));
            var color = new Color(0.17f, 0.23f, 0.30f, 0.45f);
            if (_indexes.ContainsKey(id))
            {
                color = new Color(0.25f, 0.58f, 0.51f, 0.94f);
                if (Math.Abs(x - selectedX) <= 1 && Math.Abs(y - selectedY) <= 1)
                    color = new Color(0.45f, 0.75f, 0.67f, 1f);
            }
            if (id == _selectedId)
                color = new Color(1.0f, 0.74f, 0.28f);
            DrawRect(cell, color, true);
        }

        // Highlight archive-grid neighbours, not physical mesh connections.
        DrawRect(new Rect2(Left + selectedX * Cell - 2, Top + selectedY * Cell - 2,
            Cell + 3, Cell + 3), new Color(1.0f, 0.85f, 0.51f), false, 2);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton button || button.ButtonIndex != MouseButton.Left || !button.Pressed)
            return;
        Vector2 p = button.Position;
        int x = Mathf.FloorToInt((p.X - Left) / Cell);
        int y = Mathf.FloorToInt((p.Y - Top) / Cell);
        if (x is < 0 or >= WidthInSectors || y < 0 || y >= _rows)
            return;
        uint id = (uint)(x + WidthInSectors * y);
        if (_indexes.TryGetValue(id, out int index))
            OnSectorSelected?.Invoke(index);
        AcceptEvent();
    }
}
