using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using FluentAssertions;
using Spangle.Rtmp.Amf0;
using Spangle.SourceGenerator.Rtmp;

namespace Spangle.SourceGenerator.Tests.Rtmp;

public class Amf0SerializableGeneratorTests
{
    [Fact]
    public async Task TestToAmf0Object()
    {
        var pipe = new Pipe();
        var writer = pipe.Writer;
        var reader = pipe.Reader;

        var data = new TestStruct();
        int size = data.ToAmf0Object(writer);
        size.Should().BeGreaterThan(Marshal.SizeOf<TestStruct>());
        await writer.FlushAsync();

        // Unmarshal back
        var result = await reader.ReadAtLeastAsync(size);
        var buff = result.Buffer;
        buff.Length.Should().BeGreaterOrEqualTo(size);
        buff = buff.Slice(0, size);

        object? objectResult = Amf0SequenceParser.Parse(ref buff);
        reader.AdvanceTo(buff.End);
        objectResult.Should().NotBeNull();
        // Object type information is lost during marshaling (by design)
        objectResult.Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>();
        var dic = (IReadOnlyDictionary<string, object?>)objectResult!;

        // The original type is lost with numeric types.
        dic.TryGetValue("IntField", out object? intField).Should().BeTrue();
        intField.Should().BeOfType<double>();
        intField.Should().Be((double)data.IntField);
        dic.TryGetValue("FloatField", out object? floatField).Should().BeTrue();
        floatField.Should().BeOfType<double>();
        floatField.Should().Be((double)data.FloatField);

        dic.TryGetValue("StringField", out object? stringField).Should().BeTrue();
        stringField.Should().BeOfType<string>();
        stringField.Should().Be(data.StringField);
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
