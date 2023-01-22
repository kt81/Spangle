using System.Runtime.CompilerServices;

namespace Spangle.SourceGenerator.Tests.Rtmp;

[UsesVerify]
public class Amf0SerializableGeneratorSnapshotTests
{
    [Fact]
    public Task GeneratesPartialSerializerCorrectly()
    {
        var source = $$"""
using Spangle.SourceGenerator.Rtmp;
namespace Spangle.Tests;

[Amf0Serializable]
internal partial struct {{GetName()}}
{
    [Amf0Field(0)]
    public int IntField;
    [Amf0Field(1)]
    public float FloatField;
    [Amf0Field(2)]
    public string StringField;
}
""";

        return TestHelper.Verify(source);
    }

    [Fact]
    public Task AlsoWorksWithProperty()
    {
        var source = $$"""
using Spangle.SourceGenerator.Rtmp;
namespace Spangle.Tests;

[Amf0Serializable]
internal partial struct {{GetName()}}
{
    [Amf0Field(0)]
    public int IntField {get;set;}
    [Amf0Field(1)]
    public float FloatField {get;set;}
    [Amf0Field(2)]
    public string StringField {get;set;}
}
""";

        return TestHelper.Verify(source);
    }

    [Fact]
    public Task GeneratedButEmptyWithNoAmf0Field()
    {
        var source = $$"""
using Spangle.SourceGenerator.Rtmp;
namespace Spangle.Tests;

[Amf0Serializable]
internal partial struct {{GetName()}}
{
    public int IntField;
    public float FloatField;
    public string StringField;
}
""";
        return TestHelper.Verify(source);
    }

    [Fact]
    public Task NotGeneratedWithoutAttribute()
    {
        var source = $$"""
using Spangle.SourceGenerator.Rtmp;
namespace Spangle.Tests;

internal partial struct {{GetName()}}
{
    [Amf0Field(0)]
    public int IntField;
    [Amf0Field(1)]
    public float FloatField;
    [Amf0Field(2)]
    public string StringField;
}
""";
        return TestHelper.Verify(source);
    }

    [Fact]
    public Task DuplicatedToAmf0ObjectMethod()
    {
        var source = $$"""
using System.Buffers;
using Spangle.SourceGenerator.Rtmp;

namespace Spangle.Tests;

[Amf0Serializable]
internal partial struct {{GetName()}}
{
    [Amf0Field(0)]
    public int IntField;
    [Amf0Field(1)]
    public float FloatField;
    [Amf0Field(2)]
    public string StringField;

    public int ToAmf0Object(IBufferWriter<byte> writer) => 0;
}
""";
        return TestHelper.Verify(source);
    }

    [Fact]
    public Task UnsupportedTypeFieldError()
    {
        var source = $$"""
using Spangle.SourceGenerator.Rtmp;

namespace Spangle.Tests;

[Amf0Serializable]
internal partial struct {{GetName()}}
{
    [Amf0Field(0)]
    public int IntField;
    [Amf0Field(1)]
    public float FloatField;
    [Amf0Field(2)]
    public string StringField;
    [Amf0Field(3)]
    public {{nameof(Amf0SerializableGeneratorSnapshotTests)}} ClassField;
}
""";
        return TestHelper.Verify(source);
    }

    private static string GetName([CallerMemberName] string? caller = null)
    {
        return caller! + "TestStruct";
    }
}
