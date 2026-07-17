using System.Buffers;
using System.Buffers.Binary;
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

    // The types below used to throw NotSupportedException, which killed the whole session
    // when an encoder put one Date or StrictArray into a cue point (or onMetaData).

    [Fact]
    public void UndefinedParsesAsNull()
    {
        var buff = new ReadOnlySequence<byte>([0x06]);
        Amf0SequenceParser.Parse(ref buff).Should().BeNull();
        buff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void StrictArrayParsesElementsInOrder()
    {
        byte[] bytes =
        [
            0x0A, 0x00, 0x00, 0x00, 0x02, // strict array, 2 elements
            0x00, 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // number 1.0
            0x02, 0x00, 0x01, (byte)'x', // string "x"
        ];
        var buff = new ReadOnlySequence<byte>(bytes);

        object? parsed = Amf0SequenceParser.Parse(ref buff);

        parsed.Should().BeEquivalentTo(new object?[] { 1.0, "x" }, o => o.WithStrictOrdering());
        buff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void StrictArrayCountBeyondTheRemainingBytesIsRejectedBeforeAllocating()
    {
        var act = () =>
        {
            var buff = new ReadOnlySequence<byte>([0x0A, 0xFF, 0xFF, 0xFF, 0xFF]);
            Amf0SequenceParser.Parse(ref buff);
        };
        act.Should().Throw<InvalidDataException>().WithMessage("*count*");
    }

    [Fact]
    public void DateParsesAsUnixEpochMilliseconds()
    {
        var bytes = new byte[11]; // marker + big-endian double + 2-byte time zone (reserved, 0)
        bytes[0] = 0x0B;
        BinaryPrimitives.WriteDoubleBigEndian(bytes.AsSpan(1), 1_696_118_400_000d);
        var buff = new ReadOnlySequence<byte>(bytes);

        object? parsed = Amf0SequenceParser.Parse(ref buff);

        parsed.Should().Be(DateTimeOffset.FromUnixTimeMilliseconds(1_696_118_400_000));
        buff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void NonFiniteDateIsRejected()
    {
        var act = () =>
        {
            byte[] bytes = [0x0B, 0x7F, 0xF8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]; // NaN
            var buff = new ReadOnlySequence<byte>(bytes);
            Amf0SequenceParser.Parse(ref buff);
        };
        act.Should().Throw<InvalidDataException>().WithMessage("*Date*");
    }

    [Fact]
    public void LongStringParses()
    {
        byte[] bytes = [0x0C, 0x00, 0x00, 0x00, 0x03, (byte)'a', (byte)'b', (byte)'c'];
        var buff = new ReadOnlySequence<byte>(bytes);
        Amf0SequenceParser.Parse(ref buff).Should().Be("abc");
        buff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void LongStringLengthBeyondTheRemainingBytesIsRejectedBeforeAllocating()
    {
        var act = () =>
        {
            var buff = new ReadOnlySequence<byte>([0x0C, 0xFF, 0xFF, 0xFF, 0xFF, (byte)'a']);
            Amf0SequenceParser.Parse(ref buff);
        };
        act.Should().Throw<InvalidDataException>().WithMessage("*length*");
    }

    [Fact]
    public void TypedObjectParsesAsItsAnonymousBody()
    {
        byte[] bytes =
        [
            0x10,
            0x00, 0x01, (byte)'T', // class name "T", dropped
            0x00, 0x01, (byte)'a',
            0x00, 0x3F, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // number 1.0
            0x00, 0x00, 0x09, // object end
        ];
        var buff = new ReadOnlySequence<byte>(bytes);

        object? parsed = Amf0SequenceParser.Parse(ref buff);

        var dic = parsed.Should().BeAssignableTo<AmfObject>().Subject;
        dic["a"].Should().Be(1.0);
        buff.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void ObjectEndAfterANonEmptyKeyIsMalformed()
    {
        var act = () =>
        {
            var buff = new ReadOnlySequence<byte>([0x03, 0x00, 0x01, (byte)'a', 0x09]);
            Amf0SequenceParser.Parse(ref buff);
        };
        act.Should().Throw<InvalidDataException>().WithMessage("*ObjectEnd*");
    }
}
