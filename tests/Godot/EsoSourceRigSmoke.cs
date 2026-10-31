using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Godot;
using Process = System.Diagnostics.Process;

namespace EsoTest;

/// <summary>Real ESO torso/head skin bind-pose test. All source bytes are
/// local and ignored. Verifies that a pose change reaches named source bones
/// through Godot Skeleton3D and that each mesh's bone indices are retained.
/// This tests original skinning and a limited retarget of the actual
/// ESO PC_Walk clip; it is NOT the game's complete animation graph.</summary>
public partial class EsoSourceRigSmoke : Node3D
{
    private EsoArchive? _archive;
    private OodleDecoder? _decoder;
    private readonly List<Skeleton3D> _skeletons=new();
    [Export] public bool InteractivePreview {get;set;}
    private EsoSourceAnimationClip? _walking;
    private Skeleton3D? _animatedRig;
    private readonly List<(Skeleton3D Rig,EsoSourceAnimationClip Clip)> _animatedPieces=new();
    private readonly List<Node3D> _faceVariants=new();
    private Node3D? _bodyPreview;
    private Node3D? _facePreview;
    private Camera3D? _labCamera;
    private Label? _modeHelp;
    private bool _showFaces=true;
    private int _focusedFace=-1;
    private float _orbitYaw,_orbitPitch;
    private float _faceDistance=0.95f,_bodyDistance=4.15f;
    private bool _dragging;
    private static readonly string[] FaceLabels={"NARROW","NEUTRAL","WIDE"};
    private float _elapsed;
    private bool _playing=true;
    private bool _savedPreview;
    private int _previewFrames;
    public int ActiveRetargetedBones=>_walking?.MappedBoneCount??0;

