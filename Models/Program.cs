using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using LupusInTabula.Hubs;

var builder = WebApplication.CreateBuilder(args);

// ✅ Porta dinamica: 8080 su Render, 5000 in locale
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ✅ SignalR: ping più frequenti e timeout più tollerante
builder.Services.AddSignalR(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);      // server→client ping
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(120); // tolleranza
});

// (se non usi MVC puoi rimuoverlo)
builder.Services.AddControllersWithViews();

// ✅ Compressione (aiuta anche con long polling)
builder.Services.AddResponseCompression(options => { options.EnableForHttps = true; });

var app = builder.Build();

// ✅ Rispetta gli header del proxy (https corretto dietro Render)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

app.UseResponseCompression();

app.UseDefaultFiles();
app.UseStaticFiles();

// Endpoint SignalR
app.MapHub<GameHub>("/gamehub");

app.Run();
