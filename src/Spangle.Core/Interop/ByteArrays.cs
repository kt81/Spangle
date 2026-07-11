using System.Runtime.CompilerServices;

namespace Spangle.Interop;

// The BCL provides InlineArray2..16<byte> only (nothing at 1, nothing above 16);
// wire sizes outside that range get their own inline array types here.

/// <summary>One byte as an inline array (single-byte wire headers).</summary>
[InlineArray(1)]
internal struct ByteArray1
{
    private byte _element0;
}

/// <summary>A 188-byte MPEG-2 transport stream packet buffer.</summary>
[InlineArray(188)]
internal struct ByteArray188
{
    private byte _element0;
}
