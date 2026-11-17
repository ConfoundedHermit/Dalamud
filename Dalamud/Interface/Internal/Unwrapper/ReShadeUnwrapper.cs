using System.Runtime.InteropServices;

using Dalamud.Support;

namespace Dalamud.Interface.Internal.Unwrapper;

/// <summary>Recognizes COM wrappers implemented by a module that exports the ReShade addon API.</summary>
internal unsafe class ReShadeUnwrapper : ComHookUnwrapper
{
    /// <summary>Checks whether a function's module exports the ReShade addon API.</summary>
    /// <remarks>Recognition does not require a particular filename or version metadata.</remarks>
    /// <param name="ptr">A function address.</param>
    /// <returns>Whether the owner exports all four addon entry points.</returns>
    internal static bool BelongsInReShadeDll(nint ptr)
    {
        using var module = ModuleReference.FromAddress(ptr);
        return module is not null &&
               module.HasExport("ReShadeRegisterAddon"u8) &&
               module.HasExport("ReShadeUnregisterAddon"u8) &&
               module.HasExport("ReShadeRegisterEvent"u8) &&
               module.HasExport("ReShadeUnregisterEvent"u8);
    }

    /// <inheritdoc/>
    protected override bool IsRelevantComObject<T>(T* obj)
    {
        if (!IsValidReadableMemoryAddress((nint)obj, sizeof(nint)))
            return false;

        try
        {
            var vtbl = (nint**)Marshal.ReadIntPtr((nint)obj);
            if (!IsValidReadableMemoryAddress((nint)vtbl, sizeof(nint) * 3))
                return false;

            for (var i = 0; i < 3; i++)
            {
                var pfn = Marshal.ReadIntPtr((nint)(vtbl + i));
                if (!IsValidExecutableMemoryAddress(pfn, 1))
                    return false;
                if (!BelongsInReShadeDll(pfn))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
