using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace Dalamud.Test.Interface;

/// <summary>Creates an isolated Windows x64 DLL with controlled exports and optional version metadata.</summary>
/// <remarks>
/// The cached handle and address are borrowed and remain valid only while a loader reference exists.
/// Release separately acquired module references before disposing the fixture.
/// </remarks>
internal sealed class NativeModuleFixture : IDisposable
{
    private readonly string directory;
    private nint originalReference;

    public NativeModuleFixture(IEnumerable<string> exportNames, string? description, string? directory = null)
    {
        // The loader requires the export name table to be sorted.
        var names = exportNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        if (names.Length == 0)
            throw new ArgumentException("At least one export is required.", nameof(exportNames));

        var fixtureDirectory = Path.Combine(directory ?? Path.GetTempPath(), "Dalamud-module-test-" + Guid.NewGuid().ToString("N"));
        this.directory = fixtureDirectory.Length >= 240 && !fixtureDirectory.StartsWith(@"\\?\", StringComparison.Ordinal)
                             ? @"\\?\" + Path.GetFullPath(fixtureDirectory)
                             : fixtureDirectory;
        Directory.CreateDirectory(this.directory);
        this.FileName = Path.Combine(this.directory, "module-proxy.dll");
        try
        {
            File.WriteAllBytes(this.FileName, CreateImage(names, description));
            this.originalReference = NativeLibrary.Load(this.FileName);
            this.Handle = this.originalReference;
            this.Address = NativeLibrary.GetExport(this.Handle, names[0]);
        }
        catch
        {
            this.Dispose();
            throw;
        }
    }

    public string FileName { get; }

    public nint Address { get; }

    public nint Handle { get; }

    public bool IsLoaded => GetModuleHandleW(this.FileName) != 0;

    /// <summary>Releases the fixture's loader reference so tests can observe independently retained references.</summary>
    public void ReleaseOriginalReference()
    {
        if (this.originalReference == 0)
            return;

        NativeLibrary.Free(this.originalReference);
        this.originalReference = 0;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.ReleaseOriginalReference();
        if (Directory.Exists(this.directory))
            Directory.Delete(this.directory, true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint GetModuleHandleW(string name);

    private static byte[] CreateImage(string[] names, string? description)
    {
        var exports = CreateExports(names);
        // A zeroed resource directory represents a module without version metadata.
        var resources = description is null ? new byte[16] : CreateResources(description);
        const int textOffset = 0x400;
        var exportsOffset = textOffset + Align(names.Length * 16, 0x200);
        var resourcesOffset = exportsOffset + Align(exports.Length, 0x200);
        var relocationsOffset = resourcesOffset + Align(resources.Length, 0x200);
        var image = new byte[relocationsOffset + 0x200];
        using var stream = new MemoryStream(image);
        using var writer = new BinaryWriter(stream);

        // Write the DOS and AMD64 COFF headers, with the PE signature at offset 0x80.
        writer.Write((ushort)0x5A4D);
        stream.Position = 0x3C;
        writer.Write(0x80);
        stream.Position = 0x80;
        writer.Write(0x00004550);
        writer.Write((ushort)0x8664); // AMD64
        writer.Write((ushort)4);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write((ushort)240);
        writer.Write((ushort)0x2022); // executable, large-address-aware DLL

        // The PE32+ optional header uses 4 KiB section alignment and 512-byte file alignment.
        writer.Write((ushort)0x20B);
        writer.Write((ushort)0);
        writer.Write(Align(names.Length * 16, 0x200));
        writer.Write(image.Length - exportsOffset);
        writer.Write(0);
        writer.Write(0); // no DllMain
        writer.Write(0x1000);
        writer.Write(0x0000000180000000UL);
        writer.Write(0x1000);
        writer.Write(0x200);
        writer.Write((ushort)6);
        writer.Write((ushort)0);
        writer.Write(0);
        writer.Write((ushort)6);
        writer.Write((ushort)0);
        writer.Write(0);
        writer.Write(0x5000);
        writer.Write(0x400);
        writer.Write(0);
        writer.Write((ushort)2);
        writer.Write((ushort)0x160); // ASLR, high entropy, NX
        writer.Write(0x100000UL);
        writer.Write(0x1000UL);
        writer.Write(0x100000UL);
        writer.Write(0x1000UL);
        writer.Write(0);
        writer.Write(16);

        // Populate only the export, resource, and base-relocation data directories.
        for (var i = 0; i < 16; i++)
        {
            writer.Write(i switch { 0 => 0x2000, 2 when resources.Length > 0 => 0x3000, 5 => 0x4000, _ => 0 });
            writer.Write(i switch { 0 => exports.Length, 2 => resources.Length, 5 => 12, _ => 0 });
        }

        WriteSection(writer, ".text", 0x1000, names.Length * 16, textOffset, 0x60000020);
        WriteSection(writer, ".edata", 0x2000, exports.Length, exportsOffset, 0x40000040);
        WriteSection(writer, ".rsrc", 0x3000, resources.Length, resourcesOffset, 0x40000040);
        WriteSection(writer, ".reloc", 0x4000, 12, relocationsOffset, 0x42000040);
        for (var i = 0; i < names.Length; i++)
            image[textOffset + (16 * i)] = 0xC3; // ret; exports are inspected, never called
        exports.CopyTo(image, exportsOffset);
        resources.CopyTo(image, resourcesOffset);

        // ABSOLUTE relocations let the loader rebase this image without any pointer fixups.
        stream.Position = relocationsOffset;
        writer.Write(0x1000);
        writer.Write(12);
        writer.Write(0);
        return image;
    }

    private static void WriteSection(BinaryWriter writer, string name, int rva, int length, int offset, uint flags)
    {
        var nameBytes = new byte[8];
        Encoding.ASCII.GetBytes(name).CopyTo(nameBytes, 0);
        writer.Write(nameBytes);
        writer.Write(length);
        writer.Write(rva);
        writer.Write(Align(length, 0x200));
        writer.Write(offset);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(flags);
    }

    private static byte[] CreateExports(string[] names)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Export tables use RVAs into .text and .edata; name ordinals index the function table.
        var functionTable = 40;
        var nameTable = functionTable + (4 * names.Length);
        var ordinalTable = nameTable + (4 * names.Length);
        var strings = ordinalTable + (2 * names.Length);
        writer.Write(new byte[12]);
        writer.Write(0x2000 + strings);
        writer.Write(1);
        writer.Write(names.Length);
        writer.Write(names.Length);
        writer.Write(0x2000 + functionTable);
        writer.Write(0x2000 + nameTable);
        writer.Write(0x2000 + ordinalTable);
        for (var i = 0; i < names.Length; i++)
            writer.Write(0x1000 + (16 * i));
        var stringOffset = strings + "dxgi.dll".Length + 1;
        foreach (var name in names)
        {
            writer.Write(0x2000 + stringOffset);
            stringOffset += name.Length + 1;
        }

        for (var i = 0; i < names.Length; i++)
            writer.Write((ushort)i);
        writer.Write(Encoding.ASCII.GetBytes("dxgi.dll\0"));
        foreach (var name in names)
            writer.Write(Encoding.ASCII.GetBytes(name + '\0'));
        return stream.ToArray();
    }

    private static byte[] CreateResources(string description)
    {
        // Keep the English/Unicode string table consistent with its translation entry.
        var text = Encoding.Unicode.GetBytes(description + '\0');
        var descriptionBlock = VersionBlock("FileDescription", (ushort)(description.Length + 1), 1, text);
        var fileVersion = VersionBlock("FileVersion", 8, 1, Encoding.Unicode.GetBytes("1.0.0.0\0"));
        var table = VersionBlock("040904B0", 0, 1, Array.Empty<byte>(), descriptionBlock, fileVersion);
        var strings = VersionBlock("StringFileInfo", 0, 1, Array.Empty<byte>(), table);
        var translation = VersionBlock("Translation", 4, 0, new byte[] { 0x09, 0x04, 0xB0, 0x04 });
        var variables = VersionBlock("VarFileInfo", 0, 1, Array.Empty<byte>(), translation);
        using var fixedStream = new MemoryStream();
        using (var fixedWriter = new BinaryWriter(fixedStream, Encoding.Unicode, true))
        {
            // VS_FIXEDFILEINFO identifies a Windows DLL with version 1.0.0.0.
            foreach (var value in new uint[] { 0xFEEF04BD, 0x10000, 0x10000, 0, 0x10000, 0, 0x3F, 0, 0x40004, 2, 0, 0, 0 })
                fixedWriter.Write(value);
        }

        var version = VersionBlock("VS_VERSION_INFO", 52, 0, fixedStream.ToArray(), strings, variables);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // Resource directories descend through RT_VERSION, resource ID 1, and language 0x0409.
        foreach (var (id, next) in new[] { (16, unchecked((int)0x80000018)), (1, unchecked((int)0x80000030)), (0x409, 72) })
        {
            writer.Write(new byte[14]);
            writer.Write((ushort)1);
            writer.Write(id);
            writer.Write(next);
        }

        // The leaf data entry points past the directory tree into the version payload.
        writer.Write(0x3000 + 88);
        writer.Write(version.Length);
        writer.Write(1200);
        writer.Write(0);
        writer.Write(version);
        return stream.ToArray();
    }

    private static byte[] VersionBlock(string key, ushort valueLength, ushort type, byte[] value, params byte[][] children)
    {
        // Version-resource values and child blocks start on DWORD boundaries.
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write(valueLength);
        writer.Write(type);
        writer.Write(Encoding.Unicode.GetBytes(key + '\0'));
        Pad();
        writer.Write(value);
        foreach (var child in children)
        {
            Pad();
            writer.Write(child);
        }

        var bytes = stream.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(), checked((ushort)bytes.Length));
        return bytes;

        void Pad()
        {
            while (stream.Position % 4 != 0)
                writer.Write((byte)0);
        }
    }

    private static int Align(int size, int alignment) => (size + alignment - 1) & -alignment;
}
