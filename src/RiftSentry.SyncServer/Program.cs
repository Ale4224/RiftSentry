using RiftSentry.SyncServer.Hubs;
using RiftSentry.SyncServer.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("SyncCors", policy =>
    {
        policy
            .SetIsOriginAllowed(_ => true)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
builder.Services.AddSignalR();
builder.Services.AddSingleton<LobbyRegistry>();

var app = builder.Build();

app.UseCors("SyncCors");

app.MapGet("/", () => Results.Ok(new { name = "RiftSentry Sync Server" }));
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapHub<SyncHub>("/syncHub");

app.Run();
