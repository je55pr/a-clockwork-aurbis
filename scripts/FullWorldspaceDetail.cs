using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace EsoTest;

/// <summary>
/// Builds the entire selected source worldspace at native terrain resolution
/// with original near-field paint weights and DDS materials. A 5x5 group is
/// only a texture-array batching unit, never the limit of rendered terrain.
/// Incomplete or undecodable groups retain the original baked LOD fallback.
/// </summary>
public partial class FullWorldspaceDetail : Node3D
{
    private readonly record struct Batch(
        int PaintX, int PaintZ, int DrawX, int DrawZ, int EndX, int EndZ);

    private EsoArchive _archive = null!;
    private OodleDecoder _decoder = null!;
    private IReadOnlyList<TerrainBundle> _bundles = null!;
    private TerrainBundle _section = null!;
    private ShaderMaterial _baseMaterial = null!;
    private FixturePlacementGraph.Component _group = null!;
    private readonly Queue<Batch> _pending = new();
    private readonly List<ShaderMaterial> _materials = new();
    private Image _coverageImage = null!;
    private ImageTexture _coverageTexture = null!;
    private int _width, _height, _complete, _failed, _renderedTiles, _totalBatches;
    private float _worldLow, _worldCenterX, _worldCenterZ;
    private bool _normalEnabled, _tintEnabled;
    public event Action<string>? Progress;

    public void Setup(EsoArchive archive, OodleDecoder decoder,
        IReadOnlyList<TerrainBundle> bundles, TerrainBundle section,
        ShaderMaterial baseMaterial, float worldLowestHeight,
        bool normalEnabled, bool tintEnabled)
    {
        _archive=archive;
        _decoder=decoder;
        _bundles=bundles;
        _section=section;
        _baseMaterial=baseMaterial;
        _worldLow=worldLowestHeight;
        _normalEnabled=normalEnabled;
        _tintEnabled=tintEnabled;
        GD.Print($"FULL WORLDSPACE DETAIL optional layout/colour overlay: {(tintEnabled?"enabled":"disabled")}");
        _group=new FixturePlacementGraph.Component(
            new Dictionary<uint,FixturePlacementGraph.Position>{
                [section.DescriptorId]=new(0,0)
            },0,0,0);
        _width=section.MaximumX-section.OriginX+1;
        _height=section.MaximumY-section.OriginY+1;
        if(_width<5||_height<5||_width>1024||_height>1024)
            throw new InvalidOperationException(
                "Original near-field rendering requires a source area of at least 5x5 tiles");
        _worldCenterX=(section.OriginX+section.MaximumX+1)*50f;
        _worldCenterZ=(section.OriginY+section.MaximumY+1)*50f;

        // Render every tile exactly once, including 1-4-tile boundary strips.
        // Edge groups reuse an overlapping 5x5 source palette, but their
        // geometry is restricted to their exclusive, nonoverlapping draw range.
        var available=section.Tiles.Select(record=>(
            X:(int)(record.FileId>>16), Z:(int)(record.FileId&0xffff))).ToHashSet();
        for(int z=section.OriginY;z<=section.MaximumY;z+=5)
        for(int x=section.OriginX;x<=section.MaximumX;x+=5)
        {
            int paintX=Math.Min(x,section.MaximumX-4);
            int paintZ=Math.Min(z,section.MaximumY-4);
            bool sourceComplete=true;
            for(int dz=0;dz<5 && sourceComplete;dz++)
            for(int dx=0;dx<5;dx++)
                if(!available.Contains((paintX+dx,paintZ+dz)))
                {
                    sourceComplete=false;
                    break;
                }
            if(sourceComplete)
                _pending.Enqueue(new Batch(paintX,paintZ,x,z,
                    Math.Min(x+5,section.MaximumX+1),
                    Math.Min(z+5,section.MaximumY+1)));
            else
                _failed++;
        }
        _totalBatches=_pending.Count+_failed;

        _coverageImage=Image.CreateFromData(_width,_height,false,
            Image.Format.L8,new byte[_width*_height]);
        _coverageTexture=ImageTexture.CreateFromImage(_coverageImage);
        _baseMaterial.SetShaderParameter("full_detail_mask",_coverageTexture);
        _baseMaterial.SetShaderParameter("full_detail_mask_origin",
            new Vector2I(section.OriginX,section.OriginY));
        _baseMaterial.SetShaderParameter("full_detail_mask_size",
            new Vector2I(_width,_height));
        _baseMaterial.SetShaderParameter("use_full_detail_mask",true);
    }

