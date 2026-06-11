using Microsoft.AspNetCore.SignalR;
using YourApp.Hubs;   // <-- necessario per GameHub

var builder = WebApplication.CreateBuilder(args);

// Fai in modo che ascolti su tutte le interfacce (0.0.0.0)
builder.WebHost.UseUrls("http://192.168.1.105:5000");

// ... resto del codice
builder.Services.AddSignalR();
builder.Services.AddControllersWithViews();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapHub<GameHub>("/gamehub"); // endpoint SignalR

app.Run();
