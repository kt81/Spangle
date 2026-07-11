using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Spangle.Console;
using Spangle.Console.Api;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The app lives under /console/, the management API at the host root
var apiBase = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "/");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = apiBase });
builder.Services.AddScoped<ConsoleApiClient>();

await builder.Build().RunAsync();
