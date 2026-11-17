using Microsoft.Win32.SafeHandles;

using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace Dalamud.Support;

/// <summary>Owns a loader reference to the module containing an address.</summary>
/// <remarks>Native queries defer release of the loader reference until they return.</remarks>
internal sealed unsafe class ModuleReference : SafeHandleZeroOrMinusOneIsInvalid
{
    private ModuleReference()
        : base(true)
    {
    }

    /// <summary>Acquires a loader reference to the module currently containing an address.</summary>
    /// <param name="address">An address inside a loaded module.</param>
    /// <returns>A reference the caller must dispose, or <c>null</c> if acquisition fails or the address is zero.</returns>
    public static ModuleReference? FromAddress(nint address)
    {
        if (address == 0)
            return null;

        // Allocate the owner first so allocation failure cannot leak a loader reference.
        var reference = new ModuleReference();
        HMODULE module;
        if (!GetModuleHandleExW(GET.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, (char*)address, &module))
        {
            reference.Dispose();
            return null;
        }

        reference.SetHandle((nint)module);
        return reference;
    }

    /// <summary>Checks whether the module exports a symbol.</summary>
    /// <param name="name">The export name; its backing storage must include a trailing null byte.</param>
    /// <returns>Whether the module exports the name.</returns>
    public bool HasExport(ReadOnlySpan<byte> name)
    {
        var retained = false;
        try
        {
            this.DangerousAddRef(ref retained);
            fixed (byte* p = name)
                return GetProcAddress((HMODULE)this.handle, (sbyte*)p) != null;
        }
        finally
        {
            if (retained)
                this.DangerousRelease();
        }
    }

    /// <summary>Retrieves the complete module path recorded by the loader.</summary>
    /// <returns>The path, or <c>null</c> on failure or truncation at the path limit.</returns>
    public string? GetFileName()
    {
        const int maxCapacity = 32768;
        var retained = false;
        try
        {
            this.DangerousAddRef(ref retained);
            Span<char> path = stackalloc char[260];
            while (true)
            {
                uint length;
                fixed (char* p = path)
                    length = GetModuleFileNameW((HMODULE)this.handle, p, (uint)path.Length);

                if (length == 0)
                    return null;
                if (length < path.Length)
                    return new string(path[..(int)length]);
                if (path.Length >= maxCapacity)
                    return null;

                path = new char[Math.Min(path.Length * 2, maxCapacity)];
            }
        }
        finally
        {
            if (retained)
                this.DangerousRelease();
        }
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle() => FreeLibrary((HMODULE)this.handle);
}
