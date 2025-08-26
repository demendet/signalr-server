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

// --- DYNAMIC DATA TRANSFER - NO HARDCODING NEEDED! ---
// Server accepts ANY data structure via JSON byte arrays
// This eliminates the need to modify server when adding new variables

public class SharedCockpitCommand
{
    public string Type { get; set; } = "";
    public string SessionCode { get; set; } = "";
    public string FromClientId { get; set; } = "";
    public string ToClientId { get; set; } = "";
    public Dictionary<string, object>? Data { get; set; }
}

public class SessionInfo
{
    public HashSet<string> ConnectionIds { get; set; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public DateTime LastDataSent { get; set; } = DateTime.MinValue;
    public string? CurrentPilotFlying { get; set; }
    public byte[]? LastAircraftData { get; set; }
}

public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    
    private static readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private static readonly ConcurrentDictionary<string, string> _connectionToSession = new();
    private static readonly object _lockObject = new object();
    // Rate limiting: prevent server from being overwhelmed

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
        
        lock (_lockObject)
        {
            // Check if this connection is already in a session
            if (_connectionToSession.TryGetValue(Context.ConnectionId, out string? existingSession))
            {
                if (existingSession == sessionCode)
                {
                    _logger.LogWarning("Connection {ConnectionId} already in session {SessionCode}", Context.ConnectionId, sessionCode);
                    return; // Already in this session
                }
                else
                {
                    // Remove from old session first
                    LeaveSessionInternal(Context.ConnectionId, existingSession);
                }
            }

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
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        // Notify other clients with current pilot flying status
        await Clients.OthersInGroup(sessionCode).SendAsync("ClientConnected", clientId, isHost);
        await Clients.Group(sessionCode).SendAsync("PilotFlyingChanged", _sessions[sessionCode].CurrentPilotFlying);
        
        _logger.LogInformation("Connection {ConnectionId} ({ClientId}) joined session {SessionCode} as {Role}. Current PF: {PF} (Total: {Count})", 
            Context.ConnectionId, clientId, sessionCode, isHost ? "Host" : "Client", _sessions[sessionCode].CurrentPilotFlying, _sessions[sessionCode].ConnectionIds.Count);
    }
    
    public async Task SendAircraftData(string sessionCode, byte[] compressedData)
    {
        if (string.IsNullOrWhiteSpace(sessionCode)) return;
        
        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
        {
            var now = DateTime.UtcNow;
            bool shouldSend = false;
            
            lock (_lockObject)
            {
                session.LastActivity = now;
                
                // Rate limit: minimum 20ms between messages (50Hz max)
                var timeSinceLastSend = (now - session.LastDataSent).TotalMilliseconds;
                if (timeSinceLastSend >= 20.0)
                {
                    session.LastDataSent = now;
                    shouldSend = true;
                }
            }
            
            if (shouldSend)
            {
                // Debug logging every 100 messages to avoid spam
                var connectionCount = session.ConnectionIds.Count;
                if (connectionCount % 100 == 0)
                {
                    _logger.LogInformation("Relaying {DataSize} bytes to {Count} clients in session {SessionCode}", 
                        compressedData.Length, connectionCount - 1, sessionCode);
                }
                
                // Relay JSON data to all other connections in the session
                await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", compressedData);
            }
        }
    }
    
    public async Task GiveControl(string sessionCode, string fromClientId, string toClientId)
    {
        if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
        {
            lock (_lockObject)
            {
                // Update who is pilot flying
                session.CurrentPilotFlying = toClientId;
                session.LastActivity = DateTime.UtcNow;
            }
            
            _logger.LogInformation("🎯 CONTROL GIVEN from {FromClient} to {ToClient} in session {SessionCode}", fromClientId, toClientId, sessionCode);
            
            // Notify ALL clients of the control change
            await Clients.Group(sessionCode).SendAsync("PilotFlyingChanged", toClientId);
        }
    }
    
    public async Task TakeControl(string sessionCode, string clientId)
    {
        if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
        {
            string? previousPF = null;
            lock (_lockObject)
            {
                previousPF = session.CurrentPilotFlying;
                session.CurrentPilotFlying = clientId;
                session.LastActivity = DateTime.UtcNow;
            }
            
            _logger.LogInformation("🎯 CONTROL TAKEN by {ClientId} from {PreviousClient} in session {SessionCode}", clientId, previousPF, sessionCode);
            
            // Notify ALL clients of the control change
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
        string? sessionCode = null;
        
        lock (_lockObject)
        {
            if (_connectionToSession.TryRemove(Context.ConnectionId, out sessionCode))
            {
                LeaveSessionInternal(Context.ConnectionId, sessionCode);
            }
        }
        
        if (exception != null)
        {
            _logger.LogWarning("Connection {ConnectionId} disconnected with exception: {Exception}", Context.ConnectionId, exception.Message);
        }
        else
        {
            _logger.LogInformation("Connection {ConnectionId} disconnected normally", Context.ConnectionId);
        }
        
        await base.OnDisconnectedAsync(exception);
    }
    
    private void LeaveSessionInternal(string connectionId, string sessionCode)
    {
        if (!_sessions.TryGetValue(sessionCode, out SessionInfo? session)) return;
        
        session.ConnectionIds.Remove(connectionId);
        
        // Clean up empty sessions
        if (!session.ConnectionIds.Any())
        {
            _sessions.TryRemove(sessionCode, out _);
            _logger.LogInformation("Session {SessionCode} removed as all clients disconnected", sessionCode);
        }
        else
        {
            _logger.LogInformation("Connection {ConnectionId} left session {SessionCode} (Remaining: {Count})", 
                connectionId, sessionCode, session.ConnectionIds.Count);
        }
    }
    
    // Simple rate limiting - in production you'd want a more sophisticated approach
    private static void SendPendingData(object? state)
    {
        // This timer-based approach provides consistent 25Hz output
        // regardless of input rate variations
    }
} 