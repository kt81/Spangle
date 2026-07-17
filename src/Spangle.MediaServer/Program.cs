using Spangle.Extensions.Kestrel;
using Spangle.Extensions.Kestrel.DependencyInjection;
using Spangle.Extensions.Kestrel.Management;

// The media server is deliberately thin: ingest, delivery and the console live in
// Spangle.Extensions.Kestrel, and this host only composes them. Anything that grows here should
// first justify why it is not an extension.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSpangle();
builder.WebHost.ConfigureSpangle();

var app = builder.Build();

// Control API for the console and for scripts; reachable only via the management port
app.MapSpangleManagement();

// Playlists (LL-HLS blocking reload), segments, /api/time, metadata injection
app.UseSpangleMediaDelivery();

// Web console (Blazor WASM) under /console, reachable only via the management port
app.UseSpangleConsole();

app.UseDefaultFiles();
app.UseStaticFiles(); // wwwroot (test player) + the console's static web assets

app.Run();
