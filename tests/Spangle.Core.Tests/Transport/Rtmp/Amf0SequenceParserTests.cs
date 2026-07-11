using System.Buffers;
using Spangle.Transport.Rtmp.Amf0;
using AmfObject = System.Collections.Generic.IReadOnlyDictionary<string, object?>;

namespace Spangle.Tests.Transport.Rtmp;

public class Amf0SequenceParserTests
{
    /// <summary>
    /// AMF0 nests with ~4 bytes per level, so one pre-auth connect message could
    /// otherwise recurse the parser into an uncatchable StackOverflowException
    /// that takes down the whole process.
    /// </summary>
    [Fact]
    public void DeeplyNestedObjectIsRejectedInsteadOfOverflowingTheStack()
    {
        var bytes = new List<byte>();
        for (var i = 0; i < 100; i++)
        {
            bytes.AddRange([0x03, 0x00, 0x01, (byte)'a']); // object marker + key "a"
        }

        var act = () =>
        {
            var buff = new ReadOnlySequence<byte>(bytes.ToArray());
            Amf0SequenceParser.Parse(ref buff);
        };
        act.Should().Throw<InvalidDataException>().WithMessage("*nesting*");
    }

    [Fact]
    public void ReasonablyNestedObjectsStillParse()
    {
        // { "a": { "a": "x" } } with proper object-end markers
        byte[] bytes =
        [
            0x03,
            0x00, 0x01, (byte)'a',
            0x03,
            0x00, 0x01, (byte)'a',
            0x02, 0x00, 0x01, (byte)'x',
            0x00, 0x00, 0x09, // object end
            0x00, 0x00, 0x09, // object end
        ];
        var buff = new ReadOnlySequence<byte>(bytes);

        object? parsed = Amf0SequenceParser.Parse(ref buff);

        var outer = parsed.Should().BeAssignableTo<AmfObject>().Subject;
        var inner = outer["a"].Should().BeAssignableTo<AmfObject>().Subject;
        inner["a"].Should().Be("x");
    }

    [Fact]
    public void EmptySequenceThrowsCleanly()
    {
        var act = () =>
        {
            var buff = ReadOnlySequence<byte>.Empty;
            Amf0SequenceParser.Parse(ref buff);
        };
        act.Should().Throw<InvalidDataException>();
    }
}
