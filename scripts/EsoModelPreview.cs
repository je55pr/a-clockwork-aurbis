using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Process=System.Diagnostics.Process;

namespace EsoTest;

/// <summary>
/// Read-only, on-demand 3D model inspector. A separate .NET helper process
/// accesses Granny; Godot consumes checked, derived source XYZ, UV, tangent
/// frame and per-material texture references. Static bind pose: original
/// diffuse and packed normals work, remaining ESO shader variants are pending.
/// </summary>
public partial class EsoModelPreview : Control
{
    private PanelContainer _window=null!;
    private SubViewport _viewport=null!;
    private SubViewportContainer _frame=null!;
    private Node3D _modelRoot=null!;
    private Camera3D _camera=null!;
    private Label _status=null!;
    private Label _heading=null!;
    private Button _close=null!;
    private float _yaw=0.5f,_pitch=0.25f,_distance=4;
    private Vector3 _target=Vector3.Zero;
    private bool _dragging;
    private int _currentRequest;
    private readonly List<MeshInstance3D> _instances=new();
    private Shader? _detailShader;
    private readonly record struct MaterialSection(string Name,int FirstTriangle,int TriangleCount,uint Diffuse,uint Normal,uint Detail,uint Diffuse2,uint Specular,
        bool FlagsKnown,bool DiffuseEnabled,bool NormalEnabled,bool SpecularEnabled,bool DetailEnabled,bool TwoSided,
        int MaterialType,int AlphaMode,float SecondaryGlossiness,float SecondaryFresnel,float SecondaryGlow);

    private EsoArchive _archive=null!;
    private OodleDecoder _decoder=null!;

    public void Setup(EsoArchive archive,OodleDecoder decoder)
    {
        _archive=archive;
        _decoder=decoder;
    }

    public override void _Ready()
    {
        Visible=false;
        // CanvasLayer does not provide a Control parent for anchor layout.
        // Explicitly fill the viewport and keep that size during resizing.
        AnchorLeft=0;AnchorTop=0;AnchorRight=0;AnchorBottom=0;
        Size=GetViewport().GetVisibleRect().Size;
        GetViewport().SizeChanged+=()=>Size=GetViewport().GetVisibleRect().Size;
        MouseFilter=MouseFilterEnum.Stop;
        var fade=new ColorRect {
            Color=new Color(0.015f,0.028f,0.040f,0.95f),
            MouseFilter=MouseFilterEnum.Stop
        };
        fade.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(fade);

        _window=new PanelContainer {
            AnchorLeft=0.10f,AnchorRight=0.90f,AnchorTop=0.06f,AnchorBottom=0.94f,
            OffsetLeft=0,OffsetRight=0,OffsetTop=0,OffsetBottom=0
        };
        _window.AddThemeStyleboxOverride("panel",
            EsoUiStyles.Box(EsoUiStyles.Background,12,1,new Color("#426275"),14));
        AddChild(_window);
        var stack=new VBoxContainer { SizeFlagsHorizontal=SizeFlags.ExpandFill };
        stack.AddThemeConstantOverride("separation",11);
        _window.AddChild(stack);
        var head=new HBoxContainer();
        stack.AddChild(head);
        var titleColumn=new VBoxContainer {SizeFlagsHorizontal=SizeFlags.ExpandFill};
        head.AddChild(titleColumn);
        titleColumn.AddChild(EsoUiStyles.Note("ORIGINAL GR2 MESH  /  SOURCE DIFFUSE + NORMAL DDS"));
        _heading=EsoUiStyles.Heading("3D model",21);
        titleColumn.AddChild(_heading);
        _close=new Button {Text="Close  ✕"};
        EsoUiStyles.ButtonStyle(_close);
        _close.Pressed+=ClosePreview;
        head.AddChild(_close);

        _frame=new SubViewportContainer {
            SizeFlagsVertical=SizeFlags.ExpandFill,
            SizeFlagsHorizontal=SizeFlags.ExpandFill,
            Stretch=true,
            MouseFilter=MouseFilterEnum.Stop
        };
        _frame.GuiInput+=OnPreviewInput;
        stack.AddChild(_frame);
        _viewport=new SubViewport {
            Size=new Vector2I(940,560),
            OwnWorld3D=true,
            TransparentBg=false,
            RenderTargetUpdateMode=SubViewport.UpdateMode.Always
        };
        _frame.AddChild(_viewport);
        var environment=new WorldEnvironment();
        environment.Environment=new Godot.Environment {
            BackgroundMode=Godot.Environment.BGMode.Color,
            BackgroundColor=new Color("#0d1721"),
            AmbientLightSource=Godot.Environment.AmbientSource.Color,
            AmbientLightColor=new Color("#a9c5d6"),
            AmbientLightEnergy=0.8f,
            ReflectedLightSource=Godot.Environment.ReflectionSource.Bg
        };
        _viewport.AddChild(environment);
        _modelRoot=new Node3D();
        _viewport.AddChild(_modelRoot);
        var sunlight=new DirectionalLight3D {
            RotationDegrees=new Vector3(-52,32,0),
            LightEnergy=1.65f
        };
        _viewport.AddChild(sunlight);
        var fill=new DirectionalLight3D {
            RotationDegrees=new Vector3(18,-125,0),
            LightEnergy=0.55f
        };
        _viewport.AddChild(fill);
        _camera=new Camera3D {
            Current=true,Near=0.015f,Far=250000f,
            Fov=55
        };
        _viewport.AddChild(_camera);
        _status=EsoUiStyles.Note("Drag to orbit · Mouse wheel to zoom · Esc closes preview");
        stack.AddChild(_status);
        UpdateCamera();
    }