    public override async void _Ready()
    {
        try
        {
            string root=ProjectSettings.GlobalizePath("res://");
            string eso=System.Environment.GetEnvironmentVariable("ESO_ROOT")??
                "C:/ESO/The Elder Scrolls Online";
            _archive=new EsoArchive(Path.Combine(eso,"depot","eso.mnf"));
            _decoder=OodleDecoder.FromEnvironment(Path.Combine(root,".research","ooz.dll"))
                ??throw new InvalidDataException("Local Oodle decoder unavailable");
            var torso=CreateSkin(0x000DEF03,"Torso_",
                InteractivePreview?new Vector3(0,0,-4f):new Vector3(-0.9f,0,-4f));
            var head=CreateSkin(0x000E2CFF,"Head_004",
                InteractivePreview?new Vector3(0,0,-4f):new Vector3(0.9f,0,-4f));
            if(torso.Skeleton.GetBoneCount()!=43 || head.Skeleton.GetBoneCount()!=39)
                throw new Exception("Source rig bone counts changed");
            foreach(var pair in new[]{torso,head})
            {
                if(pair.Mesh.Skin.GetBindCount()!=pair.Source.BindingNames.Length)
                    throw new Exception("Missing native skin binding");
                var arrays=((ArrayMesh)pair.Mesh.Mesh).SurfaceGetArrays(0);
                var influence=(int[])arrays[(int)Mesh.ArrayType.Bones];
                var weights=(float[])arrays[(int)Mesh.ArrayType.Weights];
                if(influence.Length!=pair.Source.VertexCount*4 || weights.Length!=influence.Length)
                    throw new Exception("Source skin vertex buffers missing");
            }
            int arm=torso.Skeleton.FindBone("defBn_upperArm_R");
            int jaw=head.Skeleton.FindBone("H_jaw3");
            int face=head.Skeleton.FindBone("H");
            if(arm<0||jaw<0||face<0)throw new Exception("Source pose or face control joints missing");
            ApplyFacePreset(head.Skeleton,1);
            if(head.Skeleton.GetBonePoseScale(face).X >= 0.98f)
                throw new Exception("Original head source bone scaling did not apply");
            ApplyFacePreset(head.Skeleton,2);
            var before=torso.Skeleton.GetBoneGlobalPose(arm);
            torso.Skeleton.SetBonePoseRotation(arm,new Quaternion(Vector3.Right,0.35f));
            head.Skeleton.SetBonePoseRotation(jaw,new Quaternion(Vector3.Right,0.10f));
            await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
            var after=torso.Skeleton.GetBoneGlobalPose(arm);
            if(before.Basis.IsEqualApprox(after.Basis))
                throw new Exception("Source bone pose did not respond to rotation");
            string clipFile=Path.Combine(ProjectSettings.GlobalizePath("res://"),
                ".research","npc-rigs","clip-0008942F-source24.json");
            if(!File.Exists(clipFile))ExportOriginalWalk(clipFile);
            var walk=EsoSourceAnimationClip.Read(clipFile,torso.Skeleton);
            if(walk.Name!="PC_Walk"||walk.MappedBoneCount<5)
                throw new Exception($"Source walk clip isn't compatible: {walk.Name}, {walk.MappedBoneCount} mapped bones");
            walk.Apply(torso.Skeleton,0f);
            var restRotation=torso.Skeleton.GetBoneRest(arm).Basis.GetRotationQuaternion();
            if(!torso.Skeleton.GetBonePoseRotation(arm).IsEqualApprox(restRotation))
                throw new Exception("Source retarget corrupted ESO bind pose at animation frame zero");
            var firstPose=torso.Skeleton.GetBoneGlobalPose(arm);
            walk.Apply(torso.Skeleton,0.35f);
            var secondPose=torso.Skeleton.GetBoneGlobalPose(arm);
            if(firstPose.Basis.IsEqualApprox(secondPose.Basis))
                throw new Exception("Original PC_Walk sampled curves did not animate the upper arm");
            // The user's walking repro was a full sideways collapse. Exercise
            // the safer source-timed anatomical remapping against the real
            // customization skeleton, checking left/right alternation, bounds
            // and that returning to time zero restores the exact bind pose.
            float leftSource=walk.OriginalJointSwing("mshBn_thigh_L",0.33f);
            float rightSource=walk.OriginalJointSwing("mshBn_thigh_R",0.33f);
            if(walk.SourceCurveCount<6 || leftSource> -0.30f || rightSource<0.30f)
                throw new Exception("Original ESO leg curves are unavailable or not alternating");
            EsoRigLabGait.Apply(torso.Skeleton,walk,0.33f);
            int leftThigh=torso.Skeleton.FindBone("defBn_thigh_L");
            int rightThigh=torso.Skeleton.FindBone("defBn_thigh_R");
            float leftPose=torso.Skeleton.GetBonePoseRotation(leftThigh)
                .AngleTo(torso.Skeleton.GetBoneRest(leftThigh).Basis.GetRotationQuaternion());
            float rightPose=torso.Skeleton.GetBonePoseRotation(rightThigh)
                .AngleTo(torso.Skeleton.GetBoneRest(rightThigh).Basis.GetRotationQuaternion());
            if(leftPose is <0.10f or >0.45f || rightPose is <0.10f or >0.45f)
                throw new Exception("Gait anatomical thigh hinges are implausibly small or overrotated");
            int upperBody=torso.Skeleton.FindBone("defCtrl_body");
            if(torso.Skeleton.GetBoneGlobalPose(upperBody).Basis.Y.Dot(Vector3.Up)<0.98f)
                throw new Exception("Gait tilted the character's whole body sideways");
            EsoRigLabGait.Apply(torso.Skeleton,walk,0f);
            if(!torso.Skeleton.GetBonePoseRotation(leftThigh).IsEqualApprox(
                torso.Skeleton.GetBoneRest(leftThigh).Basis.GetRotationQuaternion()))
                throw new Exception("Gait time zero must preserve the imported bind pose");
            GD.Print($"PASS source ESO skinned meshes: torso {torso.Source.VertexCount} vertices / 43 bones, " +
                $"head {head.Source.VertexCount} vertices / 39 bones, source PC_Walk retargeted {walk.MappedBoneCount} bones");
            if(InteractivePreview)
            {
                _animatedRig=torso.Skeleton;
                _walking=walk;
                _animatedPieces.Add((torso.Skeleton,walk));
                _elapsed=0;
                // A full seven-part test body; individual original garment
                // files contain their own subset skeletons and skin tables.
                // Keep parts independently skinned until the canonical
                // shared humanoid skeleton has been reconstructed.
                foreach(var (id,prefix) in new[]{
                    (0x000DEE23u,"Feet_"),
                    (0x000DEE5Bu,"LegsLower_"),
                    (0x000DEE9Du,"LegsUpper_"),
                    (0x000DEDDBu,"ArmsUpper_"),
                    (0x000DEDA1u,"ArmsLower_")
                })
                {
                    var part=CreateSkin(id,prefix,new Vector3(0,0,-4));
                    var clip=EsoSourceAnimationClip.Read(clipFile,part.Skeleton);
                    _animatedPieces.Add((part.Skeleton,clip));
                }
                head.Skeleton.ResetBonePoses();
                // Keep the existing full-body assets in their own display,
                // independent from the closer 3-way facial comparison.
                _bodyPreview=new Node3D{Name="BodyDisplay"};
                AddChild(_bodyPreview);
                foreach(var piece in _skeletons.ToArray())
                    piece.Reparent(_bodyPreview,true);
                AddFaceDetailsAt(_bodyPreview,new Vector3(0,0,-4),2);
                try
                {
                    var hairMesh=LoadPreviewHair(0x0009EC43);
                    _bodyPreview.AddChild(new MeshInstance3D
                    {
                        Name="OriginalESOHairOnBody",
                        Mesh=hairMesh,
                        Position=new Vector3(0,0,-4),
                        MaterialOverride=FeatureMaterial(new Color("#674b37"))
                    });
                }
                catch(Exception e)
                {
                    GD.PushWarning("Rig lab full-body hairstyle: "+e.Message);
                }
                BuildFaceGallery();
                ConfigureInteractivePreview();
                // Source-clip direct retarget remains in the importer test,
                // never in the player-facing gait preview.
                _playing=System.Environment.GetEnvironmentVariable("ESO_RIG_CAPTURE_BODY")=="1";
                _showFaces=!_playing;
                if(float.TryParse(System.Environment.GetEnvironmentVariable("ESO_RIG_PREVIEW_YAW"),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,out float yaw))
                    _orbitYaw=Mathf.Clamp(yaw,-1.4f,1.4f);
                if(int.TryParse(System.Environment.GetEnvironmentVariable("ESO_RIG_FOCUS_FACE"),out int selected)
                    && selected is >=1 and <=3)
                {
                    _showFaces=true;
                    _focusedFace=selected-1;
                    _faceDistance=0.62f;
                }
                UpdateCamera();
                foreach(var piece in _animatedPieces)piece.Rig.ResetBonePoses();
                SetProcess(true);
                return;
            }
            string? screenshot=System.Environment.GetEnvironmentVariable("ESO_RIG_PREVIEW_PATH");
            if(!string.IsNullOrEmpty(screenshot)&&DisplayServer.GetName()!="headless")
            {
                AddChild(new DirectionalLight3D{
                    RotationDegrees=new Vector3(-45,22,0),LightEnergy=1.4f});
                AddChild(new WorldEnvironment{Environment=new Godot.Environment{
                    BackgroundMode=Godot.Environment.BGMode.Color,
                    BackgroundColor=new Color("#344353"),
                    AmbientLightSource=Godot.Environment.AmbientSource.Color,
                    AmbientLightColor=Colors.White,AmbientLightEnergy=0.9f}});
                var camera=new Camera3D{Current=true,Fov=34,Position=new Vector3(0,1.8f,4.2f)};
                AddChild(camera);camera.LookAt(new Vector3(0,1.6f,-4));
                for(int i=0;i<5;i++)await ToSignal(GetTree(),SceneTree.SignalName.ProcessFrame);
                GD.Print($"RIG PREVIEW {GetViewport().GetTexture().GetImage().SavePng(screenshot)} {screenshot}");
            }
            GetTree().Quit(0);
        }
        catch(Exception e)
        {
            GD.PushError("Source ESO rig smoke failed: "+e);
            GetTree().Quit(1);
        }
    }

