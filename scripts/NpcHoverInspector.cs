using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;

namespace EsoTest;

/// <summary>
/// Cursor-following inspector for collider-free batched NPC stand-ins.
/// Left-click while pointing at a mannequin pins the card; the pinned card
/// can be scrolled and can show the full original wiki article on demand.
/// </summary>
public partial class NpcHoverInspector : CanvasLayer
{
    private SourceGeometryRaycaster _scenery = null!;
    private Func<bool>? _canHover;
    private readonly List<NpcHoverPicker.Target> _targets = new();
    private readonly List<PopulationSpawnPlanner.Placement> _placements = new();
    private IReadOnlyDictionary<string, JsonElement> _people =
        new Dictionary<string, JsonElement>();
    private readonly Dictionary<string, NpcHoverDetails.Card> _cache = new();

    private PanelContainer _panel = null!;
    private Label _title = null!;
    private Label _subtitle = null!;
    private RichTextLabel _content = null!;
    private ScrollContainer _scroller = null!;
    private Label _hint = null!;
    private Button _rawArticle = null!;
    private Button _sourceButton = null!;

    private int _current = -1;
    private bool _pinned;
    private bool _showArticle;
    private bool _enabled = true;
    private bool _firstPersonAim;
    private bool _testHold;
    private NpcHoverDetails.Card _card;
    private Vector2 _lastCursor;

    public string? HoveredId => _current >= 0 && _current < _targets.Count
        ? _targets[_current].Id : null;
    public bool IsPinned => _pinned;
    public bool IsDisplayed => _panel != null && _panel.Visible;

    public void Setup(SourceGeometryRaycaster scenery, Func<bool>? canHover = null)
    {
        _scenery = scenery;
        _canHover = canHover;
    }

