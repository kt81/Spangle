using System.Buffers;
using System.IO.Pipelines;
using Spangle.IO;

namespace Spangle.Tests.IO;

public class PipeReaderExtensionsTest
{
    [Fact]
    public async Task ReadExactlyFromSingleSegmentTest()
    {
        var pipe = new Pipe(new PipeOptions(minimumSegmentSize: 5));
        var data = new byte[] { 0, 1, 2, 3, 4 };
        pipe.Writer.Write(data);
        await pipe.Writer.CompleteAsync();

        (ReadOnlySequence<byte> byteSeq, _) = await pipe.Reader.ReadExactAsync(3);
        byteSeq.First.ToArray().Should().BeEquivalentTo(new byte[] { 0, 1, 2 });
        pipe.Reader.AdvanceTo(byteSeq.Start);

        (byteSeq, _) = await pipe.Reader.ReadExactAsync(3, 2);
        byteSeq.First.ToArray().Should().BeEquivalentTo(new byte[] { 2, 3, 4 });
        pipe.Reader.AdvanceTo(byteSeq.Start);
    }

    [Fact]
    public async Task ReadExactlyFromMultipleSegmentTest()
    {
        byte[][] data =
            {
                new byte[] { 0, 1, 2 },
                Array.Empty<byte>(),
                new byte[] { 3, 4, 5 },
                new byte[] { 6, 7, 8 },
                new byte[] { 9, 10, 11 },
            };
        var firstSeg = new TestSegment(data[0]);
        var lastSeg = firstSeg;
        for (var i = 1; i < 5; i++)
        {
            lastSeg = lastSeg.Append(data[i]);
        }

        var buff = new ReadOnlySequence<byte>(firstSeg, 0, lastSeg, lastSeg.Memory.Length);
        var reader = PipeReader.Create(buff);

        (ReadOnlySequence<byte> byteSeq, _) = await reader.ReadExactAsync(5);
        byteSeq.IsSingleSegment.Should().BeFalse();
        byteSeq.First.ToArray().Should().BeEquivalentTo(new byte[] { 0, 1, 2 });
        byteSeq.ToArray().Should().BeEquivalentTo(new byte[] { 0, 1, 2, 3, 4 });
        reader.AdvanceTo(byteSeq.Start);

        (byteSeq, _) = await reader.ReadExactAsync(9, 3);
        byteSeq.First.ToArray().Should().BeEquivalentTo(new byte[]{ 3, 4, 5 });
        byteSeq.ToArray().Should().BeEquivalentTo(new byte[] { 3, 4, 5, 6, 7, 8, 9, 10, 11 });
        reader.AdvanceTo(byteSeq.Start);
    }

    private class TestSegment : ReadOnlySequenceSegment<byte>
    {
        public TestSegment(ReadOnlyMemory<byte> memory) => Memory = memory;
        public TestSegment Append(ReadOnlyMemory<byte> memory)
        {
            var segment = new TestSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = segment;
            return segment;
        }
    }
}
