using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Spangle.Tests.Learning.Marshal;

/*
 * - AsRef interprets byte sequence as a struct without copying original bytes.
 * - `ref` keyword is necessary on calling AsRef and declaring variable in caller. (ref var a = ref AsRef(...))
 *   - Otherwise, the variable in caller is the COPY of the struct interpreted.
 * See also: https://github.com/dotnet/corefx/pull/31236
 */
public class MarshalLearningTest
{
    [Fact]
    public unsafe void TestAsRefSpan()
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

        fixed (void* p = &mapped)
        {
            Unsafe.AreSame(ref Unsafe.AsRef<byte>(p), ref MemoryMarshal.GetReference(span))
                .Should().BeTrue("Same address");
        }
    }
    
    [Fact]
    public unsafe void TestAsRefPointer()
    {
        var bytes = new byte[] { 1, 2, 0, 3, 0, 0, 0 };
        fixed (byte* p = bytes)
        {
            ref var mapped = ref Unsafe.AsRef<BytePackedStruct>(p);
            mapped.V1.Should().Be(1);
            mapped.V2.Should().Be(2);
            mapped.V3.Should().Be(3);
            p[1].Should().Be(2);

            bytes[0] = 0;
            mapped.V1.Should().Be(0);
        }
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
    
    [Fact]
    public unsafe void TestAsRefHostAlignPointer()
    {
        var bytes = new byte[] { 1, 2, 0, 3, 0, 0, 0 };
        fixed (byte* p = bytes)
        {
            // This way is REALLY UNSAFE!!
            // var mapped = Unsafe.AsRef<HostAlignStruct>(p);
            // mapped.V1.Should().Be(1);
            // mapped.V2.Should().NotBe(2);
            // mapped.V3.Should().NotBe(3);

            try
            {
                // Cannot use Assert.Throws or Record.Exception with pointer and byref-type span
                MemoryMarshal.AsRef<HostAlignStruct>(new Span<byte>(p, bytes.Length));
            }
            catch (Exception exception)
            {
                exception.Should().BeOfType<ArgumentOutOfRangeException>();
                return;
            }
            
            Assert.Fail("Should throws exception");
        }
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
