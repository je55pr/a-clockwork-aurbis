using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using EsoTest;

/// <summary>
/// Extract only explicitly requested original DDS records to a LOCAL ignored
/// research folder for read-only verification. Never ships game textures.
/// </summary>
internal static class EsoLocalDdsCandidate
{
    public static int Run(string[] args)
    {
        if(args.Length<4)
        {
            Console.Error.WriteLine(
                "Usage: --extract-original-dds <manifest> <oodle.dll> <hex-id> <local ignored .dds path>");
            return 2;
        }
        string destination=Path.GetFullPath(args[3]);
        string research=Path.GetFullPath(Path.Combine(Environment.CurrentDirectory,".research"));
        string researchPrefix=Path.TrimEndingDirectorySeparator(research)+Path.DirectorySeparatorChar;
        if(!destination.EndsWith(".dds",StringComparison.OrdinalIgnoreCase)||
           !destination.StartsWith(researchPrefix,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Original proprietary DDS may only be inspected under the repository's ignored .research directory");
        uint id=uint.Parse(args[2],NumberStyles.HexNumber,CultureInfo.InvariantCulture);
        using var archive=new EsoArchive(args[0]);
        using var oodle=OodleDecoder.FromEnvironment(args[1])
            ??throw new FileNotFoundException("Local Oodle source decoder unavailable");
        var record=archive.FindId(id).FirstOrDefault(x=>
            archive.GetIdIndexFields(x.Ordinal).Flags==0);
        if(record.UncompressedSize is <128 or >16_000_000)
            throw new InvalidDataException("Original DDS record absent or unexpectedly sized");
        var data=archive.ReadDecoded(record,oodle);
        if(data.Length<128||Encoding.ASCII.GetString(data,0,4)!="DDS ")
            throw new InvalidDataException("Source resource does not contain actual DDS");
        int height=BitConverter.ToInt32(data,12);
        int width=BitConverter.ToInt32(data,16);
        if(width is <4 or >8192||height is <4 or >8192)
            throw new InvalidDataException("Original DDS dimensions invalid");
        string fourcc=Encoding.ASCII.GetString(data,84,4);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllBytes(destination,data);
        Console.WriteLine($"Original ESO local DDS 0x{id:X8}: "+
            $"{width}x{height}, FOURCC '{fourcc}', {data.Length} bytes, "+
            $"archive={record.ArchiveIndex}. Ignored local research file: {destination}");
        return 0;
    }
}
