using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Environment=System.Environment;

namespace EsoTest;

/// <summary>Two-workspace shell: the catalogue or the original 3D world, never both.</summary>
public partial class EsoAssetBrowser : CanvasLayer
{
    private EsoArchive _archive=null!;
    private OodleDecoder _decoder=null!;
    private TerrainBrowser _terrain=null!;
    private IReadOnlyList<EsoAssetCatalogue.Category> _categories=null!;
    private HashSet<uint> _availableSections=null!;

    private PanelContainer _screen=null!;
    private PanelContainer _worldReturn=null!;
    private VBoxContainer _categoryMenu=null!;
    private Button[] _categoryButtons=Array.Empty<Button>();
    private LineEdit _search=null!;
    private ItemList _results=null!;
    private Label _summary=null!;
    private Label _description=null!;
    private Label _detail=null!;
    private TextureRect _preview=null!;
    private Button _openTerrain=null!;
    private Button _preview3D=null!;
    private EsoModelPreview _modelPreview=null!;
    private uint _selectedModelId;
    private Button _previous=null!;
    private Button _next=null!;
    private Label _pageLabel=null!;
    private Label _detailTitle=null!;
    private Label _previewHint=null!;

    private IReadOnlyList<EsoAssetCatalogue.Entry> _matches=Array.Empty<EsoAssetCatalogue.Entry>();
    private int _category;
    private int _page;
    private uint _selectedTerrainSection;
    private bool _inLibrary=true;
    private const int PageSize=100;
    private string _worldspaceStatus="";

    public void Setup(EsoArchive archive,OodleDecoder decoder,
        IReadOnlyDictionary<uint,string> names,
        IReadOnlyList<TerrainBundle> bundles,TerrainBrowser terrain)
    {
        _archive=archive;
        _decoder=decoder;
        _terrain=terrain;
        _availableSections=bundles.Select(x=>x.DescriptorId).ToHashSet();
        _categories=EsoAssetCatalogue.Build(archive,decoder,names,out _worldspaceStatus,
            ProjectSettings.GlobalizePath("res://.research/model-names.tsv"));
        GD.Print("ESO ASSET BROWSER "+string.Join(", ",
            _categories.Select(c=>$"{c.Title}: {c.Entries.Count:N0}")));
        GD.Print("ESO ASSET BROWSER "+_worldspaceStatus);
    }

