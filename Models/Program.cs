using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using LupusInTabula.Hubs;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);

// Porta dinamica (Render) o 5000 in locale
var port = Environment.GetEnvironmentVariable("PORT") ?? "5000";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// SignalR con ping/timeout più tolleranti
builder.Services.AddSignalR(o =>
{
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(120);
    o.EnableDetailedErrors = true;
});

// Se non usi MVC puoi rimuoverlo, ma non dà fastidio
builder.Services.AddControllersWithViews();

// Compressione (incluso octet-stream per fallback SignalR)
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/octet-stream"
    });
});
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// CORS (OPZIONALE) – abilitalo solo se servi l'HTML da un ORIGINE diversa.
// Imposta env var ALLOWED_ORIGINS con una o più origini separate da ';'
var allowedOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS");
if (!string.IsNullOrWhiteSpace(allowedOrigins))
{
    var origins = allowedOrigins.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    builder.Services.AddCors(o => o.AddPolicy("client", p =>
        p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
}

var app = builder.Build();

// Proxy headers (es. Render/Caddy/Nginx) per wss/https corretti
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    // Se necessario: KnownNetworks/CProxies clear
    // KnownNetworks = { }, KnownProxies = { }
});

// CORS se configurato sopra
if (!string.IsNullOrWhiteSpace(allowedOrigins))
{
    app.UseCors("client");
}

app.UseResponseCompression();

// WebSockets keep-alive coerente con client
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15)
});

app.UseDefaultFiles(); // index.html da wwwroot
app.UseStaticFiles();

// Healthcheck semplice
app.MapGet("/health", () => Results.Ok("ok"));

// IMPORTANTISSIMO: path in minuscolo, combacia col client ("/gamehub")
app.MapHub<GameHub>("/gamehub");

app.Run();
