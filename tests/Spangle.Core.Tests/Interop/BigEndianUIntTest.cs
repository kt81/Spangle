using System.Runtime.InteropServices;
using Spangle.IO.Interop;
using Xunit.Abstractions;
// ReSharper disable UnusedType.Global

namespace Spangle.Tests.Interop;

public class BigEndianUInt24Test : BigEndianUIntTest<BigEndianUInt24>
{
    public BigEndianUInt24Test(ITestOutputHelper outputHelper) : base(outputHelper)
    {
    }

    protected override int TypeSize => 3;
    // [0] << 16 + [1] << 8 + [2] << 0
    protected override uint TestHostValue => 1_048_577u;
    protected override byte[] TestBytes { get; } = {0x10, 0x00, 0x01};
}

public class BigEndianUInt32Test : BigEndianUIntTest<BigEndianUInt32>
{
    public BigEndianUInt32Test(ITestOutputHelper outputHelper) : base(outputHelper)
    {
    }

    protected override int TypeSize => 4;
    // [0] << 24 + [1] << 16 + [2] << 8 + [3] << 0
    protected override uint TestHostValue => 268_435_457u;
    protected override byte[] TestBytes { get; } = {0x10, 0x00, 0x00, 0x01};
}

public abstract class BigEndianUIntTest<TTarget> where TTarget : struct, IBigEndianUInt
{
    protected abstract int TypeSize { get; }
    protected abstract uint TestHostValue { get; }
    protected abstract byte[] TestBytes { get; }

    private readonly ITestOutputHelper _output;

    protected BigEndianUIntTest(ITestOutputHelper outputHelper)
    {
        _output = outputHelper;
    }

    [Fact]
    public void TestSize()
    {
        var size = Marshal.SizeOf<TTarget>();
        size.Should().Be(TypeSize);
    }

    [Fact]
    public void TestFromBigEndian()
    {
        var target = MemoryMarshal.AsRef<TTarget>(TestBytes);
        MarshalToBytes(ref target).Should().BeEquivalentTo(TestBytes, "Original bytes must be preserved.");
        target.HostValue.Should().Be(TestHostValue);
    }

    [Fact]
    public void TestFromHostValue()
    {
        if (!BitConverter.IsLittleEndian)
        {
            _output.WriteLine("Current system is not Little Endian. So this test may not make sense.");
        }

        var target = new TTarget { HostValue = TestHostValue };
        MarshalToBytes(ref target).Should().BeEquivalentTo(TestBytes);
    }

    private static byte[] MarshalToBytes(ref TTarget target)
    {
        return MemoryMarshal.AsBytes(new Span<TTarget>(ref target)).ToArray();
    }
}
