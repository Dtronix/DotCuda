using System.Reflection.PortableExecutable;
using System.Text;

namespace DotCuda.Generator;

/// <summary>Reads a PE file's export table: the names a [LibraryImport] EntryPoint can bind to.</summary>
static class ExportCheck
{
    public static HashSet<string> ReadExports(string dll)
    {
        using var stream = File.OpenRead(dll);
        using var pe = new PEReader(stream);
        var dir = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (dir.Size == 0) return names;

        var image = pe.GetEntireImage().GetContent().ToArray();
        int Offset(int rva)
        {
            foreach (var s in pe.PEHeaders.SectionHeaders)
                if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData))
                    return rva - s.VirtualAddress + s.PointerToRawData;
            throw new InvalidDataException($"RVA 0x{rva:X} is outside every section of {dir}");
        }
        int ReadInt(int offset) => BitConverter.ToInt32(image.AsSpan(offset, 4));

        var exp = Offset(dir.RelativeVirtualAddress);
        int count = ReadInt(exp + 24);      // IMAGE_EXPORT_DIRECTORY.NumberOfNames
        int namesRva = ReadInt(exp + 32);   // AddressOfNames
        var namesOffset = Offset(namesRva);
        for (int i = 0; i < count; i++)
        {
            var at = Offset(ReadInt(namesOffset + 4 * i));
            int end = at;
            while (image[end] != 0) end++;
            names.Add(Encoding.ASCII.GetString(image, at, end - at));
        }
        return names;
    }

    /// <summary>Entry points missing from the DLL.</summary>
    public static List<string> Missing(LibraryModel model, string dll)
    {
        var exports = ReadExports(dll);
        return model.Emitted<FunctionItem>().Select(f => f.EntryPoint).Where(e => !exports.Contains(e)).Distinct().ToList();
    }
}
