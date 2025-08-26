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

// Add SignalR services with optimized settings for flight sim data
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1024 * 1024; // 1MB for aircraft data
    options.EnableDetailedErrors = true;
    options.StreamBufferCapacity = 50;
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60); // Longer timeout for network issues
    options.KeepAliveInterval = TimeSpan.FromSeconds(15); // More frequent keepalive
});

// Add CORS for development
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Use CORS
app.UseCors();

// Map the hub
app.MapHub<CockpitHub>("/sharedcockpithub");

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

// Enhanced shared cockpit relay server with dynamic variable sync

public class SessionInfo
{
    public HashSet<string> ConnectionIds { get; set; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string? CurrentPilotFlying { get; set; }
    public Dictionary<string, string> ClientNames { get; set; } = new(); // ConnectionId -> ClientId mapping
    public string? CurrentAircraftProfile { get; set; } // Track which aircraft profile is active
}

public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    
    private static readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private static readonly ConcurrentDictionary<string, string> _connectionToSession = new();
    private static readonly ConcurrentDictionary<string, string> _connectionToClientId = new();

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
        session.ClientNames[Context.ConnectionId] = clientId;
        session.LastActivity = DateTime.UtcNow;
        _connectionToSession[Context.ConnectionId] = sessionCode;
        _connectionToClientId[Context.ConnectionId] = clientId;
        
        // HOST IS ALWAYS INITIAL PILOT FLYING
        if (isHost && string.IsNullOrEmpty(session.CurrentPilotFlying))
        {
            session.CurrentPilotFlying = clientId;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        // Notify other clients with current pilot flying status
        await Clients.OthersInGroup(sessionCode).SendAsync("ClientConnected", clientId, isHost);
        await Clients.Group(sessionCode).SendAsync("PilotFlyingChanged", session.CurrentPilotFlying);
        
        _logger.LogInformation("Connection {ConnectionId} ({ClientId}) joined session {SessionCode} as {Role}. Current PF: {PF} (Total: {Count})", 
            Context.ConnectionId, clientId, sessionCode, isHost ? "Host" : "Client", session.CurrentPilotFlying, session.ConnectionIds.Count);
    }
    
    public async Task SendAircraftData(string sessionCode, byte[] aircraftData)
    {
        if (string.IsNullOrWhiteSpace(sessionCode)) return;
        
        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        // Update session activity
        if (_sessions.TryGetValue(sessionCode, out var session))
        {
            session.LastActivity = DateTime.UtcNow;
        }
        
        // Pure passthrough - just relay data to other clients immediately
        // No rate limiting, no modifications, exactly like recorder playback
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", aircraftData);
    }
    
    // NEW: Dynamic variable synchronization for aircraft profiles
    public async Task SendVariableSync(string sessionCode, string variableName, object value, string variableType)
    {
        if (string.IsNullOrWhiteSpace(sessionCode)) return;
        
        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        // Update session activity
        if (_sessions.TryGetValue(sessionCode, out var session))
        {
            session.LastActivity = DateTime.UtcNow;
        }
        
        // Relay variable change to other clients in session
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveVariableSync", variableName, value, variableType);
        
        _logger.LogDebug("Variable sync: {Variable} = {Value} in session {Session}", variableName, value, sessionCode);
    }
    
    // NEW: Set aircraft profile for session
    public async Task SetAircraftProfile(string sessionCode, string profileName)
    {
        if (_sessions.TryGetValue(sessionCode, out var session))
        {
            session.CurrentAircraftProfile = profileName;
            session.LastActivity = DateTime.UtcNow;
            
            // Notify all clients in session about profile change
            await Clients.Group(sessionCode).SendAsync("AircraftProfileChanged", profileName);
            
            _logger.LogInformation("Aircraft profile set to {Profile} in session {Session}", profileName, sessionCode);
        }
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
        // Update session activity on heartbeat
        if (_connectionToSession.TryGetValue(Context.ConnectionId, out var sessionCode) && 
            _sessions.TryGetValue(sessionCode, out var session))
        {
            session.LastActivity = DateTime.UtcNow;
        }
        
        _logger.LogDebug("Heartbeat from {ClientId}", clientId);
        return Task.CompletedTask;
    }
    
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (_connectionToSession.TryRemove(Context.ConnectionId, out string? sessionCode) && sessionCode != null)
        {
            if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
            {
                session.ConnectionIds.Remove(Context.ConnectionId);
                session.ClientNames.Remove(Context.ConnectionId);
                
                // If the disconnected client was pilot flying, clear it
                if (_connectionToClientId.TryGetValue(Context.ConnectionId, out var clientId) && 
                    session.CurrentPilotFlying == clientId)
                {
                    session.CurrentPilotFlying = null;
                    await Clients.Group(sessionCode).SendAsync("PilotFlyingChanged", (string?)null);
                    _logger.LogInformation("Pilot flying cleared due to disconnect in session {SessionCode}", sessionCode);
                }
                
                // Notify others of disconnect
                if (!string.IsNullOrEmpty(clientId))
                {
                    await Clients.Group(sessionCode).SendAsync("ClientDisconnected", clientId);
                }
                
                // Clean up empty sessions
                if (!session.ConnectionIds.Any())
                {
                    _sessions.TryRemove(sessionCode, out _);
                    _logger.LogInformation("Session {SessionCode} removed - no clients remaining", sessionCode);
                }
            }
        }
        
        _connectionToClientId.TryRemove(Context.ConnectionId, out _);
        
        if (exception != null)
        {
            _logger.LogWarning("Client disconnected with error: {Error}", exception.Message);
        }
        
        await base.OnDisconnectedAsync(exception);
    }
}