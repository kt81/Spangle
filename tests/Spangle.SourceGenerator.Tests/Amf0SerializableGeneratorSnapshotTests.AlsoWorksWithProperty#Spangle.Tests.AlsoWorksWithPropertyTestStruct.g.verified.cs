﻿//HintName: Spangle.Tests.AlsoWorksWithPropertyTestStruct.g.cs
// <auto-generated/>
#nullable enable
#pragma warning disable CS8600
#pragma warning disable CS8601
#pragma warning disable CS8602
#pragma warning disable CS8603
#pragma warning disable CS8604

using System.IO.Pipelines;
using Spangle.Rtmp.Amf0;

namespace Spangle.Tests;

internal partial struct AlsoWorksWithPropertyTestStruct
{
    /// <summary>
    /// Write this structure itself to the buffer.
    /// </summary>
    public int ToAmf0Object(PipeWriter writer)
    {
        int total = 0;

        total += Amf0Writer.WriteObjectHeader(writer);
        total += Amf0Writer.WriteString(writer, "IntField", false);
        total += Amf0Writer.WriteNumber(writer, Convert.ToDouble(IntField));
        total += Amf0Writer.WriteString(writer, "FloatField", false);
        total += Amf0Writer.WriteNumber(writer, Convert.ToDouble(FloatField));
        total += Amf0Writer.WriteString(writer, "StringField", false);
        total += Amf0Writer.WriteString(writer, StringField);
        total += Amf0Writer.WriteObjectEnd(writer);

        writer.Advance(total);
        return total;
    }
}
