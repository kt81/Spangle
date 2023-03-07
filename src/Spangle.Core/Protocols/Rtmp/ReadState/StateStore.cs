using System.Diagnostics.CodeAnalysis;

namespace Spangle.Protocols.Rtmp.ReadState;

[SuppressMessage("ReSharper", "StaticMemberInGenericType")]
internal static class StateStore<TProcessor> where TProcessor : IReadStateAction
{
    public static readonly IReadStateAction.Action Action;

    static StateStore() => Action = TProcessor.Perform;
}