    public override void _Ready()
    {
        Layer = 25;
        var panel = new PanelContainer
        {
            Name = "NpcProfileHoverCard",
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color("#121b27"),
            BorderColor = new Color("#607d98"),
            BorderWidthLeft = 1,
            BorderWidthRight = 1,
            BorderWidthTop = 1,
            BorderWidthBottom = 1,
            CornerRadiusTopLeft = 9,
            CornerRadiusTopRight = 9,
            CornerRadiusBottomLeft = 9,
            CornerRadiusBottomRight = 9,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 10,
            ContentMarginBottom = 10
        };
        panel.AddThemeStyleboxOverride("panel", panelStyle);
        AddChild(panel);
        _panel = panel;

        var content = new VBoxContainer();
        content.AddThemeConstantOverride("separation", 5);
        panel.AddChild(content);

        _title = new Label
        {
            Text = "NPC",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _title.AddThemeFontSizeOverride("font_size", 18);
        _title.AddThemeColorOverride("font_color", new Color("#f6e2ae"));
        content.AddChild(_title);

        _subtitle = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _subtitle.AddThemeFontSizeOverride("font_size", 12);
        _subtitle.AddThemeColorOverride("font_color", new Color("#b9d4e7"));
        content.AddChild(_subtitle);

        var separator = new HSeparator();
        separator.MouseFilter = Control.MouseFilterEnum.Ignore;
        content.AddChild(separator);

        _scroller = new ScrollContainer
        {
            Name = "NpcInformationScroll",
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 75f),
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        content.AddChild(_scroller);

        _content = new RichTextLabel
        {
            BbcodeEnabled = false,
            SelectionEnabled = true,
            ScrollActive = false,
            FitContent = true,
            SizeFlagsVertical = Control.SizeFlags.ShrinkBegin,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _content.AddThemeFontSizeOverride("normal_font_size", 12);
        _content.AddThemeColorOverride("default_color", new Color("#e6ecf5"));
        _scroller.AddChild(_content);

        var actions = new HBoxContainer();
        actions.AddThemeConstantOverride("separation", 7);
        actions.MouseFilter = Control.MouseFilterEnum.Ignore;
        content.AddChild(actions);
        _rawArticle = new Button
        {
            Text = "Full wiki text",
            Disabled = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _rawArticle.Pressed += ToggleArticle;
        actions.AddChild(_rawArticle);

        _sourceButton = new Button
        {
            Text = "Open UESP page",
            Disabled = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _sourceButton.Pressed += () =>
        {
            if (!string.IsNullOrWhiteSpace(_card.SourceUrl))
                OS.ShellOpen(_card.SourceUrl);
        };
        actions.AddChild(_sourceButton);

        _hint = new Label
        {
            Text = "Left-click the NPC to pin this card.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _hint.AddThemeFontSizeOverride("font_size", 11);
        _hint.AddThemeColorOverride("font_color", new Color("#91a9bc"));
        content.AddChild(_hint);

        SetProcess(true);
        SetProcessInput(true);
    }

    public void SetPopulation(IReadOnlyList<PopulationSpawnPlanner.Placement> spawns,
        IReadOnlyDictionary<string, JsonElement> sourcePeople)
    {
        _targets.Clear();
        _placements.Clear();
        _cache.Clear();
        _people = sourcePeople;
        foreach (var p in spawns)
        {
            _placements.Add(p);
            _targets.Add(new NpcHoverPicker.Target(p.Id, p.Position));
        }
        Clear();
    }

    // The first-person camera aims through the centre of the viewport while
    // the operating-system cursor is captured and cannot hover a mannequin.
    public void SetFirstPersonAim(bool enabled)
    {
        _firstPersonAim = enabled;
        if (!enabled) Clear();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) Clear();
    }

    public override void _Process(double delta)
    {
        if (_panel == null) return;
        if (!_enabled || _targets.Count == 0 || _canHover?.Invoke() == false)
        {
            Clear();
            return;
        }

        // Captured first-person mouse input aims along the camera's centre ray.
        // The ordinary free-camera inspector continues to follow the cursor.
        if (!_firstPersonAim && Input.MouseMode != Input.MouseModeEnum.Visible)
        {
            Clear();
            return;
        }

        if (_pinned || _testHold) return;
        var camera = GetViewport().GetCamera3D();
        if (camera == null || !camera.IsCurrent())
        {
            Clear();
            return;
        }

        Vector2 size = GetViewport().GetVisibleRect().Size;
        Vector2 mouse = _firstPersonAim ? size * 0.5f : GetViewport().GetMousePosition();
        if (mouse.X < 0 || mouse.Y < 0 || mouse.X >= size.X || mouse.Y >= size.Y)
        {
            Clear();
            return;
        }

        var hit = FindHoverTarget(camera, mouse);
        if (hit is null)
        {
            Clear();
            return;
        }

        if (hit.Value.Index != _current)
            ShowTarget(hit.Value.Index, mouse);
        else
            PositionNearCursor(mouse);
    }

    // Uses the same geometry picker as ordinary hovering. Only the isolated
    // screenshot smoke test calls this; no OS cursor movement is required.
    public bool PreviewHoverAt(Vector2 cursor)
    {
        var camera = GetViewport().GetCamera3D();
        if (camera == null || !camera.IsCurrent()) return false;
        var hit = FindHoverTarget(camera, cursor);
        if (hit is null) return false;
        ShowTarget(hit.Value.Index, cursor);
        _testHold = true;
        return true;
    }

    private NpcHoverPicker.Hit? FindHoverTarget(Camera3D camera, Vector2 mouse)
    {
        Vector3 origin=camera.ProjectRayOrigin(mouse);
        Vector3 direction=camera.ProjectRayNormal(mouse).Normalized();
        var direct=NpcHoverPicker.Pick(origin,direction,_targets,
            (start,end)=>_scenery.Intersects(start,end));
        if(direct.HasValue)return direct;

        // From an aerial viewpoint a mannequin may be smaller than one pixel.
        // Permit a tiny 5px screen-space tolerance only if no exact ray hits.
        // The same scene-geometry occlusion test still applies.
        int best=-1;
        float closestPixels=5f*5f,closestDepth=650f;
        Vector3 point=default;
        for(int i=0;i<_targets.Count;i++)
        {
            var target=_targets[i].Feet+new Vector3(0f,1.15f,0f);
            if(camera.IsPositionBehind(target))continue;
            Vector2 projected=camera.UnprojectPosition(target);
            float pixels=projected.DistanceSquaredTo(mouse);
            if(pixels>closestPixels)continue;
            float depth=origin.DistanceTo(target);
            if(depth>650f)continue;
            if(_scenery.Intersects(origin,target))continue;
            if(pixels<closestPixels||depth<closestDepth)
            {
                best=i;closestPixels=pixels;closestDepth=depth;point=target;
            }
        }
        return best<0?null:new NpcHoverPicker.Hit(best,point,closestDepth);
    }

    private void ShowTarget(int index, Vector2 cursor)
    {
        if (index < 0 || index >= _targets.Count) { Clear(); return; }
        _current = index;
        _pinned = false;
        _showArticle = false;
        var placement = _placements[index];
        if (!_cache.TryGetValue(placement.Id, out _card))
        {
            if (_people.TryGetValue(placement.Id, out var person))
                _card = NpcHoverDetails.Build(person, placement);
            else
                _card = new NpcHoverDetails.Card(placement.Name,
                    "Generated population placeholder",
                    "This ambient mannequin is for density testing only.\n" +
                    "No individual NPC source record was collected for it.\n" +
                    $"Preview district: {placement.Place}",
                    null, null);
            _cache[placement.Id] = _card;
        }

        _title.Text = _card.Title;
        _subtitle.Text = _card.Subtitle;
        _content.Text = _card.Body;
        _scroller.ScrollVertical = 0;
        _hint.Text = _firstPersonAim
            ? "Look away to dismiss. Esc releases the cursor; click to recapture."
            : "Click the NPC to pin, then scroll for all collected details.";
        SetInteractive(false);
        _panel.Visible = true;
        PositionNearCursor(cursor);
    }

    public void PreviewPinForScreenshot()
    {
        if (_panel==null || !_panel.Visible || _current<0)return;
        PinCurrent();
    }

    private void PositionNearCursor(Vector2 cursor)
    {
        if (!_panel.Visible) return;
        Vector2 viewport = GetViewport().GetVisibleRect().Size;
        float width = Math.Min(viewport.X - 16f, _pinned ? 490f : 365f);
        float height = Math.Min(viewport.Y - 18f, _pinned ? 540f : 305f);
        _panel.Size = new Vector2(width, height);

        float x = cursor.X + 20f;
        float y = cursor.Y + 18f;
        if (x + width > viewport.X - 8f)
            x = cursor.X - width - 20f;
        if (y + height > viewport.Y - 8f)
            y = cursor.Y - height - 18f;
        _panel.Position = new Vector2(
            Mathf.Clamp(x, 8f, Math.Max(8f, viewport.X - width - 8f)),
            Mathf.Clamp(y, 8f, Math.Max(8f, viewport.Y - height - 8f)));
        _lastCursor = cursor;
    }

    private void SetInteractive(bool interactive)
    {
        _panel.MouseFilter = interactive
            ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        _scroller.MouseFilter = interactive
            ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        _content.MouseFilter = interactive
            ? Control.MouseFilterEnum.Pass : Control.MouseFilterEnum.Ignore;
        _rawArticle.Disabled = !interactive || string.IsNullOrEmpty(_card.ArticleWikitext);
        _sourceButton.Disabled = !interactive || string.IsNullOrEmpty(_card.SourceUrl);
        _rawArticle.MouseFilter = interactive
            ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
        _sourceButton.MouseFilter = interactive
            ? Control.MouseFilterEnum.Stop : Control.MouseFilterEnum.Ignore;
    }

    private void ToggleArticle()
    {
        if (!_pinned || string.IsNullOrEmpty(_card.ArticleWikitext)) return;
        _showArticle = !_showArticle;
        _rawArticle.Text = _showArticle ? "Hide wiki text" : "Full wiki text";
        _content.Text = _showArticle
            ? _card.Body + "\n\n── Full original UESP wikitext ──\n\n" + _card.ArticleWikitext
            : _card.Body;
        _content.ScrollToLine(0);
    }

    public override void _Input(InputEvent @event)
    {
        if (_panel == null || !_panel.Visible) return;
        if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            Clear();
            return;
        }

        if (_firstPersonAim || @event is not InputEventMouseButton button ||
            button.ButtonIndex != MouseButton.Left || !button.Pressed)
            return;

        Vector2 mouse = GetViewport().GetMousePosition();
        bool insidePanel = _panel.GetGlobalRect().HasPoint(mouse);

        if (!_pinned && _current >= 0 && !insidePanel)
            PinCurrent();
        else if (_pinned && !insidePanel)
            Clear();
    }

    private void PinCurrent()
    {
        _pinned = true;
        _hint.Text = "Pinned. Scroll to read, expand the wiki text, or click elsewhere to close.";
        SetInteractive(true);
        PositionNearCursor(_lastCursor);
    }

    private void Clear()
    {
        if (_panel == null) return;
        _panel.Visible = false;
        _current = -1;
        _pinned = false;
        _testHold = false;
        _showArticle = false;
        _rawArticle.Text = "Full wiki text";
        SetInteractive(false);
    }
}