    private static StandardMaterial3D FeatureMaterial(Color colour) =>
        new()
        {
            AlbedoColor=colour,
            Roughness=0.78f,
            CullMode=BaseMaterial3D.CullModeEnum.Disabled
        };

    private static void FacialDetail(Node3D owner,Vector3 spot,
        Vector3 size,Color colour,string name)
    {
        var mesh=new SphereMesh{Radius=1f,Height=2f,
            RadialSegments=20,Rings=12};
        owner.AddChild(new MeshInstance3D
        {
            Name=name,
            Mesh=mesh,
            Position=spot,
            Scale=size,
            MaterialOverride=FeatureMaterial(colour)
        });
    }

    private static void AddFaceDetailsAt(Node3D parent,Vector3 sourcePosition,int variant)
    {
        var source=new Node3D
        {
            Name="FaceFeatureAttachment",
            Position=sourcePosition
        };
        parent.AddChild(source);
        AddFaceDetails(source,variant);
    }

    private static void AddFaceDetails(Node3D source,int variant)
    {
        // Diagnostic appearance only. The face SHELL is a real skinned ESO
        // mesh; its material system has not been recovered. These small eye,
        // eyebrow and mouth proxies make morphology differences inspectable
        // without pretending to have the original character atlas.
        var features=new Node3D{Name="ProvisionalFacialFeatures",
            Position=new Vector3(0,2.1f,0.084f)};
        source.AddChild(features);
        float x=variant switch {1=>0.90f,3=>1.11f,_=>1f};
        features.Scale=new Vector3(x,1,1);
        for(int side=-1;side<=1;side+=2)
        {
            float a=side*0.035f;
            FacialDetail(features,new Vector3(a,0.023f,0.005f),
                new Vector3(0.0125f,0.006f,0.003f),
                new Color("#eeece7"),$"EyeWhite_{side}");
            FacialDetail(features,new Vector3(a,0.023f,0.010f),
                new Vector3(0.0048f,0.0056f,0.0027f),
                new Color("#537778"),$"Iris_{side}");
            FacialDetail(features,new Vector3(a,0.023f,0.012f),
                new Vector3(0.0025f,0.0037f,0.0012f),
                new Color("#15191d"),$"Pupil_{side}");
            FacialDetail(features,new Vector3(a,0.051f,0.001f),
                new Vector3(0.023f,0.0032f,0.004f),
                new Color("#5e493b"),$"Eyebrow_{side}");
        }
        // The source face lacks its original skin/feature material. Keep
        // these markers shallow, especially around nose and lips, until
        // authentic customization atlases are decoded.
        FacialDetail(features,new Vector3(0,-0.029f,0.010f),
            new Vector3(0.0085f,0.014f,0.007f),
            new Color("#b8a08b"),"NoseProxy");
        FacialDetail(features,new Vector3(0,-0.079f,0.013f),
            new Vector3(0.0175f,0.0027f,0.003f),
            new Color("#946961"),"MouthProxy");
    }

