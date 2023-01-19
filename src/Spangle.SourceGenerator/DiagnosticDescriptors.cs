using Microsoft.CodeAnalysis;

namespace Spangle.SourceGenerator;

public static class DiagnosticDescriptors
{
    private static readonly string s_category = typeof(DiagnosticDescriptors).Namespace;

    public static readonly DiagnosticDescriptor ToAmf0ObjectMethodExists = new(
        id: "SPGEN001",
        title: "Amf0Serializable struct cannot has ToAmf0Object in itself.",
        messageFormat: "The GenerateToString class '{0}' has ToString override but it is not allowed.",
        category: s_category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
