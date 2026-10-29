// Decoder for ESO land records. The raw source is read from an installed DAT file.
// The heightfield is turned into a Godot ArrayMesh directly in memory.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Godot;

namespace EsoTest;

public sealed class LandTile
{
    public int Width { get; }
    public int Height { get; }
    public float[] Samples { get; }

    private LandTile(int width, int height, float[] samples)
    {
        Width = width;
        Height = height;
        Samples = samples;
    }

    public static bool TryParse(ReadOnlySpan<byte> input, out LandTile? tile)
    {
        tile = null;
        const uint magic = 0xBEEF000C;
        if (input.Length < 95 || BinaryPrimitives.ReadUInt32LittleEndian(input) != magic)
            return false;
        int count = input[9];
        if (count <= 0 || count > 32 || input.Length < 10 + count * 9 + 82)
            return false;
        var sections = new List<(int id, int offset, int length)>();
        int cursor = 10;
        for (int i = 0; i < count; i++)
        {
            int id = input[cursor];
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(cursor + 1));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(input.Slice(cursor + 5));
            cursor += 9;
            if (offset > int.MaxValue || length > int.MaxValue ||
                (ulong)offset + length > (ulong)input.Length)
                return false;
            sections.Add((id, (int)offset, (int)length));
        }
        // UESP's EsoLand.cpp reads the first land section as float32 height samples.
        var section = sections[0];
        if (section.offset == 0 || section.length < 20)
            return false;
        var span = input.Slice(section.offset, section.length);
        int h = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(4)));
        int h2 = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8)));
        int rowBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(12)));
        if (h < 2 || h > 2048 || h != h2 || rowBytes < 8 || rowBytes > 32768 || rowBytes % 4 != 0)
            return false;
        int width = rowBytes / 4;
        if ((long)h * (rowBytes + 2) + 20 > span.Length)
            return false;
        var heights = new float[h * width];
        int p = 16;
        for (int z = 0; z < h; z++)
        {
            int w = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(p));
            p += 2;
            if (w != rowBytes) return false;
            for (int x = 0; x < width; x++)
            {
                float sample = BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(span.Slice(p)));
                if (!float.IsFinite(sample)) return false;
                heights[z * width + x] = sample;
                p += 4;
            }
        }
        tile = new LandTile(width, h, heights);
        return true;
    }

    public ArrayMesh BuildMesh(
        float horizontalScale = 1f, float verticalScale = 0.08f,
        bool trimWest = false, bool trimEast = false,
        bool trimNorth = false, bool trimSouth = false)
    {
        // Some ESO terrain bundles pad their external edges with an entire
        // zero-valued row/column. Do not turn that padding into huge cliffs.
        // Only omit the border if every sample on that edge is exactly zero.
        bool zeroWest = true, zeroEast = true, zeroNorth = true, zeroSouth = true;
        for (int i = 0; i < Height; i++)
        {
            zeroWest &= Samples[i * Width] == 0;
            zeroEast &= Samples[i * Width + Width - 1] == 0;
        }
        for (int i = 0; i < Width; i++)
        {
            zeroNorth &= Samples[i] == 0;
            zeroSouth &= Samples[(Height - 1) * Width + i] == 0;
        }
        int startX = trimWest && zeroWest ? 1 : 0;
        int lastX = trimEast && zeroEast ? Width - 2 : Width - 1;
        int startZ = trimNorth && zeroNorth ? 1 : 0;
        int lastZ = trimSouth && zeroSouth ? Height - 2 : Height - 1;

        using var tool = new SurfaceTool();
        tool.Begin(Mesh.PrimitiveType.Triangles);
        for (int z = startZ; z < lastZ; z++)
            for (int x = startX; x < lastX; x++)
            {
                Add(x, z, horizontalScale, verticalScale);
                Add(x, z + 1, horizontalScale, verticalScale);
                Add(x + 1, z, horizontalScale, verticalScale);
                Add(x + 1, z, horizontalScale, verticalScale);
                Add(x, z + 1, horizontalScale, verticalScale);
                Add(x + 1, z + 1, horizontalScale, verticalScale);
            }
        tool.GenerateNormals();
        return tool.Commit() ?? throw new InvalidOperationException("Godot mesh generation failed");

        void Add(int x, int z, float scaleXZ, float scaleY)
        {
            tool.SetUV(new Vector2((float)x / (Width - 1), (float)z / (Height - 1)));
            tool.AddVertex(new Vector3(x * scaleXZ, Samples[z * Width + x] * scaleY, z * scaleXZ));
        }
    }
}