    private ArrayMesh LoadPreviewHair(uint sourceId)
    {
        var record=_archive!.FindId(sourceId).First(r=>
            _archive.GetIdIndexFields(r.Ordinal).Flags==0);
        string folder=Path.Combine(ProjectSettings.GlobalizePath("res://"),
            ".research","npc-rigs");
        string basename=$"{sourceId:X8}-a{record.ArchiveIndex}-o{record.Offset}-"+
            $"h{record.Hash:X8}-u{record.UncompressedSize}";
        string meshFile=Path.Combine(folder,basename+"-mesh-v5.esm");
        if(!File.Exists(meshFile))
            RunHelper(new[]{sourceId.ToString("X8"),meshFile},null);
        return EsoHumanoidVisualLayer.LoadMesh(meshFile,"HAR_");
    }

    private void BuildFaceGallery()
    {
        _facePreview=new Node3D{Name="CloseFaceComparison"};
        AddChild(_facePreview);
        _faceVariants.Clear();
        ArrayMesh? hair=null;
        try{hair=LoadPreviewHair(0x0009EC43);}
        catch(Exception e)
        {
            GD.PushWarning("Original ESO hairstyle not available for face lab: "+e.Message);
        }
        for(int i=0;i<3;i++)
        {
            int preset=i+1;
            var modelRoot=new Node3D{
                Name=$"FaceVariant_{preset}",
                Position=new Vector3((i-1)*0.43f,0,-3.15f)
            };
            _facePreview.AddChild(modelRoot);
            _faceVariants.Add(modelRoot);
            var face=CreateSkin(0x000E2CFF,"Head_004",Vector3.Zero);
            face.Skeleton.Reparent(modelRoot,true);
            face.Skeleton.Position=Vector3.Zero;
            ApplyFacePreset(face.Skeleton,preset);
            AddFaceDetails(modelRoot,preset);
            if(hair!=null)
            {
                modelRoot.AddChild(new MeshInstance3D{
                    Name="OriginalESOHairstyle",
                    Mesh=hair,
                    MaterialOverride=FeatureMaterial(new Color("#674b37"))});
            }
            var label=new Label3D{
                Name="VariantLabel",
                Text=$"{preset}. {FaceLabels[i]}",
                Position=new Vector3(0,1.90f,0.16f),
                Modulate=new Color("#f7f3e7"),
                PixelSize=0.00065f,
                FontSize=29,
                OutlineSize=0
            };
            modelRoot.AddChild(label);
        }
        GD.Print("RIG FACE GALLERY: 3 source skinned head meshes, ",
            "original hair if available, provisional face details, ",
            "three independent customization-bone presets");
    }

