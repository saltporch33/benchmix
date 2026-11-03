using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

// What a published single-file exe carries, unpacked the way the .NET host finds it: the bundle
// header's offset sits just before a fixed signature in the apphost, and each file in the bundle
// is stored as is or deflated. verify-exe.ps1 uses it to fail an exe that would carry this
// machine's user folder: up to v2026.10.29.7 every release did, in its assemblies' PDB paths, and
// the release exes go public (2026-11-02). Written for Add-Type in Windows PowerShell, so C# 5.
public static class BundleScan
{
    static readonly byte[] Signature = {
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae };

    /// <summary>The files in the bundle ("apphost" for the exe's own code) whose bytes hold
    /// <paramref name="needle"/>, as UTF-8 or UTF-16, ignoring ASCII case.</summary>
    public static List<string> FilesHolding(string exe, string needle)
    {
        byte[] data = File.ReadAllBytes(exe);
        int sig = IndexOf(data, Signature);
        if (sig < 8) throw new InvalidDataException("not a single-file bundle: " + exe);
        long header = BitConverter.ToInt64(data, sig - 8);

        string lower = needle.ToLowerInvariant();
        byte[][] needles = { Encoding.UTF8.GetBytes(lower), Encoding.Unicode.GetBytes(lower) };
        var found = new List<string>();
        using (var r = new BinaryReader(new MemoryStream(data)))
        {
            r.BaseStream.Position = header;
            uint major = r.ReadUInt32();
            r.ReadUInt32();                                     // minor version
            int count = r.ReadInt32();
            r.ReadString();                                     // bundle id
            if (major >= 2) r.BaseStream.Position += 5 * 8;     // deps.json, runtimeconfig.json, flags
            long firstFile = data.Length;
            for (int i = 0; i < count; i++)
            {
                long offset = r.ReadInt64();
                long size = r.ReadInt64();
                long compressed = major >= 6 ? r.ReadInt64() : 0;
                r.ReadByte();                                   // file type
                string path = r.ReadString();
                firstFile = Math.Min(firstFile, offset);
                byte[] content = Slice(data, offset, compressed != 0 ? compressed : size);
                if (compressed != 0) content = Inflate(content, size);
                if (Holds(content, needles)) found.Add(path);
            }
            if (Holds(Slice(data, 0, firstFile), needles)) found.Insert(0, "apphost");
        }
        return found;
    }

    static bool Holds(byte[] content, byte[][] needles)
    {
        for (int i = 0; i < content.Length; i++)
            if (content[i] >= (byte)'A' && content[i] <= (byte)'Z') content[i] += 32;
        foreach (byte[] n in needles)
            if (IndexOf(content, n) >= 0) return true;
        return false;
    }

    static int IndexOf(byte[] hay, byte[] needle)
    {
        int last = hay.Length - needle.Length;
        for (int i = 0; i <= last; i++)
        {
            if (hay[i] != needle[0]) continue;
            int j = 1;
            while (j < needle.Length && hay[i + j] == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }

    static byte[] Slice(byte[] data, long offset, long length)
    {
        var s = new byte[length];
        Array.Copy(data, offset, s, 0, length);
        return s;
    }

    static byte[] Inflate(byte[] raw, long size)
    {
        var output = new byte[size];
        using (var d = new DeflateStream(new MemoryStream(raw), CompressionMode.Decompress))
        {
            int read = 0;
            while (read < size)
            {
                int n = d.Read(output, read, (int)(size - read));
                if (n == 0) break;
                read += n;
            }
        }
        return output;
    }
}
