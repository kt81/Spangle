namespace Spangle.Extensions;

public static class BufferExtensions
{
    public static void DumpHex(this byte[] data, Action<string> output) => DumpHex(new ReadOnlySpan<byte>(data), output);
    public static void DumpHex(this ReadOnlySpan<byte> data, Action<string> output)
    {
        const int tokenLen = 8;
        const int lineTokensLen = 8;
        while (data.Length > 0)
        {
            var lineTokens = new string[lineTokensLen];
            for (var i = 0; i < lineTokensLen && data.Length > 0; i++)
            {
                int len = Math.Min(tokenLen, data.Length);
                var token = data[..len];
                lineTokens[i] = string.Join(' ', token.ToArray().Select(x => $"{x:X02}"));
                data = data[len..];
            }
            output.Invoke(string.Join("  ", lineTokens));
        }
    }
}