    public override void _Ready()
    {
        Layer=4;
        CreateLibrary();
        CreateWorldReturn();
        _modelPreview=new EsoModelPreview();
        _modelPreview.Setup(_archive,_decoder);
        AddChild(_modelPreview);

        string name=Environment.GetEnvironmentVariable("ESO_ASSET_CATEGORY")??"";
        _category=Enumerable.Range(0,_categories.Count).FirstOrDefault(i=>
            _categories[i].Title.Equals(name,StringComparison.OrdinalIgnoreCase),0);
        _search.Text=Environment.GetEnvironmentVariable("ESO_ASSET_SEARCH")??"";
        UpdateCategory();
        if(Environment.GetEnvironmentVariable("ESO_ASSET_SELECT_FIRST")=="1" &&
            _results.ItemCount>0)
        {
            _results.Select(0);
            SelectEntry(0);
            if(Environment.GetEnvironmentVariable("ESO_ASSET_PREVIEW_MODEL")=="1")
                Open3DPreview();
        }
        bool worldRequested=Environment.GetEnvironmentVariable("ESO_START_TERRAIN")=="1" ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ESO_FULL_SECTION")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ESO_DETAIL_PATCH")) ||
            Environment.GetEnvironmentVariable("ESO_AUTO_REGION")=="1";
        ShowLibrary(!worldRequested);
    }

    private void CreateLibrary()
    {
        _screen=new PanelContainer {
            Name="AssetLibraryScreen",
            AnchorLeft=0,AnchorRight=1,AnchorTop=0,AnchorBottom=1,
            OffsetLeft=10,OffsetTop=10,OffsetRight=-10,OffsetBottom=-10,
            MouseFilter=Control.MouseFilterEnum.Stop
        };
        _screen.AddThemeStyleboxOverride("panel",
            EsoUiStyles.Box(EsoUiStyles.Background,12,1,new Color("#395365"),14));
        AddChild(_screen);

        var root=new VBoxContainer();
        root.AddThemeConstantOverride("separation",13);
        _screen.AddChild(root);

        var header=new HBoxContainer();
        header.AddThemeConstantOverride("separation",12);
        root.AddChild(header);
        var brand=new VBoxContainer {CustomMinimumSize=new Vector2(310,0)};
        header.AddChild(brand);
        brand.AddChild(EsoUiStyles.Note("THE ELDER SCROLLS ONLINE  /  LOCAL ARCHIVES"));
        brand.AddChild(EsoUiStyles.Heading("Asset library",25));
        var leftSpacer=new Control {SizeFlagsHorizontal=Control.SizeFlags.ExpandFill};
        header.AddChild(leftSpacer);

        var libraryTab=new Button {Text="Library"};
        EsoUiStyles.ButtonStyle(libraryTab,true);
        libraryTab.Pressed+=()=>ShowLibrary(true);
        header.AddChild(libraryTab);
        var worldTab=new Button {Text="World viewer"};
        EsoUiStyles.ButtonStyle(worldTab);
        worldTab.Pressed+=()=>ShowLibrary(false);
        header.AddChild(worldTab);

        var divider=new HSeparator();
        divider.AddThemeStyleboxOverride("separator",EsoUiStyles.Box(new Color("#334958"),0));
        root.AddChild(divider);

        var content=new HBoxContainer {
            SizeFlagsVertical=Control.SizeFlags.ExpandFill,
            SizeFlagsHorizontal=Control.SizeFlags.ExpandFill
        };
        content.AddThemeConstantOverride("separation",14);
        root.AddChild(content);

        var sidebar=new VBoxContainer {CustomMinimumSize=new Vector2(205,0)};
        sidebar.AddThemeConstantOverride("separation",6);
        content.AddChild(sidebar);
        sidebar.AddChild(EsoUiStyles.Note("EXPLORE"));
        var sidePanel=new PanelContainer {
            SizeFlagsVertical=Control.SizeFlags.ExpandFill
        };
        sidePanel.AddThemeStyleboxOverride("panel",
            EsoUiStyles.Box(EsoUiStyles.Surface,9,0,pad:8));
        sidebar.AddChild(sidePanel);
        _categoryMenu=new VBoxContainer();
        _categoryMenu.AddThemeConstantOverride("separation",5);
        sidePanel.AddChild(_categoryMenu);
        _categoryButtons=new Button[_categories.Count];
        for(int i=0;i<_categories.Count;i++)
        {
            int at=i;
            var cat=_categories[i];
            var button=new Button {
                Text=cat.Title+"   "+cat.Entries.Count.ToString("N0"),
                Alignment=HorizontalAlignment.Left,
                TooltipText=cat.Description,
                ClipText=true
            };
            button.Pressed+=()=>{_category=at;UpdateCategory();};
            _categoryMenu.AddChild(button);
            _categoryButtons[i]=button;
        }
        sidebar.AddChild(EsoUiStyles.Note(
            "Source-backed records only. NPC identities and gameplay data remain unclassified."));

        var middle=new VBoxContainer {
            CustomMinimumSize=new Vector2(300,0),
            SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,
            SizeFlagsVertical=Control.SizeFlags.ExpandFill
        };
        middle.AddThemeConstantOverride("separation",8);
        content.AddChild(middle);
        middle.AddChild(EsoUiStyles.Heading("Browse assets",18));
        _description=EsoUiStyles.Note("");
        _description.CustomMinimumSize=new Vector2(0,39);
        middle.AddChild(_description);
        _search=new LineEdit {
            PlaceholderText="Search names, source paths or IDs",
            ClearButtonEnabled=true,
            CustomMinimumSize=new Vector2(0,42)
        };
        _search.AddThemeStyleboxOverride("normal",EsoUiStyles.Box(new Color("#203140"),7,1,new Color("#4a6b7d"),10));
        _search.AddThemeStyleboxOverride("focus",EsoUiStyles.Box(new Color("#203140"),7,2,EsoUiStyles.Accent,10));
        _search.TextChanged+=_=>{_page=0;UpdateResults();};
        middle.AddChild(_search);
        _summary=EsoUiStyles.Note("");
        middle.AddChild(_summary);

        _results=new ItemList {
            SizeFlagsHorizontal=Control.SizeFlags.ExpandFill,
            SizeFlagsVertical=Control.SizeFlags.ExpandFill,
            SelectMode=ItemList.SelectModeEnum.Single,
            AllowReselect=true
        };
        _results.AddThemeStyleboxOverride("panel",EsoUiStyles.Box(EsoUiStyles.Surface,9,1,new Color("#334858"),7));
        _results.AddThemeStyleboxOverride("focus",EsoUiStyles.Box(new Color(0,0,0,0),8));
        _results.AddThemeColorOverride("font_selected_color",EsoUiStyles.Text);
        _results.AddThemeStyleboxOverride("selected",EsoUiStyles.Box(new Color("#2b5260"),5));
        _results.ItemSelected+=index=>SelectEntry((int)index);
        middle.AddChild(_results);
        EsoUiStyles.Scrollbar(_results.GetVScrollBar());

        var pager=new HBoxContainer();
        middle.AddChild(pager);
        _previous=new Button {Text="‹ Back"};
        EsoUiStyles.ButtonStyle(_previous);
        _previous.Pressed+=()=>{_page--;UpdatePage();};
        pager.AddChild(_previous);
        _pageLabel=EsoUiStyles.Note("");
        _pageLabel.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        _pageLabel.HorizontalAlignment=HorizontalAlignment.Center;
        pager.AddChild(_pageLabel);
        _next=new Button {Text="Next ›"};
        EsoUiStyles.ButtonStyle(_next);
        _next.Pressed+=()=>{_page++;UpdatePage();};
        pager.AddChild(_next);

        var detailsPanel=new PanelContainer {
            CustomMinimumSize=new Vector2(338,0),
            SizeFlagsVertical=Control.SizeFlags.ExpandFill
        };
        detailsPanel.AddThemeStyleboxOverride("panel",EsoUiStyles.Box(EsoUiStyles.Surface,9,0,pad:12));
        content.AddChild(detailsPanel);
        var detailColumn=new VBoxContainer();
        detailColumn.AddThemeConstantOverride("separation",10);
        detailsPanel.AddChild(detailColumn);
        detailColumn.AddChild(EsoUiStyles.Note("SELECTED ASSET"));
        _detailTitle=EsoUiStyles.Heading("Nothing selected",18);
        _detailTitle.AutowrapMode=TextServer.AutowrapMode.WordSmart;
        detailColumn.AddChild(_detailTitle);

        var imagePlate=new PanelContainer {
            CustomMinimumSize=new Vector2(0,215)
        };
        imagePlate.AddThemeStyleboxOverride("panel",EsoUiStyles.Box(new Color("#0c141d"),8));
        detailColumn.AddChild(imagePlate);
        _preview=new TextureRect {
            ExpandMode=TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode=TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter=Control.MouseFilterEnum.Ignore
        };
        imagePlate.AddChild(_preview);
        _previewHint=EsoUiStyles.Note("Original DDS preview when available");
        _previewHint.HorizontalAlignment=HorizontalAlignment.Center;
        imagePlate.AddChild(_previewHint);

        _preview3D=new Button {Text="Preview source 3D geometry",Visible=false};
        EsoUiStyles.ButtonStyle(_preview3D,true);
        _preview3D.Pressed+=Open3DPreview;
        detailColumn.AddChild(_preview3D);

        _openTerrain=new Button {Text="Open in World viewer",Visible=false};
        EsoUiStyles.ButtonStyle(_openTerrain,true);
        _openTerrain.Pressed+=OpenTerrain;
        detailColumn.AddChild(_openTerrain);

        var scroll=new ScrollContainer {
            SizeFlagsVertical=Control.SizeFlags.ExpandFill,
            HorizontalScrollMode=ScrollContainer.ScrollMode.Disabled
        };
        detailColumn.AddChild(scroll);
        EsoUiStyles.Scrollbar(scroll.GetVScrollBar());
        _detail=EsoUiStyles.Note("Select an asset to inspect its original source.");
        _detail.AddThemeFontSizeOverride("font_size",14);
        _detail.SizeFlagsHorizontal=Control.SizeFlags.ExpandFill;
        scroll.AddChild(_detail);

        root.AddChild(EsoUiStyles.Note(
            "READ ONLY  ·  DDS images stay in memory  ·  B toggles Library / World"));
    }

    private void CreateWorldReturn()
    {
        _worldReturn=new PanelContainer {
            Name="WorldNavigation",
            AnchorLeft=1,AnchorRight=1,
            OffsetLeft=-270,OffsetRight=-16,OffsetTop=16,OffsetBottom=65,
            Visible=false
        };
        _worldReturn.AddThemeStyleboxOverride("panel",
            EsoUiStyles.Box(EsoUiStyles.Background,9,1,new Color("#426270"),4));
        AddChild(_worldReturn);
        var button=new Button {Text="‹  Back to asset library"};
        EsoUiStyles.ButtonStyle(button,true);
        button.Pressed+=()=>ShowLibrary(true);
        _worldReturn.AddChild(button);
    }

    private void ShowLibrary(bool library)
    {
        _inLibrary=library;
        _screen.Visible=library;
        _worldReturn.Visible=!library;
        _terrain.SetWorkspaceVisible(!library);
    }

    private void UpdateCategory()
    {
        for(int i=0;i<_categoryButtons.Length;i++)
            EsoUiStyles.ButtonStyle(_categoryButtons[i],i==_category);
        var category=_categories[_category];
        _description.Text=category.Description;
        _page=0;
        UpdateResults();
    }

    private void UpdateResults()
    {
        var category=_categories[_category];
        string search=_search.Text.Trim();
        _matches=search.Length==0?category.Entries:
            category.Entries.Where(item=>
                item.Title.Contains(search,StringComparison.OrdinalIgnoreCase)||
                item.Source.Contains(search,StringComparison.OrdinalIgnoreCase)||
                item.ResourceId.ToString().Contains(search,StringComparison.OrdinalIgnoreCase)||
                item.ResourceId.ToString("X8").Contains(search,StringComparison.OrdinalIgnoreCase)||
                item.ZoneId?.ToString().Contains(search,StringComparison.OrdinalIgnoreCase)==true)
            .ToArray();
        _summary.Text=$"{_matches.Count:N0} results  ·  {category.Entries.Count:N0} in category";
        if(_category==0 && category.Entries.Count==0)
            _summary.Text+="  ·  "+_worldspaceStatus;
        _page=0;
        UpdatePage();
    }

    private void UpdatePage()
    {
        _results.Clear();
        int pages=Math.Max(1,(_matches.Count+PageSize-1)/PageSize);
        _page=Math.Clamp(_page,0,pages-1);
        foreach(var entry in _matches.Skip(_page*PageSize).Take(PageSize))
        {
            string suffix=_categories[_category].Title=="Model records"
                ?$"  ·  {entry.ResourceId:X8}":"";
            _results.AddItem((entry.SectionId is uint id?$"[{id}] ":"")+
                entry.Title+suffix);
        }
        _pageLabel.Text=$"{_page+1} / {pages}";
        _previous.Disabled=_page==0;
        _next.Disabled=_page>=pages-1;
        _selectedTerrainSection=0;
        _selectedModelId=0;
        _preview3D.Visible=false;
        _detailTitle.Text=_matches.Count==0?"No matches":"Choose an asset";
        _preview.Texture=null;
        _previewHint.Visible=true;
        _openTerrain.Visible=false;
        _detail.Text=_matches.Count==0 ?
            "No verified entries match this search. Try a shorter name or another category.":
            "Choose a record to see its original IDs, source path, and artwork when available.";
    }

    private void SelectEntry(int index)
    {
        int absolute=_page*PageSize+index;
        if(absolute<0||absolute>=_matches.Count)return;
        var entry=_matches[absolute];
        _detailTitle.Text=entry.Title;
        string details=$"{entry.Kind}\n\n"+
            $"Resource / section ID:\n0x{entry.ResourceId:X8}  ({entry.ResourceId})\n\n"+
            $"Source:\n{entry.Source}\n";
        if(entry.ZoneId is uint zone)
            details+=$"\nESO zone ID: {zone}\nSection: {entry.SectionId}\n"+
                     $"Dimensions: {entry.Width} × {entry.Height} units\n";
        if(entry.RelatedFiles>1)
            details+=$"\nMap tile files in set: {entry.RelatedFiles}\n";

        _selectedTerrainSection=entry.SectionId??0;
        // All model-linked IDs can be inspected, including unnamed GR2s.
        // The isolated decoder reports when a source record is not a mesh.
        _selectedModelId=_categories[_category].Title=="Model records"?entry.ResourceId:0;
        _preview3D.Visible=_selectedModelId!=0;
        _openTerrain.Visible=entry.SectionId is uint section &&
            _availableSections.Contains(section);
        _preview.Texture=null;
        _previewHint.Visible=true;

        if(entry.Source.EndsWith(".dds",StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var asset=_archive.FindId(entry.ResourceId).FirstOrDefault(
                    r=>_archive.GetIdIndexFields(r.Ordinal).Flags==0 &&
                    r.UncompressedSize is >=128 and <=16_000_000);
                if(asset.UncompressedSize==0)
                    throw new InvalidOperationException("DDS not in indexed DAT records");
                byte[] bytes=_archive.ReadDecoded(asset,_decoder);
                if(bytes.Length<128||BinaryPrimitives.ReadUInt32LittleEndian(bytes)!=0x20534444)
                    throw new InvalidOperationException("Not a DDS image");
                using var image=new Image();
                var status=image.LoadDdsFromBuffer(bytes);
                if(status!=Error.Ok)throw new InvalidOperationException($"DDS decoder: {status}");
                _preview.Texture=ImageTexture.CreateFromImage(image);
                _previewHint.Visible=false;
                details+=$"\nImage: {image.GetWidth()} × {image.GetHeight()}\n"+
                    $"DAT archive: {asset.ArchiveIndex}\n"+
                    $"Stored bytes: {asset.CompressedSize:N0}";
            }
            catch(Exception e)
            {
                details+="\nPreview unavailable: "+e.Message+
                    "\n(Index browsing still works during game updates.)";
            }
        }
        _detail.Text=details;
    }

    private void Open3DPreview()
    {
        if(_selectedModelId==0)return;
        _modelPreview.ShowModel(_selectedModelId,_detailTitle.Text);
    }

    private void OpenTerrain()
    {
        if(_selectedTerrainSection==0)return;
        ShowLibrary(false);
        _terrain.OpenSectionFromCatalogue(_selectedTerrainSection);
    }

    public override void _Input(InputEvent @event)
    {
        if(@event is not InputEventKey key || !key.Pressed || key.Echo ||
            key.Keycode!=Key.B)return;
        var focus=GetViewport().GuiGetFocusOwner();
        if(focus is LineEdit or TextEdit or SpinBox || _modelPreview.Visible)return;
        ShowLibrary(!_inLibrary);
        GetViewport().SetInputAsHandled();
    }
}