    private void UpdateCamera()
    {
        if(_labCamera==null || _facePreview==null || _bodyPreview==null)return;
        _facePreview.Visible=_showFaces;
        _bodyPreview.Visible=!_showFaces;
        for(int i=0;i<_faceVariants.Count;i++)
            _faceVariants[i].Visible=_focusedFace<0 || i==_focusedFace;
        float focusX=_focusedFace<0?0:(_focusedFace-1)*0.43f;
        Vector3 target=_showFaces
            ? new Vector3(focusX,2.13f,-3.15f)
            : new Vector3(0,1.2f,-4f);
        float distance=_showFaces?_faceDistance:_bodyDistance;
        var direction=new Vector3(Mathf.Sin(_orbitYaw)*Mathf.Cos(_orbitPitch),
            Mathf.Sin(_orbitPitch),
            Mathf.Cos(_orbitYaw)*Mathf.Cos(_orbitPitch));
        _labCamera.Position=target+direction*distance;
        _labCamera.LookAt(target,Vector3.Up);
        if(_modeHelp!=null)
            _modeHelp.Text=_showFaces
                ? "F FACES | B BODY / GAIT | 1-3 enlarge individual faces | 0 compare all\n"+
                  "Drag to orbit | Wheel zoom | Esc exit\n"+
                  "Original ESO head geometry, experimental facial details and sizes, not ESO skin textures"
                : "B BODY | F FACES | Space walk / pause | Drag orbit | Wheel zoom\n"+
                  (_playing?"GAIT ACTIVE: source PC_Walk joint timing, remapped onto limited anatomical hinges":
                            "REST POSE: Space starts source-timed in-place gait")+
                  "\nReal ESO bone weights. Partial axis mapping, not a complete ESO animation rig.";
    }

    private void ConfigureInteractivePreview()
    {
        AddChild(new DirectionalLight3D{
            RotationDegrees=new Vector3(-48,35,0),LightEnergy=1.65f});
        AddChild(new WorldEnvironment{Environment=new Godot.Environment{
            BackgroundMode=Godot.Environment.BGMode.Color,
            BackgroundColor=new Color("#344353"),
            AmbientLightSource=Godot.Environment.AmbientSource.Color,
            AmbientLightColor=Colors.White,AmbientLightEnergy=0.85f}});
        _labCamera=new Camera3D{Name="RigLabCamera",Current=true,Fov=44};
        AddChild(_labCamera);
        var hud=new CanvasLayer{Name="RigLabHUD"};
        AddChild(hud);
        var hint=new Label
        {
            Position=new Vector2(22,22),
            Text="ESO RIG LAB | experimental character appearance and gait",
        };
        hint.AddThemeFontSizeOverride("font_size",18);
        hud.AddChild(hint);
        _modeHelp=new Label{Position=new Vector2(22,58)};
        _modeHelp.AddThemeFontSizeOverride("font_size",16);
        hud.AddChild(_modeHelp);
        Input.MouseMode=Input.MouseModeEnum.Visible;
        UpdateCamera();
    }

