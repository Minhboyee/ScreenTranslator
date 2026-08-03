using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics.Capture;
using WinRT;

namespace Translator.Windows;

public static partial class GraphicsCaptureItemFactory
{
    private static readonly Guid GraphicsCaptureItemInteropIid =
        new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid GraphicsCaptureItemIid =
        new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CreateForWindow(nint windowHandle)
    {
        if (windowHandle == 0)
        {
            throw new ArgumentException("A window handle is required.", nameof(windowHandle));
        }

        var topLevelWindowHandle = NativeMethods.GetAncestor(windowHandle, NativeMethods.GaRoot);
        if (topLevelWindowHandle == 0 || !NativeMethods.IsWindowVisible(topLevelWindowHandle))
        {
            throw new ArgumentException(
                "A visible top-level window handle is required.",
                nameof(windowHandle));
        }

        windowHandle = topLevelWindowHandle;
        using var activationFactory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interopInterfaceId = GraphicsCaptureItemInteropIid;
        nint? interopPointer = null;
        var interopAbi = Marshal.QueryInterface(
            activationFactory.ThisPtr,
            ref interopInterfaceId,
            out var queriedInteropPointer);
        if (interopAbi < 0)
        {
            ThrowFactoryHResult("QueryInterface", interopAbi);
        }

        interopPointer = queriedInteropPointer;
        try
        {
            unsafe
            {
                var interop = ComInterfaceMarshaller<IGraphicsCaptureItemInterop>
                    .ConvertToManaged((void*)interopPointer.Value);
                interopPointer = 0;

                var interfaceId = GraphicsCaptureItemIid;
                nint? itemAbi = null;
                try
                {
                    var createHResult = interop.CreateForWindow(windowHandle, ref interfaceId, out var createdItemAbi);
                    itemAbi = createdItemAbi;
                    if (createHResult < 0)
                    {
                        ThrowFactoryHResult("CreateForWindow", createHResult);
                    }

                    if (itemAbi == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("The window could not be converted to a graphics capture item.");
                    }

                    var item = MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemAbi.Value);
                    itemAbi = 0;
                    return item;
                }
                finally
                {
                    if (itemAbi is { } itemPointer && itemPointer != 0)
                    {
                        Marshal.Release(itemPointer);
                    }
                }
            }
        }
        finally
        {
            if (interopPointer is { } pointer && pointer != 0)
            {
                Marshal.Release(pointer);
            }
        }
    }

    private static void ThrowFactoryHResult(string operation, int hresult)
    {
        throw new GraphicsCaptureItemFactoryException(operation, hresult);
    }

    [GeneratedComInterface]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    internal partial interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(nint windowHandle, ref Guid interfaceId, out nint itemAbi);

        [PreserveSig]
        int CreateForMonitor(nint monitorHandle, ref Guid interfaceId, out nint itemAbi);
    }
}

internal sealed class GraphicsCaptureItemFactoryException : COMException
{
    internal GraphicsCaptureItemFactoryException(string operation, int hresult)
        : base(
            $"GraphicsCaptureItemFactory.{operation} failed with HRESULT 0x{unchecked((uint)hresult):X8}.",
            hresult)
    {
        Operation = operation;
    }

    internal string Operation { get; }
}
