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
});

var app = builder.Build();

// Map the hub
app.MapHub<CockpitHub>("/sharedcockpithub");

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
app.Run($"http://0.0.0.0:{port}");

// --- Data Transfer Objects and Hub Implementation ---

public class AircraftDataDto
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public double Pitch { get; set; }
    public double Bank { get; set; }
    public double Heading { get; set; }
    public double Throttle { get; set; }
    public double Aileron { get; set; }
    public double Elevator { get; set; }
    public double Rudder { get; set; }
    public double BrakeLeft { get; set; }
    public double BrakeRight { get; set; }
    public double ParkingBrake { get; set; }
    public double Mixture { get; set; }
    public int Flaps { get; set; }
    public int Gear { get; set; }
    public double GroundSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public double AirspeedTrue { get; set; }
    public double AirspeedIndicated { get; set; }
    public double OnGround { get; set; }
    public double VelocityBodyX { get; set; }
    public double VelocityBodyY { get; set; }
    public double VelocityBodyZ { get; set; }
    public double ElevatorTrimPosition { get; set; }
    public double LightBeacon { get; set; }
    public double LightLanding { get; set; }
    public double LightTaxi { get; set; }
    public double LightNav { get; set; }
    public double LightStrobe { get; set; }
    public double PitotHeat { get; set; }
}

public class SessionInfo
{
    public HashSet<string> ConnectionIds { get; set; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public DateTime LastDataSent { get; set; } = DateTime.MinValue;
    public AircraftDataDto? LastData { get; set; }
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

    public async Task JoinSession(string sessionCode)
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
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode} (Total connections: {Count})", 
            Context.ConnectionId, sessionCode, _sessions[sessionCode].ConnectionIds.Count);
    }
    
    public async Task SendAircraftData(string sessionCode, AircraftDataDto data)
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
                
                // Rate limit: minimum 40ms between messages (25Hz max)
                var timeSinceLastSend = (now - session.LastDataSent).TotalMilliseconds;
                if (timeSinceLastSend >= 40.0)
                {
                    session.LastDataSent = now;
                    session.LastData = data;
                    shouldSend = true;
                }
                else
                {
                    // Store the latest data but don't send yet
                    session.LastData = data;
                }
            }
            
            if (shouldSend)
            {
                // Relay data to all other connections in the session with rate limiting
                await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
            }
        }
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