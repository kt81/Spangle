//HintName: Amf0SerializableAttribute.g.cs
namespace Spangle.SourceGenerator.Rtmp;

using System;

[AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
internal sealed class Amf0SerializableAttribute : Attribute
{
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
internal sealed class Amf0FieldAttribute : Attribute
{
    public int Position { get; private set; }
    public Amf0FieldAttribute(int position)
    {
        Position = position;
    }
}
