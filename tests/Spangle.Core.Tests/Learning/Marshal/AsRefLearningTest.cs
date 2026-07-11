using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spangle.Tests.Learning.Marshal;

/*
 * Documents the MemoryMarshal.AsRef semantics every wire-struct overlay in this
 * codebase relies on (BufferMarshal, the TS/RTMP struct mappings):
 * - AsRef interprets a byte sequence as a struct without copying original bytes.
 * - `ref` is required both on the AsRef call and the caller's variable
 *   (ref var a = ref AsRef(...)) — otherwise the variable is a COPY.
 * - A struct without Pack=1/Size grows by alignment padding, and AsRef rejects
 *   a span shorter than that padded size — the reason all wire structs here
 *   pin their layout explicitly.
 * See also: https://github.com/dotnet/corefx/pull/31236
 */
public class MarshalLearningTest
{
    [Fact]
    public void TestAsRefSpan()
    {
        var bytes = new byte[] { 1, 2, 0, 3, 0, 0, 0 };
        var span = bytes.AsSpan(..bytes.Length);
        ref var mapped = ref MemoryMarshal.AsRef<BytePackedStruct>(span);
        mapped.V1.Should().Be(1);
        mapped.V2.Should().Be(2);
        mapped.V3.Should().Be(3);

        bytes[0] = 0;
        span[0].Should().Be(0);
        mapped.V1.Should().Be(0, "the struct is not the copy but only interpreting original byte sequence.");

        Unsafe.AreSame(ref Unsafe.As<BytePackedStruct, byte>(ref mapped), ref MemoryMarshal.GetReference(span))
            .Should().BeTrue("Same address");
    }

    [Fact]
    public void TestAsRefHostAlignSpan()
    {
        var bytes = new byte[] { 1, 2, 0, 3, 0, 0, 0 };
        var exception = Record.Exception(() =>
        {
            MemoryMarshal.AsRef<HostAlignStruct>(bytes.AsSpan(..bytes.Length));
        });
        exception.Should().NotBeNull();
        exception.Should().BeOfType<ArgumentOutOfRangeException>("byte sequence does not match struct layout");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1, Size = 5)]
    [SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Local")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Local")]
    private struct BytePackedStruct
    {
        public byte  V1;
        public short V2;
        public int   V3;
    }

    [SuppressMessage("ReSharper", "FieldCanBeMadeReadOnly.Local")]
    [SuppressMessage("ReSharper", "MemberCanBePrivate.Local")]
    [StructLayout(LayoutKind.Sequential)]
    private struct HostAlignStruct
    {
        public byte  V1;
        public short V2;
        public int   V3;
    }
}
