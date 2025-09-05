using Microsoft.AspNetCore.SignalR;
using LupusInTabula.Hubs; // <-- corretto namespace per GameHub

var builder = WebApplication.CreateBuilder(args);

// Fai in modo che ascolti su tutte le interfacce (0.0.0.0)
builder.WebHost.UseUrls("http://0.0.0.0:8080");

// Registrazione dei servizi
builder.Services.AddSignalR();
builder.Services.AddControllersWithViews();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Endpoint SignalR
app.MapHub<GameHub>("/gamehub");

app.Run();