    public void ShowModel(uint id,string name)
    {
        _heading.Text=name;
        _status.Text="Preparing original 3D geometry…";
        Visible=true;
        _close.GrabFocus();
        ClearMeshes();
        int requested=++_currentRequest;

        // A helper process performs all native Granny calls. Do not invoke
        // unknown native decoders on the Godot render/UI process or main thread.
        _=Task.Run(()=>
        {
            (string path,string error)=GenerateMesh(id);
            CallDeferred(nameof(FinishPreview),requested,path,error);
        });
    }

    private (string,string) GenerateMesh(uint id)
    {
        try
        {
            string root=ProjectSettings.GlobalizePath("res://");
            string helper=Path.Combine(root,"tools","ModelGeometry","bin","Debug","net8.0","ModelGeometry.dll");
            if(!File.Exists(helper))return ("","3D helper not built. Build tools/ModelGeometry first.");
            string? granny=System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL");
            if(string.IsNullOrWhiteSpace(granny))
                granny=Path.Combine(root,".research","uesp-esoapps","EsoExtractData",
                    "EsoExtractData","granny2_x64.dll");
            if(!File.Exists(granny))return ("","Optional Granny library not found. Set ESO_GRANNY_DLL.");
            var record=_archive.FindId(id).FirstOrDefault(r=>
                _archive.GetIdIndexFields(r.Ordinal).Flags==0);
            if(record.UncompressedSize==0)return ("","No source GR2 record in the current depot.");
            string dir=Path.Combine(root,".research","model-previews");
            Directory.CreateDirectory(dir);
            string path=Path.Combine(dir,
                $"{id:X8}-a{record.ArchiveIndex}-o{record.Offset}-c{record.CompressedSize}-h{record.Hash:X8}-u{record.UncompressedSize}-v5.esm");
            if(File.Exists(path) && new FileInfo(path).Length>32)return (path,"");
            using var proc=new Process();
            proc.StartInfo=new ProcessStartInfo("dotnet") {
                UseShellExecute=false,
                CreateNoWindow=true,
                RedirectStandardOutput=false,
                RedirectStandardError=true,
                WorkingDirectory=root
            };
            foreach(string arg in new[]{helper,granny,id.ToString("X8"),path,
                      _archive.SourcePath,_decoder.LibraryPath})
                proc.StartInfo.ArgumentList.Add(arg);
            if(!proc.Start())return ("","Could not launch the isolated geometry helper.");
            // Geometry buffers are bounded and native parser may hang on bad files.
            if(!proc.WaitForExit(30000))
            {
                proc.Kill(entireProcessTree:true);
                return ("","3D parser timed out (30 seconds); file may be damaged.");
            }
            if(proc.ExitCode!=0)
            {
                string message=proc.StandardError.ReadToEnd().Trim();
                return ("","Geometry helper failed: "+message[..Math.Min(250,message.Length)]);
            }
            return File.Exists(path)?(path,""):("","Geometry helper created no preview.");
        }
        catch(Exception e){return ("",e.Message);}
    }