    public override void _Process(double delta)
    {
        if(!InteractivePreview||_walking==null||_animatedRig==null)return;
        if(_playing)
        {
            if(float.TryParse(System.Environment.GetEnvironmentVariable("ESO_RIG_PREVIEW_TIME"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,out float sampleTime))
                _elapsed=sampleTime;
            else
                _elapsed+=(float)delta;
            foreach(var part in _animatedPieces)
                EsoRigLabGait.Apply(part.Rig,part.Clip,_elapsed);
        }
        int targetFrame=int.TryParse(System.Environment.GetEnvironmentVariable("ESO_RIG_PREVIEW_FRAME"),out int requestedFrame)
            ? Math.Clamp(requestedFrame,12,500):100;
        if(!_savedPreview && ++_previewFrames>targetFrame)
        {
            string? save=System.Environment.GetEnvironmentVariable("ESO_RIG_PREVIEW_PATH");
            if(!string.IsNullOrWhiteSpace(save) && DisplayServer.GetName()!="headless")
            {
                _savedPreview=true;
                var status=GetViewport().GetTexture().GetImage().SavePng(save);
                GD.Print($"RIG LAB PREVIEW {status} {save}");
            }
        }
    }

    private static void ApplyFacePreset(Skeleton3D face,int preset)
    {
        // Source H and H_jaw3 bones genuinely influence head vertices.
        // These are diagnostic morphology controls, not recovered original
        // NPC complexion or character-creator slider settings.
        int skull=face.FindBone("H"), jaw=face.FindBone("H_jaw3");
        if(skull<0||jaw<0)return;
        var headScale=preset switch
        {
            1=>new Vector3(0.93f,1.025f,0.97f),
            3=>new Vector3(1.065f,0.982f,1.025f),
            _=>Vector3.One
        };
        var jawScale=preset switch
        {
            1=>new Vector3(0.95f,1f,1f),
            3=>new Vector3(1.04f,1f,1f),
            _=>Vector3.One
        };
        face.SetBonePoseScale(skull,headScale);
        face.SetBonePoseScale(jaw,jawScale);
    }

    public override void _Input(InputEvent evt)
    {
        if(!InteractivePreview)return;
        if(evt is InputEventMouseButton mouse)
        {
            if(mouse.ButtonIndex==MouseButton.Left)
                _dragging=mouse.Pressed;
            if(mouse.Pressed && mouse.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                float factor=mouse.ButtonIndex==MouseButton.WheelUp?0.84f:1.19f;
                if(_showFaces)
                    _faceDistance=Mathf.Clamp(_faceDistance*factor,0.52f,2.3f);
                else
                    _bodyDistance=Mathf.Clamp(_bodyDistance*factor,2.4f,7.5f);
                UpdateCamera();
            }
            return;
        }
        if(evt is InputEventMouseMotion motion && _dragging)
        {
            _orbitYaw=Mathf.Clamp(_orbitYaw+motion.Relative.X*0.005f,-1.4f,1.4f);
            _orbitPitch=Mathf.Clamp(_orbitPitch-motion.Relative.Y*0.005f,-0.55f,0.55f);
            UpdateCamera();
            return;
        }
        if(evt is not InputEventKey key||!key.Pressed||key.Echo)return;
        switch(key.Keycode)
        {
            case Key.F:
                _showFaces=true;_focusedFace=-1;
                _orbitYaw=0;_orbitPitch=0;
                _faceDistance=0.95f;
                UpdateCamera();
                break;
            case Key.B:
                _showFaces=false;
                _orbitYaw=0;_orbitPitch=0;
                UpdateCamera();
                break;
            case Key.Key0:
                _showFaces=true;_focusedFace=-1;_faceDistance=0.95f;
                UpdateCamera();
                break;
            case Key.Key1 or Key.Key2 or Key.Key3:
                _showFaces=true;
                _focusedFace=key.Keycode switch {Key.Key1=>0,Key.Key2=>1,_=>2};
                _faceDistance=0.62f;
                UpdateCamera();
                break;
            case Key.Space:
                _showFaces=false;
                _playing=!_playing;
                if(!_playing)
                    foreach(var part in _animatedPieces)part.Rig.ResetBonePoses();
                UpdateCamera();
                break;
            case Key.Escape:
                GetTree().Quit();
                break;
        }
    }

    private void ExportOriginalWalk(string output)
    {
        string root=ProjectSettings.GlobalizePath("res://");
        string helper=Path.Combine(root,"tools","ModelGeometry","bin","Debug","net8.0",
            "ModelGeometry.dll");
        string granny=System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
            Path.Combine(root,".research","uesp-esoapps","EsoExtractData",
                "EsoExtractData","granny2_x64.dll");
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardError=true,RedirectStandardOutput=true
        };
        foreach(var arg in new[]{helper,"--sample-animation",granny,
            "0008942F",_archive!.SourcePath,_decoder!.LibraryPath,output,"24"})
            process.StartInfo.ArgumentList.Add(arg);
        if(!process.Start())throw new IOException("Cannot sample original source animation");
        var outputTask=process.StandardOutput.ReadToEndAsync();
        var errorTask=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Original ESO animation sampler exceeded 45 seconds");
        }
        _=outputTask.GetAwaiter().GetResult();
        string errors=errorTask.GetAwaiter().GetResult();
        if(process.ExitCode!=0||!File.Exists(output))
            throw new InvalidDataException("Original source clip sample failed: "+
                errors[..Math.Min(errors.Length,300)]);
    }

    private (EsoSourceRig Source,Skeleton3D Skeleton,MeshInstance3D Mesh)
        CreateSkin(uint id,string prefix,Vector3 position)
    {
        var record=_archive!.FindId(id).First(r=>
            _archive.GetIdIndexFields(r.Ordinal).Flags==0);
        string root=ProjectSettings.GlobalizePath("res://");
        string folder=Path.Combine(root,".research","npc-rigs");
        Directory.CreateDirectory(folder);
        string basename=$"{id:X8}-a{record.ArchiveIndex}-o{record.Offset}-"+
            $"h{record.Hash:X8}-u{record.UncompressedSize}";
        string skinFile=Path.Combine(folder,basename+"-skin-v1.json");
        string meshFile=Path.Combine(folder,basename+"-mesh-v5.esm");
        if(!File.Exists(skinFile))
            RunHelper(new[]{"--inspect-rig",id.ToString("X8")},skinFile);
        if(!File.Exists(meshFile))
            RunHelper(new[]{id.ToString("X8"),meshFile},null);
        var rig=EsoSourceRig.Read(skinFile);
        var mesh=EsoHumanoidVisualLayer.LoadMesh(meshFile,prefix,rig);
        var pair=rig.BuildSkeleton();
        pair.Skeleton.Name=$"Skeleton_{id:X8}";
        pair.Skeleton.Position=position;
        AddChild(pair.Skeleton);
        var instance=new MeshInstance3D
        {
            Name=$"SkinnedSource_{id:X8}",
            Mesh=mesh, Skin=pair.Skin,Skeleton=new NodePath(".."),
            MaterialOverride=new StandardMaterial3D{
                AlbedoColor=new Color("#b3a18a"),Roughness=0.85f,
                CullMode=BaseMaterial3D.CullModeEnum.Disabled}
        };
        pair.Skeleton.AddChild(instance);
        _skeletons.Add(pair.Skeleton);
        return (rig,pair.Skeleton,instance);
    }

    private void RunHelper(string[] args,string? output)
    {
        string root=ProjectSettings.GlobalizePath("res://");
        string helper=Path.Combine(root,"tools","ModelGeometry","bin","Debug","net8.0",
            "ModelGeometry.dll");
        string granny=System.Environment.GetEnvironmentVariable("ESO_GRANNY_DLL")??
            Path.Combine(root,".research","uesp-esoapps","EsoExtractData",
                "EsoExtractData","granny2_x64.dll");
        if(!File.Exists(helper)||!File.Exists(granny))
            throw new FileNotFoundException("Source rig helper or native decoder absent");
        using var process=new Process();
        process.StartInfo=new ProcessStartInfo("dotnet")
        {
            WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true
        };
        process.StartInfo.ArgumentList.Add(helper);
        if(args[0]=="--inspect-rig")process.StartInfo.ArgumentList.Add(args[0]);
        process.StartInfo.ArgumentList.Add(granny);
        process.StartInfo.ArgumentList.Add(args[args[0]=="--inspect-rig"?1:0]);
        if(args[0]!="--inspect-rig")process.StartInfo.ArgumentList.Add(args[1]);
        process.StartInfo.ArgumentList.Add(_archive!.SourcePath);
        process.StartInfo.ArgumentList.Add(_decoder!.LibraryPath);
        if(!process.Start())throw new IOException("Cannot start isolated GR2 helper");
        var stdout=process.StandardOutput.ReadToEndAsync();
        var stderr=process.StandardError.ReadToEndAsync();
        if(!process.WaitForExit(45000))
        {
            process.Kill(entireProcessTree:true);
            throw new TimeoutException("Source skin extraction timed out");
        }
        string result=stdout.GetAwaiter().GetResult();
        string error=stderr.GetAwaiter().GetResult();
        if(process.ExitCode!=0)throw new IOException("Source skin extraction: "+
            error[..Math.Min(error.Length,300)]);
        if(output!=null)
        {
            string tmp=output+".tmp";
            File.WriteAllText(tmp,result);
            File.Move(tmp,output,true);
        }
    }

    public override void _ExitTree()
    {
        _decoder?.Dispose();
        _archive?.Dispose();
    }
}
