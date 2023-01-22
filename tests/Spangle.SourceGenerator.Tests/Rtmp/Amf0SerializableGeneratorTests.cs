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
    public void TestToAmf0Object()
    {
        var stream = new MemoryStream(new byte[4000]);
        var writer = PipeWriter.Create(stream);
        var reader = PipeReader.Create(stream);

        var data = new TestStruct();
        int size = data.ToAmf0Object(writer);
        size.Should().BeGreaterThan(Marshal.SizeOf<TestStruct>());

        // Unmarshal back

        reader.TryRead(out var result).Should().BeTrue();
        var buff = result.Buffer;
        buff.Length.Should().Be(size);

        object? objectResult = Amf0SequenceParser.Parse(ref buff);
        objectResult.Should().NotBeNull();
        // Object type information is lost during marshaling (by design)
        var dic = objectResult as IReadOnlyDictionary<string, object?>;
        dic.Should().NotBeNull();

        // The original type is lost with numeric types.
        dic!.TryGetValue("IntField", out object? intField).Should().BeTrue();
        intField.Should().BeOfType<double>();


    }

}

[Amf0Serializable]
internal partial struct TestStruct
{
    public TestStruct()
    {
    }

    [Amf0Field(0)]
    public int IntField = 0xFF00;
    [Amf0Field(1)]
    public float FloatField = 3.14f;
    [Amf0Field(2)]
    public string StringField = "SomeStringWith🧡マルチバイト文字";
}
