using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

// Add console logging
builder.Logging.AddConsole();

// Add SignalR services
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.EnableDetailedErrors = true;
    options.StreamBufferCapacity = 20;
});

var app = builder.Build();

// Map the hub
app.MapHub<CockpitHub>("/sharedcockpithub");

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

// Simple shared cockpit relay server - pure passthrough like recorder/playback

public class SessionInfo
{
    public HashSet<string> ConnectionIds { get; set; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? CurrentPilotFlying { get; set; }
}

public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    
    private static readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private static readonly ConcurrentDictionary<string, string> _connectionToSession = new();

    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinSession(string sessionCode, string clientId, bool isHost)
    {
        if (string.IsNullOrWhiteSpace(sessionCode))
        {
            _logger.LogWarning("Connection {ConnectionId} attempted to join with empty session code", Context.ConnectionId);
            return;
        }

        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        // Get or create session
        var session = _sessions.GetOrAdd(sessionCode, _ => new SessionInfo());
        
        // Add connection to session
        session.ConnectionIds.Add(Context.ConnectionId);
        session.LastActivity = DateTime.UtcNow;
        _connectionToSession[Context.ConnectionId] = sessionCode;
        
        // HOST IS ALWAYS INITIAL PILOT FLYING
        if (isHost && string.IsNullOrEmpty(session.CurrentPilotFlying))
        {
            session.CurrentPilotFlying = clientId;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        // Notify other clients with current pilot flying status
        await Clients.OthersInGroup(sessionCode).SendAsync("ClientConnected", clientId, isHost);
        await Clients.Group(sessionCode).SendAsync("PilotFlyingChanged", _sessions[sessionCode].CurrentPilotFlying);
        
        _logger.LogInformation("Connection {ConnectionId} ({ClientId}) joined session {SessionCode} as {Role}. Current PF: {PF} (Total: {Count})", 
            Context.ConnectionId, clientId, sessionCode, isHost ? "Host" : "Client", _sessions[sessionCode].CurrentPilotFlying, _sessions[sessionCode].ConnectionIds.Count);
    }
    
    public async Task SendAircraftData(string sessionCode, byte[] aircraftData)
    {
        if (string.IsNullOrWhiteSpace(sessionCode)) return;
        
        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        // Pure passthrough - just relay data to other clients immediately
        // No rate limiting, no modifications, exactly like recorder playback
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", aircraftData);
    }
    
    public async Task GiveControl(string sessionCode, string fromClientId, string toClientId)
    {
        if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
        {
            session.CurrentPilotFlying = toClientId;
            session.LastActivity = DateTime.UtcNow;
            
            _logger.LogInformation("Control given from {FromClient} to {ToClient} in session {SessionCode}", fromClientId, toClientId, sessionCode);
            await Clients.Group(sessionCode).SendAsync("PilotFlyingChanged", toClientId);
        }
    }
    
    public async Task TakeControl(string sessionCode, string clientId)
    {
        if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
        {
            var previousPF = session.CurrentPilotFlying;
            session.CurrentPilotFlying = clientId;
            session.LastActivity = DateTime.UtcNow;
            
            _logger.LogInformation("Control taken by {ClientId} from {PreviousClient} in session {SessionCode}", clientId, previousPF, sessionCode);
            await Clients.Group(sessionCode).SendAsync("PilotFlyingChanged", clientId);
        }
    }
    
    public async Task LeaveSession(string sessionCode, string clientId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionCode);
        await Clients.OthersInGroup(sessionCode).SendAsync("ClientDisconnected", clientId);
        _logger.LogInformation("Client {ClientId} left session {SessionCode}", clientId, sessionCode);
    }
    
    public Task Heartbeat(string clientId)
    {
        _logger.LogDebug("Heartbeat from {ClientId}", clientId);
        // Could update last seen time here if needed
        return Task.CompletedTask;
    }
    
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connectionToSession.TryRemove(Context.ConnectionId, out string? sessionCode) && sessionCode != null)
        {
            if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
            {
                session.ConnectionIds.Remove(Context.ConnectionId);
                
                // Clean up empty sessions
                if (!session.ConnectionIds.Any())
                {
                    _sessions.TryRemove(sessionCode, out _);
                    _logger.LogInformation("Session {SessionCode} removed - no clients remaining", sessionCode);
                }
            }
        }
        
        await base.OnDisconnectedAsync(exception);
    }
} 