using System.CommandLine;
using Microsoft.Extensions.Logging;
using Spangle.Examples.Console;
using Spangle.Logging;

var mode = new Argument<string>("mode")
{
    Description = "Mode to run (srt-hls / rtmp-hls)",
    DefaultValueFactory = _ => "rtmp-hls",
};
var cmd = new RootCommand("Spangle Example Console application.") { mode };

var loggerFactory = LoggerFactory.Create(conf =>
{
    conf.AddFilter("Spangle", LogLevel.Trace)
        .AddColorizedZLoggerConsole("Spangle");
});
SpangleLogManager.SetLoggerFactory(loggerFactory);

cmd.SetAction(async (parseResult, _) =>
{
    switch (parseResult.GetValue(mode))
    {
        case "rtmp-hls":
            await new RtmpToHLS(loggerFactory.CreateLogger<RtmpToHLS>()).Start();
            return;
        case "srt-hls":
            await new SRTToHLS(loggerFactory.CreateLogger<SRTToHLS>()).Start();
            return;
        default:
            throw new ArgumentException("Unrecognized mode");
    }
});

return await cmd.Parse(args).InvokeAsync();
