using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;

var builder = WebApplication.CreateBuilder(args);

// Add console logging.
builder.Logging.AddConsole();

// Add SignalR services with increased buffer size for smoother data flow
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 1024000; // 1MB for YourControls data
    options.StreamBufferCapacity = 50; // Increase buffer capacity for 100Hz sync
    options.EnableDetailedErrors = true;
});

var app = builder.Build();

// Use top-level route registration for the hub.
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

// YourControls SyncData class matching the client implementation
public class SyncData
{
    public Dictionary<string, double> Variables { get; set; } = new();
    public bool IsUnreliable { get; set; }
    public long Time { get; set; }
    public string From { get; set; } = "";
}

// The SignalR hub for YourControls synchronization
public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;

    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinSession(string sessionCode)
    {
        _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
    }

    // YourControls sync data method
    public async Task SendSyncData(string sessionCode, SyncData data)
    {
        // Ensure the data has a timestamp
        if (data.Time == 0)
        {
            data.Time = DateTime.UtcNow.Ticks;
        }
        
        // Set the sender
        data.From = Context.ConnectionId;
        
        // Log sync activity (reduced logging for performance)
        if (data.Variables.Count > 0)
        {
            _logger.LogDebug("YourControls sync from {ConnectionId} in session {SessionCode}: {VariableCount} variables, Unreliable={IsUnreliable}", 
                Context.ConnectionId, sessionCode, data.Variables.Count, data.IsUnreliable);
        }
            
        // Send the data to all OTHER clients in the session group (exclude sender)
        await Clients.GroupExcept(sessionCode, Context.ConnectionId).SendAsync("ReceiveSyncData", data);
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client {ConnectionId} connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected: {Exception}", Context.ConnectionId, exception?.Message ?? "Normal disconnect");
        await base.OnDisconnectedAsync(exception);
    }
}
