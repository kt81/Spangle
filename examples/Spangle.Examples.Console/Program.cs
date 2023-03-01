using System.CommandLine;
using Microsoft.Extensions.Logging;
using Spangle.Examples.Console;
using Spangle.Logging;

var mode = new Argument<string>(
    name: "mode",
    description: "Mode to run (srs-hls / rtmp-hls)",
    getDefaultValue: () => "srt-hls");
var cmd = new RootCommand("Spangle Example Console application.") { mode };

var loggerFactory = LoggerFactory.Create(conf =>
{
    conf.AddFilter("Spangle", LogLevel.Trace)
        .AddColorizedZLoggerConsole("Spangle");
});
SpangleLogManager.SetLoggerFactory(loggerFactory);

cmd.SetHandler(async m =>
{
    switch (m)
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
}, mode);

await cmd.InvokeAsync(args);
