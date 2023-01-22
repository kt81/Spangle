using Microsoft.CodeAnalysis;

namespace Spangle.SourceGenerator;

public static class DiagnosticDescriptors
{
    private const string Category = "SpangleSourceGen";

    public static readonly DiagnosticDescriptor ToAmf0ObjectMethodExists = new(
        id: "SPGEN001",
        title: "Duplicated ToAmf0Object method",
        messageFormat: "The Amf0Serializable struct {0} must not contain its own implementation of ToAmf0Object in it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
    public static readonly DiagnosticDescriptor NotSupportedTypeField = new(
        id: "SPGEN002",
        title: "Unsupported field type",
        messageFormat: "The field type of '{0}' is not supported",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
