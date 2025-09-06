using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.SignalR;
using LupusInTabula.Hubs;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);

// ✅ Porta dinamica: 8080 su Render, 5000 in locale
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ✅ SignalR: ping più frequenti e timeout più tollerante
builder.Services.AddSignalR(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);      // server→client ping
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(120); // tolleranza
    o.EnableDetailedErrors = true;                       // 👈 utile per diagnosticare
});

// (se non usi MVC puoi rimuoverlo)
builder.Services.AddControllersWithViews();

// ✅ Compressione (abilita anche application/octet-stream per fallback SignalR)
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/octet-stream" // 👈 utile per SignalR long polling / server-sent events
    });
});
// (facoltativo) taratura livello gzip un po’ più spinto
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// (OPZIONALE) Solo se il front-end NON è servito dallo stesso dominio
// builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
//     .AllowAnyHeader().AllowAnyMethod().AllowCredentials()
//     .SetIsOriginAllowed(_ => true)));

var app = builder.Build();

// ✅ Rispetta gli header del proxy (https corretto dietro Render)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// (OPZIONALE) Se usi CORS
// app.UseCors();

app.UseResponseCompression();

// ✅ WebSockets keep-alive coerente
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15)
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Endpoint SignalR
app.MapHub<GameHub>("/gamehub");

app.Run();
