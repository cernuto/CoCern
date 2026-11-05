using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using CoCern.Dashboard;
using CoCern.Dashboard.Services;
using BlazorBlueprint.Components;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });
builder.Services.AddScoped<DashboardConnection>();
builder.Services.AddBlazorBlueprintComponents(configureTheme: options =>
{
    // preferences.js is the dashboard's single appearance store.
    options.PersistToLocalStorage = false;
    options.DetectSystemPreference = false;
    options.DefaultRadius = .6;
});
await builder.Build().RunAsync();
