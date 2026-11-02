using System;
using System.Buffers.Binary;

/// <summary>
/// Inspection-only evidence from genuine BC3/DXT5 ESO DDS compressed mip bytes.
/// A source-normal packed AG pair usually has means near 0.5 in both channels.
/// This probe does not establish what mesh a texture belongs to.
/// </summary>
internal static class EsoDxt5NormalEvidence
{
    internal static bool TrySample(ReadOnlySpan<byte> dds,
        out float meanGreen,out float meanAlpha)
    {
        meanGreen=0;meanAlpha=0;
        if(dds.Length<128 || BinaryPrimitives.ReadUInt32LittleEndian(dds)!=0x20534444u ||
           BinaryPrimitives.ReadUInt32LittleEndian(dds[84..])!=0x35545844u)
            return false;
        int width=BinaryPrimitives.ReadInt32LittleEndian(dds[16..]);
        int height=BinaryPrimitives.ReadInt32LittleEndian(dds[12..]);
        int mips=BinaryPrimitives.ReadInt32LittleEndian(dds[28..]);
        if(width is <16 or >4096 ||height is <16 or >4096 ||mips is <1 or >16)
            return false;
        int ptr=128,w=width,h=height;
        for(int mip=0;mip<mips;mip++)
        {
            int cols=(w+3)/4,rows=(h+3)/4;
            long size=(long)cols*rows*16;
            if(size>dds.Length-ptr)return false;
            if(w<=32&&h<=32&&w>=8&&h>=8)
            {
                double green=0,alpha=0;
                int pixels=0;
                Span<float> ag=stackalloc float[8];
                Span<float> gp=stackalloc float[4];
                for(int by=0;by<rows;by++)
                    for(int bx=0;bx<cols;bx++)
                    {
                        int p=ptr+(by*cols+bx)*16;
                        int a0=dds[p],a1=dds[p+1];
                        ag[0]=a0/255f;
                        ag[1]=a1/255f;
                        if(a0>a1)
                        {
                            for(int k=1;k<=6;k++)
                                ag[k+1]=((7-k)*a0+k*a1)/(7f*255f);
                        }
                        else
                        {
                            for(int k=1;k<=4;k++)
                                ag[k+1]=((5-k)*a0+k*a1)/(5f*255f);
                            ag[6]=0;
                            ag[7]=1;
                        }
                        ulong aBits=0;
                        for(int k=0;k<6;k++)aBits|=(ulong)dds[p+2+k]<<(k*8);
                        ushort c0=BinaryPrimitives.ReadUInt16LittleEndian(dds[(p+8)..]);
                        ushort c1=BinaryPrimitives.ReadUInt16LittleEndian(dds[(p+10)..]);
                        float g0=((c0>>5)&63)/63f, g1=((c1>>5)&63)/63f;
                        gp[0]=g0;gp[1]=g1;
                        gp[2]=(2*g0+g1)/3f;
                        gp[3]=(g0+2*g1)/3f;
                        uint cBits=BinaryPrimitives.ReadUInt32LittleEndian(dds[(p+12)..]);
                        for(int y=0;y<4;y++)
                            for(int x=0;x<4;x++)
                            {
                                if(by*4+y>=h||bx*4+x>=w)continue;
                                int at=y*4+x;
                                int aIndex=(int)((aBits>>(at*3))&7);
                                int gIndex=(int)((cBits>>(at*2))&3);
                                green+=gp[gIndex];
                                alpha+=ag[aIndex];
                                pixels++;
                            }
                    }
                if(pixels==0)return false;
                meanGreen=(float)(green/pixels);
                meanAlpha=(float)(alpha/pixels);
                return float.IsFinite(meanGreen)&&float.IsFinite(meanAlpha);
            }
            ptr+=checked((int)size);
            w=Math.Max(1,w/2);
            h=Math.Max(1,h/2);
        }
        return false;
    }

    internal static bool LooksPackedNormal(float meanGreen,float meanAlpha)=>
        meanGreen is >=0.28f and <=0.73f &&
        meanAlpha is >=0.28f and <=0.73f;

    internal static int Inspect(string[] args)
    {
        if(args.Length<3)
        {
            Console.Error.WriteLine("--inspect-original-packed-normal <manifest> <oodle> <DDS-IDs...>");
            return 2;
        }
        using var archive=new EsoTest.EsoArchive(args[0]);
        using var oodle=EsoTest.OodleDecoder.FromEnvironment(args[1])??
            throw new System.IO.FileNotFoundException("ESO Oodle decoder missing");
        for(int i=2;i<args.Length;i++)
        {
            uint id=uint.Parse(args[i],System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture);
            var record=archive.FindId(id).FirstOrDefault(r=>
                archive.GetIdIndexFields(r.Ordinal).Flags==0);
            if(record.UncompressedSize<128||record.UncompressedSize>8_000_000)
            {
                Console.WriteLine($"{id:X8} invalid source DDS");
                continue;
            }
            var data=archive.ReadDecoded(record,oodle);
            bool sampled=TrySample(data,out float g,out float a);
            Console.WriteLine($"{id:X8} DXT5-sampled={sampled} mean-green={g:F4} "+
                $"mean-alpha={a:F4} packed-normal-candidate={sampled&&LooksPackedNormal(g,a)}");
        }
        return 0;
    }
}
