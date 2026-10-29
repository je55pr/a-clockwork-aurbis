using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>
/// In-memory 3D viewer for genuine ESO land bundles, including the independently
/// identified Auridon section 233. Terrain is read from the installed DATs.
/// </summary>
public partial class TerrainBrowser : Node3D
{
    private EsoArchive _archive = null!;
    private OodleDecoder _decoder = null!;
    private IReadOnlyList<TerrainBundle> _bundles = null!;
    private TerrainHierarchyIndex _hierarchy = null!;
    private Node3D? _terrainRoot;
    private Label _status = null!;
    private Label _briefStatus = null!;
    private LineEdit _jump = null!;
    private TerrainAtlas? _atlas;
    private Control? _toolsPanel;
    private CanvasLayer? _terrainUiLayer;
    private OptionButton? _connectedRegionPicker;
    private FixturePlacementGraph? _fixtureGraph;
    private OptionButton? _automaticRegionPicker;
    private Control? _referenceMap;
    private PanelContainer? _gapPanel;
    private TextureRect? _gapImage;
    private Label? _gapInfo;
    private Label? _gapHeading;
    private PanelContainer? _auridonComparePanel;
    private TextureRect? _elevationImage;
    private Label? _elevationInfo;
    private OptionButton? _candidatePicker;
    private TerrainBundle[] _auridonCandidates=Array.Empty<TerrainBundle>();
    private uint _comparisonSectionId;
    private int _selectedIndex;
    private Camera3D? _camera;
    private Vector3 _focus;
    private float _yaw = 0.35f;
    private float _pitch = 0.75f;
    private float _distance = 360f;
    // Free-flight: _focus/_distance are retained only for initial framing when switching scenes.
    private Vector3 _flyPosition;
    private bool _mouseLooking;
    private bool _bestView;
    public bool IsBestView => _bestView;
    private CanvasLayer? _simpleHud;
    private Label? _sceneLabel;
    private float _flySpeed = 35f;
    private ShaderMaterial? _currentLodMaterial;
    private bool _showOriginalLodNormals = true;
    private bool _currentHasNearColour;
    private bool _showOriginalNearColourTint =
        System.Environment.GetEnvironmentVariable("ESO_EXPERIMENTAL_TINT") == "1";
    private bool _showOriginalLodDiffuse =
        System.Environment.GetEnvironmentVariable("ESO_TERRAIN_ELEVATION") != "1";

    public void Setup(EsoArchive archive, OodleDecoder decoder, IReadOnlyList<TerrainBundle> bundles, Control? referenceMap)
    {
        _archive = archive;
        _decoder = decoder;
        _bundles = bundles;
        _hierarchy = TerrainHierarchyIndex.Analyse(bundles);
        GD.Print($"Terrain metadata hierarchy: {_hierarchy.Archives.Count} archives, " +
            $"{_hierarchy.TotalSectors} sections, {_hierarchy.FullSectors} complete, " +
            $"{_hierarchy.DistinctCompleteHeightLayouts} distinct complete terrain layouts, " +
            $"{_hierarchy.ReusedSectionCount} sections with matching hash-based copies");
        _referenceMap = referenceMap;
        if (_referenceMap != null)
            _referenceMap.Visible = System.Environment.GetEnvironmentVariable("ESO_CAPTURE_AURIDON") == "1";
    }

    public void SetWorkspaceVisible(bool visible)
    {
        if(_terrainUiLayer!=null)_terrainUiLayer.Visible=visible&&!_bestView;
        if(_simpleHud!=null)_simpleHud.Visible=visible&&_bestView;
        if(!visible && _referenceMap!=null)_referenceMap.Visible=false;
        if(!visible && _atlas!=null)_atlas.Visible=false;
    }

