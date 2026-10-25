using System.Reflection.Metadata;
using System.Runtime.InteropServices;
using Passenger.Debugger.Interop;

namespace Passenger.Debugger;

public partial class ManagedDebugger
{
    private static int GetDebuggerBrowsableCustomAttributeResultInt(nint debuggerBrowsableCustomAttributePointer, uint debuggerBrowsableCustomAttributeSize)
    {
        byte[] byteArray = new byte[debuggerBrowsableCustomAttributeSize];
        Marshal.Copy(debuggerBrowsableCustomAttributePointer, byteArray, 0, byteArray.Length);
        // 2 bytes of prolog, 4 of data, 2 of alignment.
        Span<byte> byteSpan = byteArray.AsSpan()[2..^2];
        int dataAsInt = BitConverter.ToInt32(byteSpan);
        return dataAsInt;
    }

    private static string GetCustomAttributeResultString(nint customAttributePointer, uint customAttributeSize)
    {
        string? dataAsString = GetCustomAttributeCtorStringArg(customAttributePointer, checked((int)customAttributeSize)); // e.g. "Count = {Count}" or "{DebuggerDisplay,nq}"
        return dataAsString ?? string.Empty;
    }

    private static unsafe string? GetCustomAttributeCtorStringArg(IntPtr ppData, int pcbData)
    {
        BlobReader reader = new BlobReader((byte*)ppData, pcbData);

        // 1. Prolog (must be 0x0001)
        ushort prolog = reader.ReadUInt16();
        if (prolog != 0x0001)
        {
            throw new InvalidOperationException("Invalid custom attribute prolog");
        }

        // 2. The constructor's one fixed argument, DebuggerDisplay's string.
        string? stringCtorArg = reader.ReadSerializedString();
        return stringCtorArg;
    }
}
