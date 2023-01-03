using Microsoft.AspNetCore.Connections;
using Sequin.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseKestrel(options =>
{
    options.ListenAnyIP(1935, listenOptions =>
    {
        listenOptions.UseConnectionHandler<RtmpConnectionHandler>();
    });

});
var app = builder.Build();

// app.MapGet("/", () => "Hello World!");

app.Run();
