namespace Spangle.SourceGenerator.Tests.Rtmp;

[UsesVerify]
public class Amf0SerializableGeneratorSnapshotTests
{
    [Fact]
    public Task GeneratesPartialSerializerCorrectly()
    {
        const string source = """
using Spangle.SourceGenerator.Amf0SerializableGenerator;
namespace Spangle.Tests;

[Amf0Serializable]
public partial struct TestStruct
{
    [Amf0Field(0)]
    public int IntField = 0xFF00;
    [Amf0Field(1)]
    public float FloatField = 3.14;
    [Amf0Field(2)]
    public string StringField = "SomeStringWith🧡マルチバイト文字";
}
""";

        return TestHelper.Verify(source);
    }
}
