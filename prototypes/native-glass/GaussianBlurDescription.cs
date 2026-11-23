using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Effects;
using WinRT;

namespace Xdc.NativeGlass;

// Describes the built-in Direct2D Gaussian blur. Windows Composition renders it.
[WinRTExposedType(typeof(GaussianBlurInterfaces))]
internal sealed class GaussianBlurDescription(IGraphicsEffectSource source, float blurAmount)
    : IGraphicsEffect, IGraphicsEffectSource
{
    public string Name { get; set; } = "Blur";
    internal IGraphicsEffectSource Source { get; } = source;
    internal float BlurAmount { get; } = blurAmount;
}

internal sealed unsafe class GaussianBlurInterfaces : IWinRTExposedTypeDetails
{
    private const int InvalidArgument = unchecked((int)0x80070057);
    private static readonly Guid EffectInteropId = new("2FC57384-A068-44D7-A331-30982FCF7177");
    private static readonly Guid GaussianBlurId = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");
    private static readonly Guid PropertyValueId = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
    private static readonly nint EffectVtable = CreateEffectVtable();
    private static readonly nint InteropVtable = CreateInteropVtable();

    public ComWrappers.ComInterfaceEntry[] GetExposedInterfaces() =>
    [
        new() { IID = typeof(IGraphicsEffect).GUID, Vtable = EffectVtable },
        new() { IID = typeof(IGraphicsEffectSource).GUID, Vtable = IInspectable.Vftbl.AbiToProjectionVftablePtr },
        new() { IID = EffectInteropId, Vtable = InteropVtable },
    ];

    private static nint CreateEffectVtable()
    {
        var vtable = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(GaussianBlurInterfaces), 8 * sizeof(nint));
        *(IInspectable.Vftbl*)vtable = IInspectable.Vftbl.AbiToProjectionVftable;
        vtable[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&GetName;
        vtable[7] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, int>)&SetName;
        return (nint)vtable;
    }

    private static nint CreateInteropVtable()
    {
        var vtable = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(GaussianBlurInterfaces), 9 * sizeof(nint));
        *(WinRT.Interop.IUnknownVftbl*)vtable = WinRT.Interop.IUnknownVftbl.AbiToProjectionVftbl;
        // IGraphicsEffectD2D1Interop follows the method order of the Windows SDK header.
        vtable[3] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, int>)&GetEffectId;
        vtable[4] = (nint)(delegate* unmanaged[Stdcall]<nint, char*, uint*, uint*, int>)&GetNamedPropertyMapping;
        vtable[5] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GetPropertyCount;
        vtable[6] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&GetProperty;
        vtable[7] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&GetSource;
        vtable[8] = (nint)(delegate* unmanaged[Stdcall]<nint, uint*, int>)&GetSourceCount;
        return (nint)vtable;
    }

    private static GaussianBlurDescription Description(nint instance) =>
        ComWrappers.ComInterfaceDispatch.GetInstance<GaussianBlurDescription>((ComWrappers.ComInterfaceDispatch*)instance);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetName(nint instance, nint* value)
    {
        *value = 0;
        try
        {
            *value = MarshalString.FromManaged(Description(instance).Name);
            return 0;
        }
        catch (Exception error) { return error.HResult; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetName(nint instance, nint value)
    {
        try
        {
            Description(instance).Name = MarshalString.FromAbi(value);
            return 0;
        }
        catch (Exception error) { return error.HResult; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetEffectId(nint instance, Guid* value)
    {
        *value = GaussianBlurId;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetNamedPropertyMapping(nint instance, char* name, uint* index, uint* mapping)
    {
        *index = 0;
        *mapping = 1; // GRAPHICS_EFFECT_PROPERTY_MAPPING_DIRECT
        if (name == null) return InvalidArgument;
        ReadOnlySpan<char> property = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(name);
        if (property.SequenceEqual("BlurAmount")) return 0;
        if (property.SequenceEqual("Optimization"))
        {
            *index = 1;
            return 0;
        }
        if (property.SequenceEqual("BorderMode"))
        {
            *index = 2;
            return 0;
        }
        return InvalidArgument;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPropertyCount(nint instance, uint* value)
    {
        *value = 3;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetProperty(nint instance, uint index, nint* value)
    {
        *value = 0;
        if (index > 2) return InvalidArgument;
        try
        {
            object property = index == 0
                ? PropertyValue.CreateSingle(Description(instance).BlurAmount)
                : PropertyValue.CreateUInt32(1); // BALANCED optimization, HARD border.
            nint inspectable = MarshalInspectable<object>.FromManaged(property);
            try
            {
                Guid iid = PropertyValueId;
                return Marshal.QueryInterface(inspectable, in iid, out *value);
            }
            finally
            {
                Marshal.Release(inspectable);
            }
        }
        catch (Exception error) { return error.HResult; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetSource(nint instance, uint index, nint* value)
    {
        *value = 0;
        if (index != 0) return InvalidArgument;
        try
        {
            *value = MarshalInterface<IGraphicsEffectSource>.FromManaged(Description(instance).Source);
            return 0;
        }
        catch (Exception error) { return error.HResult; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetSourceCount(nint instance, uint* value)
    {
        *value = 1;
        return 0;
    }
}
