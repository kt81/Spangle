using System.IO.Pipelines;
using System.Runtime.InteropServices;
using FluentAssertions;
using Spangle.Rtmp.Amf0;
using Spangle.SourceGenerator.Rtmp;

namespace Spangle.SourceGenerator.Tests.Rtmp;

public class Amf0SerializableGeneratorTests
{
    [Fact]
    public async Task TestWriteAsAmf0Command()
    {
        var pipe = new Pipe();
        var writer = pipe.Writer;
        var reader = pipe.Reader;

        var data = new TestStruct();
        int size = data.WriteBytes(writer);
        await writer.FlushAsync();
        size.Should().BeGreaterThan(Marshal.SizeOf<TestStruct>());

        // Unmarshal back to host value but cannot return to an original struct, to dictionary.
        var result = await reader.ReadAtLeastAsync(size);
        var buff = result.Buffer;
        buff.Length.Should().BeGreaterOrEqualTo(size);

        object? expectsIntField = Amf0SequenceParser.Parse(ref buff);
        // Type information is lost during marshaling (by design)
        // For numeric types, the original type is lost.
        expectsIntField.Should().NotBeNull();
        expectsIntField.Should().BeOfType<double>("IntField comes as AMF number (double) type");
        expectsIntField.Should().Be((double)data.IntField);

        // NOTE: Parse(ref buff) advances the buff.End position so we don't have to do anything.
        object? expectsFloatField = Amf0SequenceParser.Parse(ref buff);
        expectsIntField.Should().NotBeNull();
        expectsFloatField.Should().BeOfType<double>();
        expectsFloatField.Should().Be((double)data.FloatField);

        object? expectsStringField = Amf0SequenceParser.Parse(ref buff);
        expectsStringField.Should().BeOfType<string>();
        expectsStringField.Should().Be(data.StringField);
    }

}

[Amf0Serializable]
internal readonly partial struct TestStruct
{
    public TestStruct()
    {
    }

    [Amf0Field(0)]
    public readonly int IntField = 0xFF00;
    [Amf0Field(1)]
    public readonly float FloatField = 3.14f;
    [Amf0Field(2)]
    public readonly string StringField = "SomeStringWith🧡マルチバイト文字";
}
