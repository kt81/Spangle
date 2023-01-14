namespace Spangle.Rtmp.ReadState;

internal static class StateStore<TProcessor> where TProcessor : IReadStateAction
{
    // ReSharper disable once StaticMemberInGenericType
    public static readonly IReadStateAction.Action Action;

    static StateStore() => Action = TProcessor.Perform;
}