    // Called deferred on the Godot scene thread.
    private void FinishPreview(int request,string path,string error)
    {
        if(request!=_currentRequest||!Visible)return;
        if(error.Length>0)
        {
            _status.Text="Cannot preview model: "+error;
            return;
        }
        try{LoadGeometry(path);}
        catch(Exception e){_status.Text="Preview geometry rejected: "+e.Message;ClearMeshes();}
    }

    private void LoadGeometry(string path)
    {
        if(new FileInfo(path).Length>72_000_000)
            throw new InvalidDataException("Model preview exceeds safe file size");
        using var reader=new BinaryReader(File.OpenRead(path));
        if(new string(reader.ReadChars(4))!="ESM5")
            throw new InvalidDataException("Invalid preview format");
        int meshes=reader.ReadInt32();
        if(meshes is <1 or >128)
            throw new InvalidDataException("Unexpected mesh count");
        var pending=new List<(string,Vector3[],Vector2[],Vector2[],Vector3[],float[],bool,bool,int[],MaterialSection[])>();
        Vector3 low=new(float.MaxValue,float.MaxValue,float.MaxValue),
            high=new(float.MinValue,float.MinValue,float.MinValue);
        int totalVertices=0,totalIndices=0;
        for(int i=0;i<meshes;i++)
        {
            string name=reader.ReadString();
            int nv=reader.ReadInt32(),ni=reader.ReadInt32(),ng=reader.ReadInt32();
            bool sourceFrame=reader.ReadBoolean();
            bool hasUv2=reader.ReadBoolean();
            if(ng is <1 or >256 || nv is <3 or >400000 || ni is <3 or >1200000 || ni%3!=0 ||
                (totalVertices+=nv)>600000 || (totalIndices+=ni)>1800000)
                throw new InvalidDataException("Preview mesh exceeds vertex/triangle limits");
            var v=new Vector3[nv];
            var uv=new Vector2[nv];
            var uv2=new Vector2[nv];
            var authoredNormals=new Vector3[nv];
            var authoredTangents=new float[nv*4];
            var indices=new int[ni];
            for(int k=0;k<nv;k++)
            {
                v[k]=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                if(!v[k].IsFinite())throw new InvalidDataException("Invalid vertex position");
                low=low.Min(v[k]);
                high=high.Max(v[k]);
            }
            for(int k=0;k<nv;k++)
            {
                uv[k]=new Vector2(reader.ReadSingle(),reader.ReadSingle());
                if(!uv[k].IsFinite())throw new InvalidDataException("Invalid source UV");
            }
            for(int k=0;k<nv;k++)
            {
                uv2[k]=new Vector2(reader.ReadSingle(),reader.ReadSingle());
                if(hasUv2 && !uv2[k].IsFinite())throw new InvalidDataException("Invalid original detail UV");
            }
            for(int k=0;k<nv;k++)
            {
                authoredNormals[k]=new Vector3(reader.ReadSingle(),reader.ReadSingle(),reader.ReadSingle());
                if(sourceFrame && (!authoredNormals[k].IsFinite()||
                    authoredNormals[k].LengthSquared()<0.5f))
                    throw new InvalidDataException("Invalid source vertex normal");
            }
            for(int k=0;k<authoredTangents.Length;k++)
            {
                authoredTangents[k]=reader.ReadSingle();
                if(sourceFrame && !float.IsFinite(authoredTangents[k]))
                    throw new InvalidDataException("Invalid source tangent");
            }
            for(int k=0;k<ni;k++)
            {
                indices[k]=reader.ReadInt32();
                if(indices[k]<0||indices[k]>=nv)
                    throw new InvalidDataException("Invalid triangle index");
            }
            var groups=new MaterialSection[ng];
            for(int g=0;g<ng;g++)
            {
                string matName=reader.ReadString();
                int first=reader.ReadInt32(),count=reader.ReadInt32();
                uint diffuse=reader.ReadUInt32(),normal=reader.ReadUInt32();
                uint detail=reader.ReadUInt32(),diffuse2=reader.ReadUInt32(),specular=reader.ReadUInt32();
                bool flagsKnown=reader.ReadBoolean(),diffuseEnabled=reader.ReadBoolean(),
                    normalEnabled=reader.ReadBoolean(),specularEnabled=reader.ReadBoolean(),
                    detailEnabled=reader.ReadBoolean(),twoSided=reader.ReadBoolean();
                int materialType=reader.ReadInt32(),alphaMode=reader.ReadInt32();
                float glossiness2=reader.ReadSingle(),fresnel2=reader.ReadSingle(),glow2=reader.ReadSingle();
                if((flagsKnown && (materialType is <0 or >100 || alphaMode is <0 or >100)) ||
                    !float.IsFinite(glossiness2)||!float.IsFinite(fresnel2)||!float.IsFinite(glow2))
                    throw new InvalidDataException("Invalid GR2 material parameters");
                if(first<0||count<1||(long)(first+count)*3>ni)
                    throw new InvalidDataException("Invalid original material triangle group");
                groups[g]=new MaterialSection(matName,first,count,diffuse,normal,detail,diffuse2,specular,
                    flagsKnown,diffuseEnabled,normalEnabled,specularEnabled,detailEnabled,twoSided,
                    materialType,alphaMode,glossiness2,fresnel2,glow2);
            }
            pending.Add((name,v,uv,uv2,authoredNormals,authoredTangents,sourceFrame,hasUv2,indices,groups));
        }
        if(reader.BaseStream.Position!=reader.BaseStream.Length)
            throw new InvalidDataException("Unexpected trailing preview data");
        var center=(low+high)*0.5f;
        var size=high-low;
        if(!size.IsFinite()||size.Length()<0.0001f)
            throw new InvalidDataException("Degenerate geometry");
        ClearMeshes();
        var imageCache=new Dictionary<uint,Texture2D?>();
        int mappedGroups=0,unresolvedGroups=0,normalGroups=0,detailGroups=0;
        // Channel references alone do not establish FX_DEF_DETAILMAP/DIFFUSE2MAP.
        // Keep the secondary shader diagnostic opt-in until original flags are found.
        bool experimentalDetail=System.Environment.GetEnvironmentVariable("ESO_EXPERIMENTAL_MODEL_DETAIL")=="1";
        var normalCache=new Dictionary<uint,Texture2D?>();
        foreach(var (name,vertices,uv,uv2,authoredNormals,authoredTangents,sourceFrame,hasUv2,indices,groups) in pending)
        {
            var norms=new Vector3[vertices.Length];
            for(int i=0;i<indices.Length;i+=3)
            {
                int a=indices[i],b=indices[i+1],c=indices[i+2];
                var normal=(vertices[b]-vertices[a]).Cross(vertices[c]-vertices[a]);
                norms[a]+=normal;norms[b]+=normal;norms[c]+=normal;
            }
            for(int i=0;i<norms.Length;i++)norms[i]=norms[i].LengthSquared()>1e-12
                ?norms[i].Normalized():Vector3.Up;
            if(sourceFrame)norms=authoredNormals;
            var mesh=new ArrayMesh();
            foreach(var group in groups)
            {
                var partIndices=new int[group.TriangleCount*3];
                Array.Copy(indices,group.FirstTriangle*3,partIndices,0,partIndices.Length);
                var arrays=new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex]=vertices;
                arrays[(int)Mesh.ArrayType.Normal]=norms;
                if(sourceFrame)arrays[(int)Mesh.ArrayType.Tangent]=authoredTangents;
                arrays[(int)Mesh.ArrayType.TexUV]=uv;
                if(hasUv2)arrays[(int)Mesh.ArrayType.TexUV2]=uv2;
                arrays[(int)Mesh.ArrayType.Index]=partIndices;
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles,arrays);
                var mat=new StandardMaterial3D {
                    AlbedoColor=Colors.White,
                    Metallic=0f,
                    Roughness=0.83f,
                    CullMode=group.FlagsKnown && !group.TwoSided
                        ?BaseMaterial3D.CullModeEnum.Front
                        :BaseMaterial3D.CullModeEnum.Disabled
                };
                Texture2D? diffuse=LoadOriginalDiffuse(group.Diffuse,imageCache);
                if(diffuse!=null){mat.AlbedoTexture=diffuse;mappedGroups++;}
                else {mat.AlbedoColor=new Color("#b7d3d7");unresolvedGroups++;}
                // Original pixel shader: packed DDS A/G are tangent nx/ny.
                // Requires the decoded source normal/tangent frame.
                Texture2D? originalNormal=null;
                if(sourceFrame)
                {
                    originalNormal=LoadOriginalNormal(group.Normal,normalCache);
                    if(originalNormal!=null)
                    {
                        mat.NormalEnabled=true;
                        mat.NormalTexture=originalNormal;
                        mat.NormalScale=1f;
                        normalGroups++;
                    }
                }
                Material finalMaterial=mat;
                if(experimentalDetail && diffuse!=null && hasUv2 &&
                    (!group.FlagsKnown || group.DetailEnabled) &&
                    group.Detail!=0 && group.Diffuse2!=0)
                {
                    // Original GR2 exporter materialType=4 samples a second
                    // packed normal in DetailMaterialPS, rather than RGB color.
                    // Shader selection remains an opt-in experiment until the
                    // exporter-type-to-runtime-variant mapping is proven.
                    bool detailNormalVariant=group.FlagsKnown && group.MaterialType==4;
                    Texture2D? detail=detailNormalVariant
                        ?LoadOriginalNormal(group.Detail,normalCache)
                        :LoadOriginalDiffuse(group.Detail,imageCache);
                    Texture2D? secondary=LoadOriginalDiffuse(group.Diffuse2,imageCache);
                    if(detail!=null && secondary!=null && (!detailNormalVariant || originalNormal!=null))
                    {
                        var blend=new ShaderMaterial {Shader=GetSourceDetailBlendShader()};
                        blend.SetShaderParameter("base_diffuse",diffuse);
                        blend.SetShaderParameter("secondary_diffuse",secondary);
                        blend.SetShaderParameter("use_source_normal",originalNormal!=null);
                        blend.SetShaderParameter("detail_normal_variant",detailNormalVariant);
                        if(detailNormalVariant)
                            blend.SetShaderParameter("detail_normal",detail);
                        else blend.SetShaderParameter("source_detail",detail);
                        if(originalNormal!=null)
                            blend.SetShaderParameter("source_normal",originalNormal);
                        finalMaterial=blend;
                        detailGroups++;
                    }
                }
                mesh.SurfaceSetMaterial(mesh.GetSurfaceCount()-1,finalMaterial);
            }
            var instance=new MeshInstance3D {Name=name,Mesh=mesh,Position=-center};
            _modelRoot.AddChild(instance);
            _instances.Add(instance);
        }
        _yaw=0.45f;_pitch=0.20f;_target=Vector3.Zero;
        _distance=Math.Max(size.Length()*1.5f,0.8f);
        UpdateCamera();
        _status.Text=$"{meshes} mesh · {totalVertices:N0} vertices · {totalIndices/3:N0} triangles · "+
            $"{mappedGroups} diffuse · {normalGroups} normal · {unresolvedGroups} fallback"+
            (detailGroups>0?$" · {detailGroups} experimental detail":"")+
            " · Drag to orbit, scroll to zoom";
    }

    private Shader GetSourceDetailBlendShader()
    {
        if(_detailShader!=null)return _detailShader;
        _detailShader=new Shader
        {
            Code=@"shader_type spatial;
render_mode cull_disabled, diffuse_burley, specular_schlick_ggx;
uniform sampler2D base_diffuse : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D source_detail : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D secondary_diffuse : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D source_normal : filter_linear_mipmap, repeat_enable;
uniform sampler2D detail_normal : filter_linear_mipmap, repeat_enable;
uniform bool use_source_normal=false;
uniform bool detail_normal_variant=false;

void fragment() {
    vec4 base=texture(base_diffuse,UV);
    METALLIC=0.0;
    ROUGHNESS=0.83;
    if(detail_normal_variant) {
        // Experimental reconstruction of DetailMaterialPS, not MaterialPS.
        // Diffuse alpha is the secondary diffuse blend factor; UV1 selects
        // the source detail-normal and diffuse2 textures.
        vec4 secondary=texture(secondary_diffuse,UV2);
        ALBEDO=mix(base.rgb,secondary.rgb,base.a);
        if(use_source_normal) {
            vec3 original=texture(source_normal,UV).rgb*2.0-1.0;
            vec3 added=texture(detail_normal,UV2).rgb*2.0-1.0;
            vec3 combined=normalize(vec3(original.xy+added.xy,original.z));
            NORMAL_MAP=combined*0.5+0.5;
            NORMAL_MAP_DEPTH=1.0;
        }
    } else {
        // Experimental reconstruction of MaterialPS and ApplyDetailBlend.
        vec4 detail=texture(source_detail,UV2);
        vec4 secondary=texture(secondary_diffuse,UV);
        ALBEDO=mix(secondary.rgb,base.rgb,detail.a)*detail.rgb;
        if(use_source_normal) {
            NORMAL_MAP=texture(source_normal,UV).rgb;
            NORMAL_MAP_DEPTH=1.0;
        }
    }
}"
        };
        return _detailShader;
    }

    private Texture2D? LoadOriginalNormal(uint id,Dictionary<uint,Texture2D?> cache)
    {
        if(id==0)return null;
        if(cache.TryGetValue(id,out var existing))return existing;
        if(cache.Count>=32)return null;
        Texture2D? result=null;
        try
        {
            // Original ZoCommon.fxh ExtractNormal:
            // packed RGBA = glow, normalY, fresnel, normalX.
            using Image? source=ReadOriginalDdsImage(id);
            if(source!=null)
            {
                int width=source.GetWidth(),height=source.GetHeight();
                byte[] pixels=source.GetData();
                int topMipBytes=checked(width*height*4);
                if(pixels.Length>=topMipBytes)
                {
                    var converted=new byte[topMipBytes];
                    for(int p=0;p<topMipBytes;p+=4)
                    {
                        float x=pixels[p+3]/127.5f-1f;
                        float y=pixels[p+1]/127.5f-1f;
                        float z=MathF.Sqrt(MathF.Max(0,1f-x*x-y*y));
                        converted[p]=pixels[p+3];       // X from source alpha
                        converted[p+1]=pixels[p+1];     // Y from source green
                        converted[p+2]=(byte)Math.Clamp((int)MathF.Round((z*0.5f+0.5f)*255f),0,255);
                        converted[p+3]=255;
                    }
                    using var normal=Image.CreateFromData(
                        width,height,false,Image.Format.Rgba8,converted);
                    normal.GenerateMipmaps();
                    result=ImageTexture.CreateFromImage(normal);
                }
            }
        }
        catch(Exception ex){GD.Print($"Source normal DDS 0x{id:X8} unavailable: {ex.Message}");}
        cache[id]=result;
        return result;
    }

    private Texture2D? LoadOriginalDiffuse(uint id,Dictionary<uint,Texture2D?> cache)
    {
        if(id==0)return null;
        if(cache.TryGetValue(id,out var known))return known;
        if(cache.Count>=32)return null;
        Texture2D? result=null;
        try
        {
            using Image? image=ReadOriginalDdsImage(id);
            if(image!=null)result=ImageTexture.CreateFromImage(image);
        }
        catch(Exception ex){GD.Print($"Source diffuse DDS 0x{id:X8} unavailable: {ex.Message}");}
        cache[id]=result;
        return result;
    }

    private Image? ReadOriginalDdsImage(uint id)
    {
        var original=_archive.FindId(id).FirstOrDefault(r=>
            _archive.GetIdIndexFields(r.Ordinal).Flags==0 &&
            r.UncompressedSize is >=128 and <=20_000_000);
        if(original.UncompressedSize==0)return null;
        byte[] bytes=_archive.ReadDecoded(original,_decoder);
        if(bytes.Length<128||bytes[0]!='D'||bytes[1]!='D'||
            bytes[2]!='S'||bytes[3]!=' ')return null;
        uint width=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16,4));
        uint height=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12,4));
        uint fourCC=BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(84,4));
        int blockBytes=fourCC switch
        {
            0x31545844=>8,   // DXT1 / BC1
            0x33545844=>16,  // DXT3 / BC2
            0x35545844=>16,  // DXT5 / BC3
            _=>0
        };
        if(blockBytes==0||width is 0 or >4096||height is 0 or >4096)
            return null;
        // Nonstandard linear size in ESO DDS; correct only in memory.
        uint expected=((width+3u)/4u)*((height+3u)/4u)*(uint)blockBytes;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20,4),expected);
        var image=new Image();
        var status=image.LoadDdsFromBuffer(bytes);
        if(status!=Error.Ok||image.IsEmpty())
        {image.Dispose();return null;}
        if(image.IsCompressed()&&image.Decompress()!=Error.Ok)
        {image.Dispose();return null;}
        image.Convert(Image.Format.Rgba8);
        return image;
    }

    private void ClearMeshes()
    {
        foreach(var n in _instances)n.QueueFree();
        _instances.Clear();
    }

    private void OnPreviewInput(InputEvent input)
    {
        if(input is InputEventMouseButton button)
        {
            if(button.ButtonIndex==MouseButton.Left)
                _dragging=button.Pressed;
            else if(button.Pressed && button.ButtonIndex==MouseButton.WheelUp)
                _distance*=0.86f;
            else if(button.Pressed && button.ButtonIndex==MouseButton.WheelDown)
                _distance*=1.16f;
            _distance=Math.Clamp(_distance,0.1f,100000f);
            UpdateCamera();
            AcceptEvent();
        }
        else if(input is InputEventMouseMotion motion && _dragging)
        {
            _yaw-=motion.Relative.X*0.008f;
            _pitch=Math.Clamp(_pitch+motion.Relative.Y*0.008f,-1.48f,1.48f);
            UpdateCamera();
            AcceptEvent();
        }
    }

    private void UpdateCamera()
    {
        if(_camera==null)return;
        float cp=Mathf.Cos(_pitch);
        _camera.Position=_target+new Vector3(
            _distance*Mathf.Sin(_yaw)*cp,
            _distance*Mathf.Sin(_pitch),
            _distance*Mathf.Cos(_yaw)*cp);
        _camera.LookAt(_target,Vector3.Up);
    }

    private void ClosePreview()
    {
        _currentRequest++;
        Visible=false;
        _dragging=false;
        ClearMeshes();
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if(Visible && @event is InputEventKey key && key.Pressed && !key.Echo &&
            key.Keycode==Key.Escape)
        {
            ClosePreview();
            GetViewport().SetInputAsHandled();
        }
    }
}
