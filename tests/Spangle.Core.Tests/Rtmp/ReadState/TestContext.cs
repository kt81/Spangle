using System.Buffers;
using System.IO.Pipelines;
using Spangle.Rtmp;

namespace Spangle.Tests.Rtmp.ReadState;

internal static class TestContext
{
    /// <summary>
    ///
    /// </summary>
    /// <param name="dataToReader"></param>
    /// <returns>context, writtenPipe</returns>
    public static async ValueTask<(RtmpReceiverContext, PipeReader)> WithData(byte[] dataToReader)
    {
        var readPipe = new Pipe();
        var writePipe = new Pipe();
        var cts = new CancellationTokenSource();
        // Test case timeout
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        var context = RtmpReceiverContext.CreateInstance("test", readPipe.Reader, writePipe.Writer, cts.Token);
        // `Read` pipe is read by tests
        readPipe.Writer.Write(dataToReader);
        await readPipe.Writer.FlushAsync(cts.Token);
        // `Write` pipe is written by the StateAction and is read by tests
        // var writtenPipe = writePipe.Reader;
        return (context, writePipe.Reader);
    }
}