    public override void _Ready()
    {
        _camera = GetParent().GetParent().GetNodeOrNull<Camera3D>("Camera3D");
        if (_camera == null)
        {
            _camera = new Camera3D { Name = "TerrainViewerCamera" };
            AddChild(_camera);
        }
        _camera.Current = true;
        GD.Print($"TerrainBrowser camera: {_camera.GetPath()}");
        CreateSimpleHud();
        var layer = new CanvasLayer {Visible=false};
        _terrainUiLayer=layer;
        AddChild(layer);
        var panel = new PanelContainer
        {
            Position = new Vector2(14, 14),
            CustomMinimumSize = new Vector2(430, 0)
        };
        panel.AddThemeStyleboxOverride("panel",
            EsoUiStyles.Box(EsoUiStyles.Background,11,1,new Color("#3b5566"),12));
        layer.AddChild(panel);
        _toolsPanel=panel;
        _auridonCandidates=_bundles
            .Where(b => b.Tiles.Count>=350 &&
                (float)(b.MaximumX-b.OriginX+1)/(b.MaximumY-b.OriginY+1) is >= 0.35f and <= 1.10f)
            .OrderByDescending(b=>b.Tiles.Count)
            .Take(85).ToArray();

        _gapPanel = new PanelContainer {
            Position = new Vector2(552, 18),
            Visible = false,
            CustomMinimumSize = new Vector2(320, 0)
        };
        layer.AddChild(_gapPanel);
        var gapBox = new VBoxContainer();
        _gapPanel.AddChild(gapBox);
        _gapHeading = new Label { Text = "Original heightfield coverage (not sea/land)" };
        gapBox.AddChild(_gapHeading);
        _gapImage = new TextureRect {
            CustomMinimumSize = new Vector2(310, 310),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        gapBox.AddChild(_gapImage);
        _gapInfo = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        gapBox.AddChild(_gapInfo);
        var dismiss = new Button { Text = "Hide coverage map" };
        dismiss.Pressed += () => _gapPanel.Visible = false;
        gapBox.AddChild(dismiss);

        _auridonComparePanel = new PanelContainer {
            Position = new Vector2(24, 20),
            Visible = false,
            CustomMinimumSize = new Vector2(520, 0)
        };
        layer.AddChild(_auridonComparePanel);
        var compareBox = new VBoxContainer();
        _auridonComparePanel.AddChild(compareBox);
        compareBox.AddChild(new Label {
            Text = "Original source terrain | relative elevation"
        });
        _candidatePicker = new OptionButton();
        foreach(var candidate in _auridonCandidates)
            _candidatePicker.AddItem(
                $"Section {candidate.DescriptorId} ({candidate.MaximumX-candidate.OriginX+1}×" +
                $"{candidate.MaximumY-candidate.OriginY+1}; {candidate.Tiles.Count:N0} tiles)");
        _candidatePicker.ItemSelected += i => {
            int at=(int)i;
            if(at>=0&&at<_auridonCandidates.Length)
                ShowAuridonComparison(_auridonCandidates[at].DescriptorId);
        };
        compareBox.AddChild(_candidatePicker);
        var customSectionRow = new HBoxContainer();
        compareBox.AddChild(customSectionRow);
        var customSectionId = new LineEdit {
            PlaceholderText = "Any section ID (e.g. 233)",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        customSectionRow.AddChild(customSectionId);
        void ReadCustomId() {
            if(uint.TryParse(customSectionId.Text,out var id))
                ShowAuridonComparison(id);
        }
        customSectionId.TextSubmitted += _ => ReadCustomId();
        var customGo = new Button { Text = "Compare ID" };
        customGo.Pressed += ReadCustomId;
        customSectionRow.AddChild(customGo);
        _elevationImage = new TextureRect {
            CustomMinimumSize = new Vector2(480, 375),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered
        };
        compareBox.AddChild(_elevationImage);
        _elevationInfo = new Label {
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        compareBox.AddChild(_elevationInfo);
        var actions = new HBoxContainer();
        compareBox.AddChild(actions);
        var renderSelection = new Button { Text = "Render selected section" };
        renderSelection.Pressed += () => {
            uint id = _comparisonSectionId;
            CloseAuridonComparison();
            ShowCompleteSection(id);
        };
        actions.AddChild(renderSelection);
        var closeComparison = new Button { Text = "Back to explorer" };
        closeComparison.Pressed += CloseAuridonComparison;
        actions.AddChild(closeComparison);
        // Fit the growing set of research controls into the screen;
        // the list scrolls independently without covering the whole landscape.
        var scroll = new ScrollContainer {
            CustomMinimumSize = new Vector2(387, 465),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        panel.AddChild(scroll);
        EsoUiStyles.Scrollbar(scroll.GetVScrollBar());
        var vbox = new VBoxContainer {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        var advancedWidgets = new System.Collections.Generic.List<Control>();
        vbox.AddThemeConstantOverride("separation", 7);
        scroll.AddChild(vbox);

        var title = EsoUiStyles.Heading("World viewer",23);
        vbox.AddChild(title);
        var hierarchyLabel = new Label {
            Text = $"Archive hierarchy: {_hierarchy.Archives.Count} DAT containers / " +
                $"{_hierarchy.TotalSectors:N0} sections / " +
                $"{_hierarchy.DistinctCompleteHeightLayouts:N0} unique complete height layouts"
        };
        hierarchyLabel.AddThemeFontSizeOverride("font_size", 12);
        hierarchyLabel.AutowrapMode=TextServer.AutowrapMode.WordSmart;
        vbox.AddChild(hierarchyLabel);
        advancedWidgets.Add(hierarchyLabel);

        var caveat = new Label
        {
            Text = "Section 233: Auridon confirmed (ESO zone ID 381).",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        caveat.Modulate = EsoUiStyles.Accent;
        vbox.AddChild(caveat);

        _briefStatus = EsoUiStyles.Note("Choose a worldspace from the library, or enter a section ID below.");
        _briefStatus.AddThemeFontSizeOverride("font_size",14);
        vbox.AddChild(_briefStatus);
        _status = new Label {
            Text = "Preparing terrain index...",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _status.AddThemeFontSizeOverride("font_size",13);
        vbox.AddChild(_status);
        advancedWidgets.Add(_status);

        var row = new HBoxContainer();
        vbox.AddChild(row);

        var prev = new Button { Text = "‹", TooltipText="Previous section" };
        prev.Pressed += () => SelectBundle(_selectedIndex - 1);
        EsoUiStyles.ButtonStyle(prev);
        row.AddChild(prev);

        var next = new Button { Text = "›", TooltipText="Next section" };
        next.Pressed += () => SelectBundle(_selectedIndex + 1);
        EsoUiStyles.ButtonStyle(next);
        row.AddChild(next);

        _jump = new LineEdit { PlaceholderText = "Section / zone ID", CustomMinimumSize = new Vector2(150, 0), SizeFlagsHorizontal=Control.SizeFlags.ExpandFill };
        _jump.TextSubmitted += JumpTo;
        row.AddChild(_jump);

        var go = new Button { Text = "Go" };
        go.Pressed += () => JumpTo(_jump.Text);
        EsoUiStyles.ButtonStyle(go);
        row.AddChild(go);

        var verifiedLabel = EsoUiStyles.Note("Verified relative layouts (research)");
        vbox.AddChild(verifiedLabel);
        advancedWidgets.Add(verifiedLabel);
        _connectedRegionPicker = new OptionButton();
        foreach (var candidate in TerrainConnections.Confirmed)
            _connectedRegionPicker.AddItem(candidate.Label);
        _connectedRegionPicker.ItemSelected += i => ShowCatalogRegion((int)i);
        vbox.AddChild(_connectedRegionPicker);

        var automaticButton = new Button {
            Text = "Discover larger regions from original fixture IDs"
        };
        automaticButton.Pressed += DiscoverAndDisplayFixtureRegions;
        vbox.AddChild(automaticButton);
        _automaticRegionPicker = new OptionButton {
            TooltipText = "Only cycle-consistent fixture placement components are shown"
        };
        _automaticRegionPicker.AddItem("Automatic region viewer (run discovery first)");
        _automaticRegionPicker.Disabled = true;
        _automaticRegionPicker.ItemSelected += index => ShowAutoRegion((int)index);
        vbox.AddChild(_automaticRegionPicker);

        var largeSources = _bundles.OrderByDescending(b => b.Tiles.Count)
            .Take(25).ToArray();
        var sourcePicker = new OptionButton {
            TooltipText = "Complete source packages, each positioned by original tile IDs"
        };
        sourcePicker.AddItem("Browse 25 largest source landscapes");
        foreach (var source in largeSources)
            sourcePicker.AddItem(
                $"ID {source.DescriptorId}: {source.Tiles.Count:N0} tiles " +
                $"({source.MaximumX-source.OriginX+1}×{source.MaximumY-source.OriginY+1})");
        sourcePicker.ItemSelected += item => {
            int choice = (int)item-1;
            if (choice>=0 && choice<largeSources.Length)
                ShowCompleteSection(largeSources[choice].DescriptorId);
        };
        vbox.AddChild(sourcePicker);

        var comparisonButton = new Button {
            Text = "Compare terrain elevation with Auridon map"
        };
        comparisonButton.Pressed += OpenAuridonComparison;
        vbox.AddChild(comparisonButton);

        var gapsButton = new Button { Text = "Inspect holes: original tile coverage" };
        gapsButton.Pressed += InspectCurrentSectionGaps;
        vbox.AddChild(gapsButton);

        var diffuseMode = new CheckButton {
            Text = "Original ESO terrain textures",
            ButtonPressed = _showOriginalLodDiffuse,
            TooltipText = "Use original diffuse and normal material DDS in 5x5 close-ups, baked LOD diffuse in complete region views. Uncheck for elevation diagnostics."
        };
        diffuseMode.Toggled += enabled => {
            _showOriginalLodDiffuse = enabled;
            ShowCompleteSection();
        };
        vbox.AddChild(diffuseMode);
        var normalMode = new CheckButton {
            Text = "Original terrain normal maps",
            ButtonPressed = _showOriginalLodNormals
        };
        normalMode.Toggled += enabled => {
            _showOriginalLodNormals = enabled;
            _currentLodMaterial?.SetShaderParameter("use_original_normals",enabled);
            _terrainRoot?.GetNodeOrNull<FullWorldspaceDetail>(
                "FullWorldspaceDetail")?.SetNormalEnabled(enabled);
        };
        vbox.AddChild(normalMode);
        var colourMode = new CheckButton {
            Text = "ESO layout/colour overlay (diagnostic only)",
            TooltipText = "Off by default: the source 128x128 terrain-colour maps contain legacy 'Layout Map v1.2' planning annotations including Dark Anchors and Group Bosses. Not suitable as world terrain colouring.",
            ButtonPressed = _showOriginalNearColourTint
        };
        colourMode.Toggled += enabled => {
            _showOriginalNearColourTint=enabled;
            _currentLodMaterial?.SetShaderParameter(
                "apply_original_tint",enabled&&_currentHasNearColour);
            _terrainRoot?.GetNodeOrNull<FullWorldspaceDetail>(
                "FullWorldspaceDetail")?.SetTintEnabled(enabled);
        };
        vbox.AddChild(colourMode);

        var patchRow = new HBoxContainer();
        patchRow.AddChild(new Label { Text = "Close-up tile X / Z:" });
        var patchX = new SpinBox {
            MinValue = 0, MaxValue = 1000, Step = 1, Value = 23,
            CustomMinimumSize = new Vector2(68,0)
        };
        patchRow.AddChild(patchX);
        var patchZ = new SpinBox {
            MinValue = 0, MaxValue = 1000, Step = 1, Value = 25,
            CustomMinimumSize = new Vector2(68,0)
        };
        patchRow.AddChild(patchZ);
        vbox.AddChild(patchRow);
        var closeButton = new Button {
            Text = "View 5x5 tiles at FULL heightfield resolution"
        };
        closeButton.Pressed += () => ShowCompleteSection(
            closeUp: true, tileX: (int)patchX.Value, tileZ: (int)patchZ.Value);
        EsoUiStyles.ButtonStyle(closeButton,true);
        vbox.AddChild(closeButton);
        var layerRow = new HBoxContainer();
        layerRow.AddChild(new Label { Text = "Original shader layer:" });
        var paintChannel = new SpinBox {
            MinValue = 0, MaxValue = 15, Value = 0, Step = 1,
            CustomMinimumSize = new Vector2(75,0)
        };
        layerRow.AddChild(paintChannel);
        var paintButton = new Button { Text = "Inspect" };
        paintButton.Pressed += () => InspectPaintMask(
            (int)patchX.Value,(int)patchZ.Value,(int)paintChannel.Value);
        layerRow.AddChild(paintButton);
        vbox.AddChild(layerRow);

        var fullDetailButton = new Button { Text = "Render entire worldspace at original texture resolution" };
        fullDetailButton.TooltipText="Full 65x65 terrain and authentic near-field material textures across the entire source worldspace. May use substantial GPU memory.";
        fullDetailButton.Pressed += () => ShowCompleteSection(fullWorldspaceDetail:true);
        EsoUiStyles.ButtonStyle(fullDetailButton,true);
        vbox.AddChild(fullDetailButton);
        var vegetationButton = new Button {
            Text = "Vulkhel Guard candidate: original vegetation test",
            TooltipText = "Source-only vegetation fixtures around Auridon's two southernmost " +
                "lighthouse placements; 3D model meshes and coordinates come from ESO. " +
                "The precise Vulkhel Guard boundary is not independently verified."
        };
        vegetationButton.Pressed += ShowBestAvailableScene;
        EsoUiStyles.ButtonStyle(vegetationButton,true);
        vbox.AddChild(vegetationButton);

        var allTilesButton = new Button { Text = "Render all tiles for selected section" };
        allTilesButton.Pressed += () => ShowCompleteSection();
        EsoUiStyles.ButtonStyle(allTilesButton);
        vbox.AddChild(allTilesButton);

        var largeRegionButton = new Button { Text = "View large section 233 (1,750 tiles)" };
        largeRegionButton.Pressed += () => ShowCompleteSection(233);
        EsoUiStyles.ButtonStyle(largeRegionButton);
        vbox.AddChild(largeRegionButton);

        var extendedButton = new Button { Text = "Legacy 55-tile comparison (961, 955, 996)" };
        extendedButton.Pressed += ShowExtendedLandscape;
        vbox.AddChild(extendedButton);
        var joinButton = new Button { Text = "Legacy 50-tile comparison (868, 869)" };
        joinButton.Pressed += ShowJoinedLandscape;
        vbox.AddChild(joinButton);

        var toggle = new Button { Text = "Toggle Auridon 2D reference" };
        toggle.Pressed += () =>
        {
            if (_referenceMap != null) _referenceMap.Visible = !_referenceMap.Visible;
        };
        vbox.AddChild(toggle);

        var coords = new HBoxContainer();
        vbox.AddChild(coords);
        foreach (var (name, dx, dy) in new[] {
            ("North", 0, -1), ("South", 0, 1), ("West", -1, 0), ("East", 1, 0)
        })
        {
            var button = new Button { Text = name };
            int localX = dx, localY = dy;
            button.Pressed += () => MoveSector(localX, localY);
            coords.AddChild(button);
        }

        var help = new Label { Text = "FREE CAMERA | Right mouse: look | WASD: fly | E/Space: up | Q/C: down | Shift/Alt: speed | Wheel: speed | H: terrain UI | B: asset browser" };
        help.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        help.Modulate = new Color(0.70f, 0.79f, 0.83f);
        vbox.AddChild(help);

        if (_bundles.Count > 0)
        {
            _atlas = new TerrainAtlas { Position = new Vector2(565, 18) };
            _atlas.Configure(_bundles);
            _atlas.Visible=false;
            _atlas.OnSectorSelected = SelectBundle;
            layer.AddChild(_atlas);
            var overview = new Button { Text = "Show / hide archive grid" };
            overview.Pressed += () => _atlas.Visible = !_atlas.Visible;
            vbox.AddChild(overview);
            advancedWidgets.Add(overview);
        }

        var advancedToggle = new Button {Text="Advanced tools  ▾"};
        EsoUiStyles.ButtonStyle(advancedToggle);
        vbox.AddChild(advancedToggle);
        var advanced = new VBoxContainer { Visible=false };
        advanced.AddThemeConstantOverride("separation",7);
        vbox.AddChild(advanced);
        advancedToggle.Pressed += () => {
            advanced.Visible=!advanced.Visible;
            advancedToggle.Text=advanced.Visible ? "Advanced tools  ▴" : "Advanced tools  ▾";
        };
        advancedWidgets.AddRange(new Control[]{
            _connectedRegionPicker!, automaticButton, _automaticRegionPicker!,
            sourcePicker, comparisonButton, gapsButton, diffuseMode, normalMode,
            colourMode, layerRow, extendedButton, joinButton, toggle, coords
        });
        foreach(Control control in advancedWidgets)
            control.Reparent(advanced);

        if (_bundles.Count == 0)
        {
            _status.Text = "No terrain bundles found in the installation.";
            return;
        }

        // Default to an entire validated original source region, not an
        // arbitrary 25-tile corner of several different-sized regions.
        string requested = System.Environment.GetEnvironmentVariable("ESO_TERRAIN_BUNDLE") ?? "";
        string region = System.Environment.GetEnvironmentVariable("ESO_REGION_INDEX") ?? "";
        string full = System.Environment.GetEnvironmentVariable("ESO_FULL_SECTION") ?? "";
        string detail = System.Environment.GetEnvironmentVariable("ESO_DETAIL_PATCH") ?? "";
        string[] detailParts = detail.Split(',');
        if (detailParts.Length == 2 &&
            int.TryParse(detailParts[0],out int detailX) &&
            int.TryParse(detailParts[1],out int detailZ))
            ShowCompleteSection(uint.TryParse(full,out uint detailId) ? detailId : 233u,
                closeUp: true, tileX: detailX, tileZ: detailZ);
        else if (uint.TryParse(full, out uint fullId))
            ShowCompleteSection(fullId,
                fullWorldspaceDetail:System.Environment.GetEnvironmentVariable("ESO_FULL_RES_WORLDSPACE")=="1");
        else if (System.Environment.GetEnvironmentVariable("ESO_VULKHEL_VEGETATION") == "1")
            ShowBestAvailableScene();
        else if (System.Environment.GetEnvironmentVariable("ESO_AUTO_REGION") == "1")
            DiscoverAndDisplayFixtureRegions();
        else if (int.TryParse(region, out int regionIndex) && regionIndex >= 0 &&
            regionIndex < TerrainConnections.Confirmed.Count)
        {
            _connectedRegionPicker?.Select(regionIndex);
            ShowCatalogRegion(regionIndex);
        }
        else if (requested.Length > 0) JumpTo(requested);
        else
            ShowBestAvailableScene();
        if (System.Environment.GetEnvironmentVariable("ESO_GAP_DIAGNOSTIC") == "1")
            InspectCurrentSectionGaps();
        string mask = System.Environment.GetEnvironmentVariable("ESO_PAINT_MASK") ?? "";
        var maskParts=mask.Split(',');
        if(maskParts.Length==3 &&
            int.TryParse(maskParts[0],out int mx) &&
            int.TryParse(maskParts[1],out int mz) &&
            int.TryParse(maskParts[2],out int mc))
            InspectPaintMask(mx,mz,mc);
        if (System.Environment.GetEnvironmentVariable("ESO_COMPARE_AURIDON") == "1")
            OpenAuridonComparison();
        if (System.Environment.GetEnvironmentVariable("ESO_CAPTURE_SCREENSHOT") == "1")
            CaptureDebugFrame();
    }

    private async void CaptureDebugFrame()
    {
        if (System.Environment.GetEnvironmentVariable("ESO_CAPTURE_CLEAN") == "1") {
            if (_toolsPanel != null) _toolsPanel.Visible = false;
            if (_atlas != null) _atlas.Visible = false;
        }
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (int.TryParse(System.Environment.GetEnvironmentVariable("ESO_CAPTURE_DELAY_MS"),out int delay) && delay>0)
            await ToSignal(GetTree().CreateTimer(Math.Min(delay,20000)/1000.0),SceneTreeTimer.SignalName.Timeout);
        var image = GetViewport().GetTexture().GetImage();
        string path = ProjectSettings.GlobalizePath("res://.research/terrain-preview.png");
        GD.Print($"Debug screenshot status: {image.SavePng(path)} {image.GetWidth()}x{image.GetHeight()}");
        GetTree().Quit();
    }

    private void MoveSector(int dx, int dy)
    {
        uint currentId = _bundles[_selectedIndex].DescriptorId;
        int x = (int)(currentId % TerrainAtlas.WidthInSectors) + dx;
        int y = (int)(currentId / TerrainAtlas.WidthInSectors) + dy;
        if (x < 0 || x >= TerrainAtlas.WidthInSectors || y < 0)
            return;
        uint id = (uint)(x + y * TerrainAtlas.WidthInSectors);
        int next = -1;
        for (int i = 0; i < _bundles.Count; i++)
            if (_bundles[i].DescriptorId == id) { next = i; break; }
        if (next >= 0) SelectBundle(next);
        else _status.Text = $"Archive sector ({x},{y}), ID {id}, has no readable terrain bundle.";
    }

    private void JumpTo(string value)
    {
        // A grid coordinate such as "15,6" is the same sector as "id:381".
        string[] coordinates = value.Split(',');
        if (coordinates.Length == 2 &&
            int.TryParse(coordinates[0].Trim(), out int x) &&
            int.TryParse(coordinates[1].Trim(), out int y) &&
            x >= 0 && x < TerrainAtlas.WidthInSectors && y >= 0)
        {
            uint id = (uint)(x + TerrainAtlas.WidthInSectors * y);
            int found = -1;
            for (int i = 0; i < _bundles.Count; i++)
                if (_bundles[i].DescriptorId == id) { found = i; break; }
            if (found >= 0) SelectBundle(found);
            else _status.Text = $"No readable terrain bundle at archive sector ({x},{y}).";
            return;
        }
        if (value.StartsWith("id:", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(value.AsSpan(3), out uint descriptorId))
        {
            int found = -1;
            for (int i = 0; i < _bundles.Count; i++)
                if (_bundles[i].DescriptorId == descriptorId) { found = i; break; }
            if (found >= 0)
                SelectBundle(found);
            else
                _status.Text = $"No indexed terrain bundle with descriptor ID {descriptorId}";
            return;
        }
        if (int.TryParse(value, out int index))
            SelectBundle(index);
    }

    private readonly List<FixturePlacementGraph.Component> _autoCandidates = new();

    private void DiscoverAndDisplayFixtureRegions()
    {
        _status.Text = "Reading ESO fixtures and their repeated world positions...";
        try
        {
            _fixtureGraph ??= FixturePlacementGraph.Discover(
                _archive, _decoder, minWitnesses: 20);
            _autoCandidates.Clear();
            _autoCandidates.AddRange(_fixtureGraph.Components
                .Where(c => c.Count >= 3 && c.ConsistencyErrors == 0));
            if (_automaticRegionPicker == null)
                return;
            _automaticRegionPicker.Clear();
            foreach (var component in _autoCandidates)
                _automaticRegionPicker.AddItem(
                    $"{component.Count} sections | {component.EdgeCount} verified links");
            _automaticRegionPicker.Disabled = _autoCandidates.Count == 0;
            GD.Print($"Fixture graph: {_fixtureGraph.FixtureCount:N0} fixtures, " +
                $"{_fixtureGraph.Links.Count} evidence-backed links, " +
                $"{_fixtureGraph.Components.Count} spatial components, " +
                $"{_autoCandidates.Count} cycle-consistent components with 3+ sections");
            if (_autoCandidates.Count == 0)
            {
                _status.Text = "No contradiction-free fixture components have 3+ sections.";
                return;
            }
            _automaticRegionPicker.Select(0);
            ShowAutoRegion(0);
        }
        catch (Exception error)
        {
            _status.Text = "Fixture-based placement failed: " + error.Message;
            GD.PushError(error.ToString());
        }
    }

    private void ShowAutoRegion(int index)
    {
        if (index < 0 || index >= _autoCandidates.Count)
            return;
        var group = _autoCandidates[index];
        _status.Text = $"Validating original terrain in {group.Count} positioned sections...";
        try
        {
            var result = AutoRegionRenderer.Create(
                _archive, _decoder, _bundles, group);
            using var shader = new Shader
            {
                Code = @"shader_type spatial;
render_mode unshaded, cull_disabled;
uniform float maximum_height = 500.0;
varying float elevation;
void vertex() { elevation = VERTEX.y; }
void fragment() {
    float t = clamp(elevation/max(maximum_height,0.01), 0.0, 1.0);
    ALBEDO = mix(
        mix(vec3(0.23,0.45,0.32),vec3(0.54,0.51,0.40),smoothstep(0.15,0.7,t)),
        vec3(0.78,0.76,0.70),smoothstep(0.75,1.0,t));
}"
            };
            using var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter(
                "maximum_height", result.HighestHeight-result.LowestHeight);
            using var mesh = result.Mesh;
            var root = new Node3D { Name = "FixturePositionedTerrain" };
            root.AddChild(new MeshInstance3D
            {
                Name = "UnambiguousOriginalLandTiles",
                Mesh = mesh,
                MaterialOverride = material
            });
            if (_terrainRoot != null)
                _terrainRoot.QueueFree();
            AddChild(root);
            _terrainRoot = root;
            if (_atlas != null) _atlas.Visible = false;
            _focus = result.ViewCenter;
            _distance = result.SuggestedDistance * 0.60f;
            _yaw = 0.52f;
            _pitch = 1.08f;
            UpdateCamera();
            _status.Text =
                $"{result.SectionCount} fixture-positioned sections (experimental)\n" +
                $"Safe terrain cells: {result.CellsRendered}/{result.CellsAvailable}; " +
                $"ambiguous: {result.AmbiguousCells}; unreadable: {result.UndecodableCells}\n" +
                $"Unresolved edge joins: {result.MismatchedNeighbourSeams}; " +
                $"low-detail triangles: {result.Triangles:N0}\n" +
                "Incomplete original terrain, not a verified named ESO zone.";
            GD.Print("AUTOMATIC REGION " + _status.Text);
        }
        catch (Exception error)
        {
            _status.Text = "Automatic region rejected: " + error.Message;
            GD.PushError(error.ToString());
        }
    }

    private void OpenAuridonComparison()
    {
        if(_auridonCandidates.Length==0||_auridonComparePanel==null)
            return;
        _toolsPanel?.Hide();
        if(_terrainRoot!=null)_terrainRoot.Visible=false;
        _gapPanel?.Hide();
        _atlas?.Hide();
        if(_referenceMap!=null) _referenceMap.Visible=true;
        _auridonComparePanel.Visible=true;

        uint current=_bundles[_selectedIndex].DescriptorId;
        int chosen=Array.FindIndex(_auridonCandidates,b=>b.DescriptorId==current);
        if(chosen<0)chosen=Array.FindIndex(_auridonCandidates,b=>b.DescriptorId==233);
        if(chosen<0)chosen=0;
        _candidatePicker?.Select(chosen);
        ShowAuridonComparison(_auridonCandidates[chosen].DescriptorId);
    }

    private void CloseAuridonComparison()
    {
        if(_auridonComparePanel!=null)_auridonComparePanel.Visible=false;
        if(_referenceMap!=null)_referenceMap.Visible=false;
        if(_toolsPanel!=null)_toolsPanel.Visible=true;
        if(_terrainRoot!=null)_terrainRoot.Visible=true;
    }

    private void ShowAuridonComparison(uint sectionId)
    {
        if(_elevationImage==null||_elevationInfo==null)return;
        var source=_bundles.FirstOrDefault(b=>b.DescriptorId==sectionId);
        if(source==null)return;
        _comparisonSectionId=sectionId;
        _elevationInfo.Text=$"Reading {source.Tiles.Count:N0} original heightfield records...";
        try
        {
            var generated=TerrainElevationOverview.Generate(_archive,_decoder,source);
            using var image=generated.Image;
            _elevationImage.Texture=ImageTexture.CreateFromImage(image);
            _elevationInfo.Text=
                $"Section {sectionId} | {generated.Width} × {generated.Height} tiles\n"+
                $"Elevation 5th–95th percentile: {generated.LowReference:F0} to "+
                $"{generated.HighReference:F0}\n"+
                $"Missing tiles: {generated.Missing}; unreadable: {generated.Failed}\n"+
                "Blue-green = lower height; sand = higher. This is NOT a coastline "+
                "or a verified Auridon match. Compare original shapes and landmarks.";
            GD.Print($"AURIDON COMPARISON section={sectionId}, "+
                $"tiles={generated.Tiles}, missing={generated.Missing}, "+
                $"altitudeRange={generated.LowReference:F1}..{generated.HighReference:F1}");
        }
        catch(Exception error)
        {
            _elevationInfo.Text="Terrain overview failed: "+error.Message;
            GD.PushError(error.ToString());
        }
    }

    private void InspectPaintMask(int tileX,int tileZ,int layer)
    {
        if(_gapPanel==null||_gapImage==null||_gapInfo==null||_gapHeading==null)
            return;
        var section=_bundles[_selectedIndex];
        try {
            var paint=TerrainPaintMasks.ReadPatch(
                _archive,_decoder,section,tileX,tileZ,layer);
            var summary=TerrainPaintMasks.SummarizePatch(
                _archive,_decoder,section,tileX,tileZ);
            using var image=paint.Image;
            _gapImage.Texture=ImageTexture.CreateFromImage(image);
            _gapHeading.Text=$"Original ESO shader material layer {layer}";
            _gapInfo.Text=
                $"Source section {section.DescriptorId}; centred on tiles ({tileX},{tileZ})\n"+
                $"Decoded original tiles: {paint.TilesRead}/25\n"+
                $"Nonzero mask pixels: {paint.NonzeroPixels:N0}\n"+
                $"Mean authored weight: {paint.MeanWeight:P1}\n"+
                $"Active source layers: {summary.ActiveChannels}\n"+
                $"Weight totals exactly 255: {summary.ExactlyNormalizedPixels:N0}/{summary.PixelCount:N0}\n"+
                "Layer numbering uses ESO shader BGRA order. White = painted; black = absent. Actual " +
                "texture assignments remain unidentified.";
            _gapPanel.Visible=true;
            GD.Print($"PAINT MASK section={section.DescriptorId} layer={layer} "+
                $"tiles={paint.TilesRead} nonzero={paint.NonzeroPixels} "+
                $"mean={paint.MeanWeight:F4}");
        }
        catch(Exception e) {
            _status.Text="Could not decode original paint mask: "+e.Message;
            GD.PushError(e.ToString());
        }
    }

    private void InspectCurrentSectionGaps()
    {
        if(_bundles.Count==0||_gapPanel==null||_gapImage==null||_gapInfo==null)
            return;
        var section=_bundles[_selectedIndex];
        _status.Text=$"Examining {section.Tiles.Count:N0} source heightfields in section {section.DescriptorId}...";
        try
        {
            var diag=TerrainGapDiagnostics.Scan(_archive,_decoder,section);
            using var image=diag.Image;
            _gapImage.Texture=ImageTexture.CreateFromImage(image);
            if(_gapHeading!=null)
                _gapHeading.Text="Original heightfield coverage (not sea/land)";
            _gapInfo.Text=
                $"ID {diag.SectionId} | {diag.Width} × {diag.Height} tile positions\n" +
                $"Green: {diag.ValidTiles:N0} normal\n" +
                $"Yellow: {diag.BorderZeroTiles:N0} border zeros\n" +
                $"Orange: {diag.InteriorZeroTiles:N0} interior zeros\n" +
                $"Red: {diag.AllZeroTiles:N0} fully zero\n" +
                $"Grey: {diag.MissingTiles:N0} absent files\n" +
                $"Purple: {diag.InvalidTiles:N0} unreadable\n" +
                $"Skipped preview quads: {diag.DroppedPreviewQuads:N0}";
            _gapPanel.Visible=true;
            _status.Text=
                $"Coverage: {diag.TotalTiles:N0} original tiles; "+
                $"{diag.MissingTiles} missing, {diag.InvalidTiles} undecodable.\n" +
                $"Zero-bearing tiles: {diag.BorderZeroTiles} border-only, "+
                $"{diag.InteriorZeroTiles} interior, {diag.AllZeroTiles} all zero.\n" +
                $"{diag.DroppedPreviewQuads:N0} quads are currently discarded by zero-height test.";
            GD.Print("TERRAIN GAP DIAGNOSTIC "+_status.Text.Replace("\n"," | "));
        }
        catch(Exception ex)
        {
            _status.Text="Gap scan failed: "+ex.Message;
            GD.PushError(ex.ToString());
        }
    }

    public void OpenSectionFromCatalogue(uint descriptorId,bool fullDetail=false) =>
        ShowCompleteSection(descriptorId,fullWorldspaceDetail:fullDetail);

    private void CreateSimpleHud()
    {
        var layer=new CanvasLayer{Name="WorldHud",Layer=2,Visible=false};
        _simpleHud=layer;
        AddChild(layer);
        var panel=new PanelContainer{
            Position=new Vector2(16,16),
            CustomMinimumSize=new Vector2(350,0),
            MouseFilter=Control.MouseFilterEnum.Ignore
        };
        panel.AddThemeStyleboxOverride("panel",
            EsoUiStyles.Box(EsoUiStyles.Background,10,1,new Color("#324b5b"),12));
        layer.AddChild(panel);
        var content=new VBoxContainer();
        content.AddThemeConstantOverride("separation",5);
        panel.AddChild(content);
        var title=new Label{Text="SOUTHERN AURIDON  |  SOURCE SCENE"};
        title.AddThemeFontSizeOverride("font_size",16);
        content.AddChild(title);
        _sceneLabel=new Label{
            Text="Loading original landscape...",
            AutowrapMode=TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize=new Vector2(350,0)
        };
        _sceneLabel.AddThemeFontSizeOverride("font_size",12);
        content.AddChild(_sceneLabel);
        var help=new Label{
            Text="WASD move | Right mouse look | E/Q up/down | Shift sprint | Wheel speed",
            AutowrapMode=TextServer.AutowrapMode.WordSmart
        };
        help.AddThemeFontSizeOverride("font_size",11);
        help.Modulate=new Color(0.75f,0.82f,0.88f);
        content.AddChild(help);
    }

    // Sample source elevations at the selected position; the camera is
    // ground-level, not an overhead orbit or a paper-map approximation.
    private void PositionGroundCameraAtSource(TerrainBundle bundle,int centerX,
        int centerZ,float lowestHeight,Vector3 localSourcePosition)
    {
        if(_camera==null)return;
        float worldX=(centerX+0.5f)*100f+localSourcePosition.X;
        float worldZ=(centerZ+0.5f)*100f+localSourcePosition.Z;
        int tx=Mathf.FloorToInt(worldX/100f),tz=Mathf.FloorToInt(worldZ/100f);
        var record=bundle.Tiles.FirstOrDefault(r=>
            (int)(r.FileId>>16)==tx&&(int)(r.FileId&0xffff)==tz);
        if(record.UncompressedSize==0)
            throw new InvalidOperationException("Camera tile missing from ESO source");
        if(!LandTile.TryParse(_archive.ReadDecoded(record,_decoder),out var tile)||
            tile==null)
            throw new InvalidOperationException("Cannot decode camera heightfield");
        float u=Mathf.Clamp((worldX-tx*100f)/100f,0f,1f)*64f;
        float v=Mathf.Clamp((worldZ-tz*100f)/100f,0f,1f)*64f;
        int ix=Mathf.FloorToInt(u),iz=Mathf.FloorToInt(v);
        int nx=Math.Min(ix+1,64),nz=Math.Min(iz+1,64);
        float a=tile.Samples[iz*65+ix],b=tile.Samples[iz*65+nx];
        float c=tile.Samples[nz*65+ix],e=tile.Samples[nz*65+nx];
        if(a==0||b==0||c==0||e==0)
            throw new InvalidOperationException("Camera would spawn in height padding");
        float h=Mathf.Lerp(Mathf.Lerp(a,b,u-ix),Mathf.Lerp(c,e,u-ix),v-iz);
        _flyPosition=new Vector3(localSourcePosition.X,h-lowestHeight+2.15f,
            localSourcePosition.Z);
        _yaw=0.45f;
        _pitch=0.05f;
        _flySpeed=8f;
        _camera.Position=_flyPosition;
        _camera.Rotation=new Vector3(-_pitch,_yaw,0f);
        GD.Print($"SOURCE GROUND CAMERA: ({worldX:F2},{worldZ:F2}) "+
            $"heightfield={h:F2}, eyeHeight=2.15, section={bundle.DescriptorId}");
    }
    private void ShowBestAvailableScene()
    {
        _bestView=true;
        if(_terrainUiLayer!=null)_terrainUiLayer.Visible=false;
        if(_simpleHud!=null)_simpleHud.Visible=true;
        ShowCompleteSection(233u,closeUp:true,tileX:23,tileZ:39,vegetationTest:true);
    }

    private void ShowCompleteSection(uint? descriptorId = null,
        bool closeUp = false, int tileX = 23, int tileZ = 25,
        bool fullWorldspaceDetail=false, bool vegetationTest=false)
    {
        if (_bundles.Count == 0) return;
        uint id = descriptorId ?? _bundles[_selectedIndex].DescriptorId;
        TerrainBundle? bundle = _bundles.FirstOrDefault(b => b.DescriptorId == id);
        if (bundle == null)
        {
            _status.Text = $"No source terrain with section ID {id}.";
            _briefStatus.Text = _status.Text;
            return;
        }
        _status.Text = $"Decoding the complete original terrain in section {id} ({bundle.Tiles.Count} tiles)...";
        _briefStatus.Text=$"Loading source section {id}…";
        try
        {
            var origin = new Dictionary<uint, FixturePlacementGraph.Position>
            {
                [id] = new(0, 0)
            };
            var group = new FixturePlacementGraph.Component(origin, 0, 0, 0);
            var result = AutoRegionRenderer.Create(
                _archive, _decoder, _bundles, group,
                sampleStep: closeUp ? 1 : 4,
                detailCenter: closeUp ? new Vector2I(tileX,tileZ) : null);
            var root = new Node3D {
                Name = closeUp ? "FullResolutionTerrainDetail" : "CompleteOriginalSection"
            };
            var shader = new Shader
            {
                Code = @"shader_type spatial;
render_mode unshaded, cull_disabled;
uniform sampler2D full_detail_mask : filter_nearest, repeat_disable;
uniform bool use_full_detail_mask=false;
uniform ivec2 full_detail_mask_origin=ivec2(0,0);
uniform ivec2 full_detail_mask_size=ivec2(1,1);
uniform float height_range = 250.0;
varying float elevation;
void vertex() { elevation = VERTEX.y; }
void fragment() {
    ivec2 coord=ivec2(floor(UV2))-full_detail_mask_origin;
    if(use_full_detail_mask && all(greaterThanEqual(coord,ivec2(0))) &&
       all(lessThan(coord,full_detail_mask_size)) &&
       texture(full_detail_mask,(vec2(coord)+vec2(0.5))/vec2(full_detail_mask_size)).r>0.5)
        discard;
    float t = clamp(elevation/max(height_range,0.01),0.0,1.0);
    ALBEDO = mix(
        mix(vec3(0.19,0.43,0.30),vec3(0.54,0.49,0.36),smoothstep(0.15,0.72,t)),
        vec3(0.76,0.73,0.70),smoothstep(0.72,1.0,t));
}"
            };
            var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter(
                "height_range", result.HighestHeight - result.LowestHeight);
            ShaderMaterial selectedMaterial = material;
            _currentLodMaterial = null;
            _currentHasNearColour = false;
            string textureStatus = "elevation diagnostic";
            if (_showOriginalLodDiffuse)
            {
                if(closeUp)
                {
                    try
                    {
                        TerrainNearFieldColors.Result? sourceColour=null;
                        try
                        {
                            sourceColour=TerrainNearFieldColors.LoadPatch(
                                _archive,_decoder,bundle,tileX,tileZ);
                        }
                        catch(Exception e)
                        {
                            GD.PushWarning("Original tile colour unavailable: "+e.Message);
                        }
                        var near=TerrainNearFieldMaterials.LoadPatch(
                            _archive,_decoder,bundle,tileX,tileZ,sourceColour,
                            _showOriginalLodNormals,_showOriginalNearColourTint);
                        selectedMaterial=near.Material;
                        _currentLodMaterial=selectedMaterial;
                        _currentHasNearColour=sourceColour!=null;
                        textureStatus=$"ESO original NEAR-FIELD materials: {near.UniqueMaterials} " +
                            $"material IDs, {near.SourceTiles} land tiles, " +
                            $"{near.DiffuseDds} original diffuse/normal DDS pairs. " +
                            "UV repetition provisional.";
                        GD.Print("TERRAIN NEAR-FIELD MATERIAL section="+id+" "+textureStatus);
                    }
                    catch(Exception e)
                    {
                        GD.PushWarning("Original near-field materials unavailable: "+e.Message);
                    }
                }
                if(_currentLodMaterial==null)
                try
                {
                    var lod = TerrainLodTextures.Load(_archive, _decoder, bundle);
                    TerrainNearFieldColors.Result? closeColour=null;
                    if(closeUp) {
                        try {
                            closeColour=TerrainNearFieldColors.LoadPatch(
                                _archive,_decoder,bundle,tileX,tileZ);
                            GD.Print($"ORIGINAL TILE COLOUR loaded {closeColour.LoadedTiles} tiles at {tileX},{tileZ}");
                        }
                        catch(Exception colourError){
                            GD.PushWarning("Original terrain-colour unavailable: "+colourError.Message);
                        }
                    }
                    _currentHasNearColour = closeColour!=null;
                    selectedMaterial = TerrainLodTextures.CreateMaterial(
                        lod,_showOriginalLodNormals,closeColour,
                        _currentHasNearColour&&_showOriginalNearColourTint);
                    _currentLodMaterial = selectedMaterial;
                    textureStatus = $"ESO baked LOD diffuse: {lod.OriginalDdsCount} DDS layers" +
                        (lod.HasOriginalNormals ? " + original DXT5 terrain normals" : " (original normal map unavailable)");
                    if(closeColour!=null)
                        textureStatus += "; 25 original source-colour tiles available for comparison";
                    GD.Print($"TERRAIN LOD MATERIAL section={id} " + textureStatus);
                }
                catch (Exception textureError)
                {
                    textureStatus = "LOD DDS unavailable (elevation fallback): " + textureError.Message;
                    GD.PushWarning(textureStatus);
                }
            }
            var mesh = result.Mesh;
            root.AddChild(new MeshInstance3D
            {
                Name = $"FullSection_{id}", Mesh = mesh, MaterialOverride = selectedMaterial
            });
            if (_currentLodMaterial != null)
            {
                root.AddChild(new DirectionalLight3D {
                    Name = "TerrainInspectionSun",
                    RotationDegrees = new Vector3(-52f,25f,0f),
                    LightEnergy = 2.0f,
                    ShadowEnabled = false
                });
                root.AddChild(new WorldEnvironment {
                    Name = "TerrainInspectionAmbient",
                    Environment = new Godot.Environment {
                        AmbientLightSource = Godot.Environment.AmbientSource.Color,
                        AmbientLightColor = new Color(0.90f,0.94f,1.0f),
                        AmbientLightEnergy = 1.05f,
                        BackgroundMode = Godot.Environment.BGMode.Color,
                        BackgroundColor = new Color(0.12f,0.19f,0.25f)
                    }
                });
            }
            if (_terrainRoot != null) _terrainRoot.QueueFree();
            AddChild(root);
            _terrainRoot = root;
            for (int i = 0; i < _bundles.Count; i++)
                if (_bundles[i].DescriptorId == id) { _selectedIndex = i; break; }
            _jump.Text = $"id:{id}";
            _atlas?.Highlight(id);
            if (_atlas != null) _atlas.Visible = false;
            _focus = result.ViewCenter;
            _distance = closeUp ?
                Math.Clamp(result.SuggestedDistance*0.26f,170f,540f) :
                result.SuggestedDistance*0.73f;
            _yaw = System.Environment.GetEnvironmentVariable("ESO_CAPTURE_TOPDOWN") == "1" ? 0f : 0.50f;
            _pitch = System.Environment.GetEnvironmentVariable("ESO_CAPTURE_TOPDOWN") == "1" ? 1.56f : (closeUp ? 0.68f : 1.13f);
            UpdateCamera();
            if(closeUp)
                PlaceFreeCameraNearTerrain(bundle,tileX,tileZ,result.LowestHeight);
            _status.Text =
                $"{(closeUp ? "FULL 65x65 CLOSE-UP" : "Complete source")} section {id} | {result.CellsRendered:N0} original tiles\n" +
                $"Bounds: ({bundle.OriginX},{bundle.OriginY}) to ({bundle.MaximumX},{bundle.MaximumY})\n" +
                $"Missing source tiles: {result.UndecodableCells} | source seam mismatches: {result.MismatchedNeighbourSeams}\n" +
                textureStatus + "\n" +
                (closeUp && textureStatus.StartsWith("ESO original NEAR-FIELD")
                    ? "Original land palettes and painted DDS surfaces; UV transforms provisional."
                    : "Source LOD is baked colour, not yet the original near-field material blending.");
            _briefStatus.Text=$"{(closeUp ? "Full-resolution close-up" : "Full region")} · " +
                $"section {id} · {result.CellsRendered:N0} tiles";
            GD.Print("FULL SECTION " + _status.Text);
            if(vegetationTest && closeUp)
            {
                var props=new SourcePropLayer{Name="SourcePlacedProps"};
                props.Setup(_archive,_decoder,bundle,result.LowestHeight,tileX,tileZ);
                string waterNote="No source-linked harbour-water elevation found.";
                if(props.HarbourWaterCandidate is float boatHeight)
                {
                    root.AddChild(SourceHarbourWater.Create(boatHeight,
                        result.LowestHeight,props.HarbourAnchorCount));
                    waterNote=$"Water experiment: Y={boatHeight:F2} from "+
                        $"{props.HarbourAnchorCount} authored boat placements; "+
                        "actual ESO sea level and water material unverified.";
                }
                props.Progress+=message=>{
                    if(_terrainRoot==root)
                    {
                        _status.Text=message;
                        _briefStatus.Text=message;
                        if(_sceneLabel!=null)_sceneLabel.Text=message+"\n"+waterNote;
                    }
                };
                root.AddChild(props);
                PositionGroundCameraAtSource(bundle,tileX,tileZ,
                    result.LowestHeight,props.Focus);
                _status.Text="Original source props and native 65x65 terrain loaded.";
                _briefStatus.Text="Best available 5x5 source scene";
                if(_sceneLabel!=null)_sceneLabel.Text=
                    $"{props.SourceInstances:N0} original prop placements across "+
                    $"{props.SourceModels} source models. Preparing textures...\n"+
                    waterNote;
            }
            if(fullWorldspaceDetail && !closeUp)
            {
                var detail=new FullWorldspaceDetail { Name="FullWorldspaceDetail" };
                detail.Setup(_archive,_decoder,_bundles,bundle,selectedMaterial,
                    result.LowestHeight,_showOriginalLodNormals,_showOriginalNearColourTint);
                detail.Progress+=message=>{
                    if(_terrainRoot==root)
                    {
                        _status.Text=message;
                        _briefStatus.Text=message;
                    }
                };
                root.AddChild(detail);
            }
        }
        catch (Exception error)
        {
            _status.Text = $"Could not load full section {id}: {error.Message}";
            _briefStatus.Text = $"Section {id} unavailable: {error.Message}";
            GD.PushError(error.ToString());
        }
    }

    private void ShowCatalogRegion(int selected)
    {
        if (selected < 0 || selected >= TerrainConnections.Confirmed.Count)
            return;
        if (selected == 0)
        {
            ShowExtendedLandscape();
            return;
        }
        var candidate = TerrainConnections.Confirmed[selected];
        _status.Text = $"Reading original data for {candidate.Label}...";
        try
        {
            var result = CompositeTerrain.Create(
                _archive, _decoder, _bundles, candidate.Patches);

            // Build the replacement off-scene; retain the old landscape if
            // validation or decoding fails.
            var root = new Node3D { Name = "ValidatedConnectedRegion" };
            using var shader = new Shader
            {
                Code = @"shader_type spatial;
render_mode unshaded, cull_disabled;
uniform float max_height = 100.0;
varying float height;
void vertex() { height = VERTEX.y; }
void fragment() {
  float t = clamp(height / max(max_height, 0.01), 0.0, 1.0);
  ALBEDO = mix(
    mix(vec3(0.20,0.42,0.32), vec3(0.55,0.51,0.38), smoothstep(0.18,0.80,t)),
    vec3(0.80,0.78,0.74), smoothstep(0.77,1.0,t));
}"
            };
            using var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter(
                "max_height", result.MaxElevation - result.MinElevation);
            using var mesh = result.Mesh;
            root.AddChild(new MeshInstance3D
            {
                Name = "ValidatedTerrain",
                Mesh = mesh,
                MaterialOverride = material
            });
            if (_terrainRoot != null)
                _terrainRoot.QueueFree();
            AddChild(root);
            _terrainRoot = root;

            uint firstId = candidate.Patches[0].SectorId;
            for (int i = 0; i < _bundles.Count; i++)
                if (_bundles[i].DescriptorId == firstId)
                {
                    _selectedIndex = i;
                    break;
                }
            _atlas?.Highlight(firstId);
            if (_atlas != null) _atlas.Visible = false;
            _jump.Text = $"id:{firstId}";
            float relief = result.MaxElevation - result.MinElevation;
            _focus = new Vector3(0, relief * 0.38f, 0);
            _distance = Math.Max(1100, relief * 2.8f);
            _yaw = 0.55f;
            _pitch = 0.89f;
            UpdateCamera();
            _status.Text =
                $"{candidate.Label}\n" +
                $"{result.OriginalTiles} original tiles | {result.VerifiedOverlaps:N0} matching overlaps\n" +
                $"0 conflicts: {result.ConflictingOverlaps} | {result.RepairedPadding} padding repairs\n" +
                "Archive IDs do not establish absolute world position.";
            GD.Print(_status.Text);
        }
        catch (Exception ex)
        {
            _status.Text = $"Refused join {candidate.Label}: {ex.Message}";
            GD.PushError(ex.ToString());
        }
    }

    private void ShowExtendedLandscape()
    {
        _status.Text = "Validating a larger, three-part terrain arrangement...";
        try
        {
            // The only subtiles retained are those with fully matching nonzero
            // overlapping vertices at the two joins. No fabricated terrain.
            var patches = new[] {
                new CompositeTerrain.Patch(961,0,0,0,4,0,4),   // central 5x5
                new CompositeTerrain.Patch(955,5,0,0,4,0,3),   // 4 rows along east edge
                new CompositeTerrain.Patch(996,0,-5,0,1,0,4)   // 2 columns along north edge
            };
            var result=CompositeTerrain.Create(_archive,_decoder,_bundles,patches);
            if(_terrainRoot!=null) { _terrainRoot.QueueFree(); _terrainRoot=null; }
            var root=new Node3D { Name="ThreePartConnectedRegion" };
            AddChild(root);
            _terrainRoot=root;
            using var shader=new Shader {
                Code=@"shader_type spatial;
render_mode unshaded, cull_disabled;
uniform float max_height = 100.0;
varying float height;
void vertex() { height = VERTEX.y; }
void fragment() {
    float t=clamp(height/max(max_height,0.01),0.0,1.0);
    ALBEDO=mix(mix(vec3(0.17,0.41,0.32),vec3(0.55,0.52,0.38),smoothstep(0.2,0.8,t)),
               vec3(0.78,0.77,0.74),smoothstep(0.85,1.0,t));
}"
            };
            using var material=new ShaderMaterial { Shader=shader };
            material.SetShaderParameter("max_height",result.MaxElevation-result.MinElevation);
            using var mesh=result.Mesh;
            root.AddChild(new MeshInstance3D {
                Name="ThreePart_961_955_996",Mesh=mesh,MaterialOverride=material
            });

            for(int i=0;i<_bundles.Count;i++)
                if(_bundles[i].DescriptorId==961){_selectedIndex=i;break;}
            _atlas?.Highlight(961);
            if(_atlas!=null)_atlas.Visible=false;
            _jump.Text="id:961";
            float relief=result.MaxElevation-result.MinElevation;
            _focus=new Vector3(0,relief*0.4f,0);
            _distance=Math.Max(1500,relief*3.0f);
            _yaw=0.55f;
            _pitch=1.08f;
            UpdateCamera();
            _status.Text=
                $"3-part original landscape | {result.OriginalTiles} verified terrain tiles\n"+
                $"Original overlapping vertices: {result.VerifiedOverlaps} matching, "+
                $"{result.RepairedPadding} padding repairs, {result.ConflictingOverlaps} conflicts\n"+
                $"Indexed mesh: {result.RenderedTriangles:N0} triangles | "+
                $"{result.MissingTriangles:N0} triangles excluded (no source/padding)\n"+
                "Partial map, source-height joins only; geographical names unverified.";
            GD.Print(_status.Text);
        }
        catch(Exception ex)
        {
            _status.Text="Conservative 3-part join rejected: "+ex.Message;
            GD.PushError(ex.ToString());
        }
    }

    private void ShowJoinedLandscape()
    {
        _status.Text = "Reading and joining 50 original ESO terrain tiles...";
        try
        {
            var result = StitchedTerrain.Create(_archive, _decoder, _bundles, 868u, 869u);
            if (_terrainRoot != null)
            {
                _terrainRoot.QueueFree();
                _terrainRoot = null;
            }
            var root = new Node3D { Name = "JoinedOriginalTerrain" };
            AddChild(root);
            _terrainRoot = root;

            using var shader = new Shader
            {
                Code = @"shader_type spatial;
render_mode unshaded, cull_disabled;
uniform float maximum_height = 100.0;
varying float elevation;
void vertex() { elevation = VERTEX.y; }
void fragment() {
    float t = clamp(elevation / max(maximum_height, 0.01), 0.0, 1.0);
    ALBEDO = mix(
        mix(vec3(0.18, 0.40, 0.28), vec3(0.48, 0.50, 0.35), smoothstep(0.2, 0.75, t)),
        vec3(0.70, 0.70, 0.65), smoothstep(0.75, 1.0, t));
    ROUGHNESS = 0.96;
}"
            };
            using var material = new ShaderMaterial { Shader = shader };
            material.SetShaderParameter("maximum_height", result.MaxHeight-result.MinHeight);
            using var mesh = result.Mesh;
            root.AddChild(new MeshInstance3D
            {
                Name = "Merged_868_869",
                Mesh = mesh,
                MaterialOverride = material
            });

            for (int i=0;i<_bundles.Count;i++)
                if (_bundles[i].DescriptorId==868) { _selectedIndex=i; break; }
            _atlas?.Highlight(868);
            if (_atlas != null) _atlas.Visible = false;
            _jump.Text = "id:868";
            float relief = result.MaxHeight-result.MinHeight;
            _focus = new Vector3(0, relief*0.36f, 0);
            _distance = Math.Max(1100, relief*2.5f);
            _yaw=0.55f;
            _pitch=0.70f;
            UpdateCamera();
            _status.Text =
                "Verified neighbour pair 868 + 869 | 10 x 5 terrain tiles\n" +
                $"Original samples | {result.SourceTiles} files | {result.MatchingOverlaps} matching vertices\n" +
                $"Zero padding filled: {result.ZeroPaddingRepairs} | conflicts: {result.ConflictingOverlaps}\n" +
                "One indexed mesh, direct DAT reads. Zone name not yet identified.";
            GD.Print(_status.Text);
        }
        catch (Exception ex)
        {
            _status.Text = "Join failed: " + ex.Message;
            GD.PushError(ex.ToString());
        }
    }

    public void SelectBundle(int index)
    {
        if (_bundles.Count == 0) return;
        _selectedIndex = Mathf.Clamp(index, 0, _bundles.Count - 1);
        _jump.Text = _selectedIndex.ToString();
        var bundle = _bundles[_selectedIndex];
        _status.Text = $"Loading bundle {_selectedIndex:N0} of {_bundles.Count - 1:N0}...";
        _atlas?.Highlight(bundle.DescriptorId);

        if (_terrainRoot != null)
        {
            _terrainRoot.QueueFree();
            _terrainRoot = null;
        }

        var root = new Node3D { Name = "DecodedTerrain" };
        AddChild(root);
        _terrainRoot = root;
        // Diagnostic elevation colouring, not a claim that ESO uses these materials.
        using var shader = new Shader
        {
            Code = @"shader_type spatial;
render_mode cull_disabled;
uniform float min_elevation = 0.0;
uniform float max_elevation = 100.0;
varying float elevation;
void vertex() { elevation = VERTEX.y; }
void fragment() {
    float t = clamp((elevation - min_elevation) /
                    max(max_elevation - min_elevation, 0.01), 0.0, 1.0);
    vec3 grass = vec3(0.22, 0.42, 0.27);
    vec3 soil = vec3(0.52, 0.49, 0.37);
    vec3 stone = vec3(0.72, 0.73, 0.68);
    ALBEDO = mix(mix(grass, soil, smoothstep(0.20, 0.75, t)),
                 stone, smoothstep(0.70, 1.0, t));
    ROUGHNESS = 0.98;
}"
        };
        using var material = new ShaderMaterial { Shader = shader };

        var decoded = new List<(EsoRecord record, LandTile tile)>();
        float minH = float.PositiveInfinity, maxH = float.NegativeInfinity;
        int failures = 0;
        // Legacy 5x5 single-section preview. The full package can contain
        // thousands of original heightfields and is accessed by the region viewer.
        foreach (var record in bundle.Tiles.Where(r =>
            (int)(r.FileId>>16) < bundle.OriginX+5 &&
            (int)(r.FileId&0xFFFF) < bundle.OriginY+5))
        {
            try
            {
                byte[] raw = _archive.ReadDecoded(record, _decoder);
                if (!LandTile.TryParse(raw, out LandTile? tile) || tile == null)
                {
                    failures++;
                    continue;
                }
                decoded.Add((record, tile));
                foreach (float h in tile.Samples)
                {
                    minH = Math.Min(minH, h);
                    maxH = Math.Max(maxH, h);
                }
            }
            catch (Exception ex)
            {
                failures++;
                GD.PrintErr($"Land record {record.Ordinal}: {ex.Message}");
            }
        }

        if (decoded.Count == 0)
        {
            _status.Text = $"Bundle #{_selectedIndex}: no usable heightfield tiles (errors {failures}).";
            return;
        }

        const float heightScale = 0.70f;
        material.SetShaderParameter("min_elevation", minH * heightScale);
        material.SetShaderParameter("max_elevation", maxH * heightScale);
        int maxX = decoded.Max(t => (int)(t.record.FileId >> 16) - bundle.OriginX);
        int maxZ = decoded.Max(t => (int)(t.record.FileId & 0xFFFF) - bundle.OriginY);
        float centerX = (maxX + 1) * 32f;
        float centerZ = (maxZ + 1) * 32f;
        foreach (var (record, tile) in decoded)
        {
            int x = (int)(record.FileId >> 16) - bundle.OriginX;
            int z = (int)(record.FileId & 0xFFFF) - bundle.OriginY;
            if (x is < 0 or > 5 || z is < 0 or > 5) continue;

            using var generatedMesh = tile.BuildMesh(
                horizontalScale: 1, verticalScale: heightScale,
                trimWest: x == 0, trimEast: x == maxX,
                trimNorth: z == 0, trimSouth: z == maxZ);
            var mesh = new MeshInstance3D
            {
                Name = $"Land_{x}_{z}_{record.Ordinal}",
                Mesh = generatedMesh,
                Position = new Vector3(x * 64f - centerX, -minH * heightScale, z * 64f - centerZ),
                MaterialOverride = material
            };
            root.AddChild(mesh);
        }

        _focus = new Vector3(0, (maxH - minH) * heightScale * 0.45f, 0);
        _distance = Math.Max(570, (maxH - minH) * heightScale * 2.1f);
        _yaw = 0.45f;
        _pitch = 1.05f;
        UpdateCamera();
        int sectorX = (int)(bundle.DescriptorId % TerrainAtlas.WidthInSectors);
        int sectorY = (int)(bundle.DescriptorId / TerrainAtlas.WidthInSectors);
        _status.Text =
            $"Sector ({sectorX},{sectorY}) | ID {bundle.DescriptorId} | DAT {bundle.ArchiveIndex:D4}\n" +
            $"Bundle {_selectedIndex:N0}/{_bundles.Count - 1:N0} | preview {decoded.Count}/{bundle.Tiles.Count} tiles | height {minH:F1}-{maxH:F1}\n" +
            "Select 'Render all tiles' for full section. World zone identity unverified.";
        GD.Print(_status.Text);
    }

    // Place a 5x5 inspection camera a few units over the centre tile's
    // ORIGINAL height, not at a large, arbitrarily clamped orbit radius.
    private void PlaceFreeCameraNearTerrain(TerrainBundle bundle,int tileX,int tileZ,float lowestHeight)
    {
        if(_camera==null)return;
        var record=bundle.Tiles.FirstOrDefault(r=>
            (int)(r.FileId>>16)==tileX && (int)(r.FileId&0xffff)==tileZ);
        if(record.UncompressedSize==0)return;
        try
        {
            if(!LandTile.TryParse(_archive.ReadDecoded(record,_decoder),out var tile)||
                tile==null||tile.Width!=65||tile.Height!=65)
                return;
            int x=32,z=32;
            if(tile.Samples[z*65+x]==0f)
            {
                // Some original terrain edges have zero-height padding.
                var valid=Enumerable.Range(0,65*65)
                    .Where(i=>tile.Samples[i]!=0f)
                    .OrderBy(i=>Math.Abs(i%65-32)+Math.Abs(i/65-32))
                    .FirstOrDefault(-1);
                if(valid<0)return;
                x=valid%65;
                z=valid/65;
            }
            const float tileWidth=100f;
            _flyPosition=new Vector3(
                (x-32f)*tileWidth/64f,
                tile.Samples[z*65+x]-lowestHeight+2.5f,
                (z-32f)*tileWidth/64f);
            _yaw=0.5f;
            _pitch=0.10f;
            _flySpeed=18f;
            _camera.Position=_flyPosition;
            _camera.Rotation=new Vector3(-_pitch,_yaw,0f);
            GD.Print($"FREE CAMERA starting at original tile {tileX},{tileZ} " +
                $"sample ({x},{z}), 2.5 units above terrain, speed {_flySpeed}");
        }
        catch(Exception e)
        {
            GD.PushWarning("Could not position free camera at source terrain: "+e.Message);
        }
    }

    // The mouse is only captured while right-click is held.
    // Release even when the pointer passes over the tools UI.
    public override void _Input(InputEvent @event)
    {
        if(!_mouseLooking)return;
        if(@event is InputEventMouseButton button &&
            button.ButtonIndex==MouseButton.Right && !button.Pressed)
        {
            ReleaseMouse();
            GetViewport().SetInputAsHandled();
        }
        else if(@event is InputEventKey key &&
            key.Pressed && key.Keycode==Key.Escape)
        {
            ReleaseMouse();
            GetViewport().SetInputAsHandled();
        }
        else if(@event is InputEventMouseMotion motion && _camera!=null)
        {
            _yaw-=motion.Relative.X*0.003f;
            _pitch=Mathf.Clamp(_pitch+motion.Relative.Y*0.003f,-1.52f,1.52f);
            _camera.Rotation=new Vector3(-_pitch,_yaw,0f);
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_terrainUiLayer?.Visible==true &&
            @event is InputEventKey key && key.Pressed && !key.Echo && key.Keycode == Key.H)
        {
            if (_toolsPanel != null) _toolsPanel.Visible = !_toolsPanel.Visible;
            if (_atlas != null) _atlas.Visible = false;
            GetViewport().SetInputAsHandled();
            return;
        }
        if (@event is not InputEventMouseButton mb)return;
        if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
        {
            _mouseLooking=true;
            Input.MouseMode=Input.MouseModeEnum.Captured;
            GetViewport().SetInputAsHandled();
        }
        // A wheel changes free-fly movement speed, never camera distance.
        else if(mb.Pressed && mb.ButtonIndex==MouseButton.WheelUp)
        {
            _flySpeed=Mathf.Clamp(_flySpeed*1.3f,0.2f,3000f);
            GD.Print($"FREE CAMERA movement speed {_flySpeed:F1}");
            GetViewport().SetInputAsHandled();
        }
        else if(mb.Pressed && mb.ButtonIndex==MouseButton.WheelDown)
        {
            _flySpeed=Mathf.Clamp(_flySpeed/1.3f,0.2f,3000f);
            GD.Print($"FREE CAMERA movement speed {_flySpeed:F1}");
            GetViewport().SetInputAsHandled();
        }
    }

    private void ReleaseMouse()
    {
        _mouseLooking=false;
        Input.MouseMode=Input.MouseModeEnum.Visible;
    }

    public override void _ExitTree()
    {
        if(_mouseLooking)ReleaseMouse();
    }

    public override void _Process(double delta)
    {
        if (_camera==null) return;
        Control? focused=GetViewport().GuiGetFocusOwner();
        if(!_mouseLooking && focused is LineEdit or TextEdit or SpinBox)
            return;

        Vector3 movement=Vector3.Zero;
        var basis=_camera.Transform.Basis;
        if(Input.IsKeyPressed(Key.W))movement-=basis.Z;
        if(Input.IsKeyPressed(Key.S))movement+=basis.Z;
        if(Input.IsKeyPressed(Key.A))movement-=basis.X;
        if(Input.IsKeyPressed(Key.D))movement+=basis.X;
        if(Input.IsKeyPressed(Key.E)||Input.IsKeyPressed(Key.Space))
            movement+=Vector3.Up;
        if(Input.IsKeyPressed(Key.Q)||Input.IsKeyPressed(Key.C))
            movement-=Vector3.Up;
        if(movement.LengthSquared()==0)return;

        float speed=_flySpeed;
        if(Input.IsKeyPressed(Key.Shift))speed*=5f;
        if(Input.IsKeyPressed(Key.Alt))speed*=0.2f;
        _flyPosition+=movement.Normalized()*speed*(float)delta;
        _camera.Position=_flyPosition;
    }

    // Only used when entering a new section/region or switching views.
    // Navigation after this initial framing is independent of the focus point.
    private void UpdateCamera()
    {
        if (_camera == null) return;
        if(_mouseLooking)ReleaseMouse();
        float cosPitch = Mathf.Cos(_pitch);
        Vector3 offset = new Vector3(
            Mathf.Sin(_yaw) * cosPitch, Mathf.Sin(_pitch),
            Mathf.Cos(_yaw) * cosPitch) * _distance;
        _flyPosition=_focus+offset;
        _camera.Position=_flyPosition;
        _camera.LookAt(_focus);
        _camera.Near=0.05f;
        _camera.Far=50000;
        _flySpeed=Mathf.Clamp(_distance*0.08f,20f,800f);
    }
}