    public void SetTintEnabled(bool enabled)
    {
        _tintEnabled=enabled;
        foreach(var material in _materials)
            material.SetShaderParameter("apply_original_tint",enabled);
    }

    public void SetNormalEnabled(bool enabled)
    {
        _normalEnabled=enabled;
        foreach(var material in _materials)
            material.SetShaderParameter("use_original_normals",enabled);
    }

    public override void _Ready()
    {
        GD.Print($"FULL WORLDSPACE DETAIL section={_section.DescriptorId} " +
            $"batches={_totalBatches} source tiles={_section.Tiles.Count}");
        Report();
    }

    public override void _Process(double delta)
    {
        if(_pending.Count==0)
        {
            SetProcess(false);
            Report();
            return;
        }
        var next=_pending.Dequeue();
        try
        {
            var patchCenter=new Vector2I(next.PaintX+2,next.PaintZ+2);
            var fine=AutoRegionRenderer.Create(
                _archive,_decoder,_bundles,_group,
                sampleStep:1,detailCenter:patchCenter,
                renderFrom:new Vector2I(next.DrawX,next.DrawZ),
                renderToExclusive:new Vector2I(next.EndX,next.EndZ));
            TerrainNearFieldColors.Result? colour=null;
            try
            {
                colour=TerrainNearFieldColors.LoadPatch(
                    _archive,_decoder,_section,patchCenter.X,patchCenter.Y);
            }
            catch(Exception e)
            {
                GD.PushWarning($"Original tile tint unavailable at {patchCenter}: {e.Message}");
            }
            TerrainNearFieldMaterials.Result near;
            try
            {
                near=TerrainNearFieldMaterials.LoadPatch(
                    _archive,_decoder,_section,patchCenter.X,patchCenter.Y,
                    colour,_normalEnabled,_tintEnabled);
            }
            catch
            {
                fine.Mesh.Dispose();
                throw;
            }
            var meshNode=new MeshInstance3D{
                Name=$"OriginalNearField_{next.DrawX}_{next.DrawZ}",
                Mesh=fine.Mesh,
                MaterialOverride=near.Material,
                Position=new Vector3(
                    (next.PaintX+2.5f)*100f-_worldCenterX,
                    fine.LowestHeight-_worldLow,
                    (next.PaintZ+2.5f)*100f-_worldCenterZ)
            };
            AddChild(meshNode);
            _materials.Add(near.Material);

            // Reveal the completed chunk, not an empty hole while loading.
            // Base mesh remains visible under chunks that failed to decode.
            for(int z=next.DrawZ;z<next.EndZ;z++)
            for(int x=next.DrawX;x<next.EndX;x++)
            {
                _coverageImage.SetPixel(x-_section.OriginX,z-_section.OriginY,
                    Colors.White);
                _renderedTiles++;
            }
            _coverageTexture.Update(_coverageImage);
            _complete++;
        }
        catch(Exception ex)
        {
            _failed++;
            GD.PushWarning(
                $"Full-worldspace near-field batch {next.DrawX},{next.DrawZ} " +
                $"unavailable; retaining source LOD: {ex.Message}");
        }
        if((_complete+_failed)%5==0||_pending.Count==0)Report();
    }

    private void Report()
    {
        string message=$"Full-resolution worldspace section {_section.DescriptorId}: " +
            $"{_complete+_failed}/{_totalBatches} terrain groups, " +
            $"{_renderedTiles:N0}/{_section.Tiles.Count:N0} source tiles at 65x65, " +
            $"{_failed} fallback groups. " +
            (_pending.Count==0
                ?"Complete. Original material UV rotation/bias remains provisional."
                :"Loading original terrain materials...");
        Progress?.Invoke(message);
        if(_pending.Count==0)GD.Print("FULL WORLDSPACE DETAIL COMPLETE "+message);
    }

    public override void _ExitTree()
    {
        _baseMaterial?.SetShaderParameter("use_full_detail_mask",false);
        _coverageTexture?.Dispose();
        _coverageImage?.Dispose();
        _materials.Clear();
    }
}
