using System.Buffers;
using System.IO.Pipelines;
using Spangle.Protocols.Rtmp;

namespace Spangle.Tests.Protocols.Rtmp.ReadState;

public class TestContext
{
    public RtmpReceiverContext Context { get; }
    public Pipe ReceivePipe { get; }
    public Pipe SendPipe { get; }
    public CancellationTokenSource CTokenSrc { get; }

    public TestContext()
    {
        ReceivePipe = new Pipe();
        SendPipe = new Pipe();
        CTokenSrc = new CancellationTokenSource();
        // Test case timeout
        CTokenSrc.CancelAfter(TimeSpan.FromSeconds(3));
        Context = RtmpReceiverContext.CreateInstance("test", ReceivePipe.Reader, SendPipe.Writer, CTokenSrc.Token);
    }

    /// <summary>
    ///
    /// </summary>
    /// <param name="dataToReader"></param>
    /// <returns>context, writtenPipe</returns>
    public static async ValueTask<TestContext> WithData(byte[] dataToReader)
    {
        var self = new TestContext();
        // `Receive` pipe is read by tests
        self.ReceivePipe.Writer.Write(dataToReader);
        await self.ReceivePipe.Writer.FlushAsync(self.CTokenSrc.Token);
        // `Send` pipe is written by the StateAction and is read by tests
        // var writtenPipe = writePipe.Reader;
        return self;
    }
}
