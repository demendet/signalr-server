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

// Add SignalR services with optimized settings for stability
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
    options.EnableDetailedErrors = true; // Better error reporting
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60); // Client timeout
    options.KeepAliveInterval = TimeSpan.FromSeconds(15); // Keep alive interval
    options.HandshakeTimeout = TimeSpan.FromSeconds(15); // Handshake timeout
    options.MaximumParallelInvocationsPerClient = 10; // Allow parallel calls
});

var app = builder.Build();

// Map the hub
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

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

public class LightStatesDto
{
    public bool LightBeacon { get; set; }
    public bool LightLanding { get; set; }
    public bool LightTaxi { get; set; }
    public bool LightNav { get; set; }
    public bool LightStrobe { get; set; }
}

public class PitotHeatStateDto
{
    public bool PitotHeatOn { get; set; }
}

public class G1000SoftkeyPressDto
{
    public int SoftkeyNumber { get; set; }
}

public class SessionInfo
{
    public string ControllerConnectionId { get; set; } = string.Empty;
    public string OriginalControllerConnectionId { get; set; } = string.Empty; // Remember original controller
    public HashSet<string> ConnectionIds { get; set; } = new();
    public Dictionary<string, DateTime> ConnectionJoinTimes { get; set; } = new(); // Track when connections joined
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}

// Store mapping of old connection IDs to new ones for reconnection handling
public class ReconnectionInfo
{
    public string SessionCode { get; set; } = string.Empty;
    public bool WasController { get; set; } = false;
    public DateTime DisconnectTime { get; set; } = DateTime.UtcNow;
}

public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    
    // Thread-safe collections to prevent race conditions
    private static readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private static readonly ConcurrentDictionary<string, string> _connectionToSession = new();
    private static readonly ConcurrentDictionary<string, ReconnectionInfo> _recentDisconnections = new();
    private static readonly object _lockObject = new object();

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
            session.ConnectionJoinTimes[Context.ConnectionId] = DateTime.UtcNow;
            session.LastActivity = DateTime.UtcNow;
            _connectionToSession[Context.ConnectionId] = sessionCode;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        bool hasControl = false;
        bool isReconnection = false;
        
        lock (_lockObject)
        {
            var session = _sessions[sessionCode];
            
            // Check if this is a reconnection of a previous controller
            var recentDisconnection = _recentDisconnections.Values
                .Where(r => r.SessionCode == sessionCode && r.WasController && 
                           (DateTime.UtcNow - r.DisconnectTime).TotalSeconds < 30)
                .OrderByDescending(r => r.DisconnectTime)
                .FirstOrDefault();
            
            if (recentDisconnection != null)
            {
                // This is likely a reconnection of the original controller
                session.ControllerConnectionId = Context.ConnectionId;
                hasControl = true;
                isReconnection = true;
                _logger.LogInformation("Controller reconnected and control restored to {ControlId} in session {SessionCode}", Context.ConnectionId, sessionCode);
                
                // Clean up the reconnection info
                var toRemove = _recentDisconnections.Where(kvp => kvp.Value == recentDisconnection).Select(kvp => kvp.Key).ToList();
                foreach (var key in toRemove)
                {
                    _recentDisconnections.TryRemove(key, out _);
                }
            }
            else if (string.IsNullOrEmpty(session.ControllerConnectionId))
            {
                // Assign control to first connection (original host)
                session.ControllerConnectionId = Context.ConnectionId;
                session.OriginalControllerConnectionId = Context.ConnectionId;
                hasControl = true;
                _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", Context.ConnectionId, sessionCode);
            }
            else
            {
                hasControl = session.ControllerConnectionId == Context.ConnectionId;
                _logger.LogInformation("Client {ClientId} joined session {SessionCode} with control status: {HasControl}", Context.ConnectionId, sessionCode, hasControl);
            }
        }

        await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
        
        if (isReconnection)
        {
            _logger.LogInformation("Reconnection successful for controller {ConnectionId} in session {SessionCode}", Context.ConnectionId, sessionCode);
        }
        
        _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode} (Total connections: {Count})", 
            Context.ConnectionId, sessionCode, _sessions[sessionCode].ConnectionIds.Count);
    }
    
    public async Task SendAircraftData(string sessionCode, AircraftDataDto data)
    {
        if (string.IsNullOrWhiteSpace(sessionCode)) return;
        
        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
        {
            lock (_lockObject)
            {
                if (session.ControllerConnectionId == Context.ConnectionId)
                {
                    session.LastActivity = DateTime.UtcNow;
                }
                else
                {
                    _logger.LogWarning("Unauthorized data send attempt from {ConnectionId} in session {SessionCode}", Context.ConnectionId, sessionCode);
                    return;
                }
            }
            
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
        }
    }
    
    public async Task SendLightStates(string sessionCode, LightStatesDto lights)
    {
        if (string.IsNullOrWhiteSpace(sessionCode)) return;
        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        _logger.LogInformation("Received light states in session {SessionCode}", sessionCode);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightStates", lights);
    }
    
    public async Task SendPitotHeatState(string sessionCode, PitotHeatStateDto state)
    {
        if (string.IsNullOrWhiteSpace(sessionCode)) return;
        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        _logger.LogInformation("Received pitot heat state in session {SessionCode}: {State}", sessionCode, state.PitotHeatOn);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceivePitotHeatState", state);
    }
    
    public async Task SendG1000SoftkeyPress(string sessionCode, G1000SoftkeyPressDto press)
    {
        if (string.IsNullOrWhiteSpace(sessionCode)) return;
        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        _logger.LogInformation("Received G1000 softkey press in session {SessionCode}: {Number}", sessionCode, press.SoftkeyNumber);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveG1000SoftkeyPress", press);
    }
    
    public Task TransferControl(string sessionCode, bool giving)
    {
        if (string.IsNullOrWhiteSpace(sessionCode)) return Task.CompletedTask;
        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        if (!_sessions.TryGetValue(sessionCode, out SessionInfo? session)) return Task.CompletedTask;
        
        lock (_lockObject)
        {
            if (giving)
            {
                // Only current controller can give control
                if (session.ControllerConnectionId == Context.ConnectionId)
                {
                    var otherConnections = session.ConnectionIds.Where(id => id != Context.ConnectionId).ToList();
                    if (otherConnections.Any())
                    {
                        var newController = otherConnections.First();
                        session.ControllerConnectionId = newController;
                        session.LastActivity = DateTime.UtcNow;
                        
                        // Notify both parties
                        _ = Task.Run(async () =>
                        {
                            await Clients.Caller.SendAsync("ControlStatusChanged", false);
                            await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                        });
                        
                        _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", Context.ConnectionId, newController, sessionCode);
                    }
                }
            }
            else
            {
                // Taking control
                if (session.ControllerConnectionId != Context.ConnectionId)
                {
                    string oldController = session.ControllerConnectionId;
                    session.ControllerConnectionId = Context.ConnectionId;
                    session.LastActivity = DateTime.UtcNow;
                    
                    // Notify both parties
                    _ = Task.Run(async () =>
                    {
                        await Clients.Caller.SendAsync("ControlStatusChanged", true);
                        if (!string.IsNullOrEmpty(oldController) && session.ConnectionIds.Contains(oldController))
                        {
                            await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                        }
                    });
                    
                    _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", Context.ConnectionId, oldController, sessionCode);
                }
            }
        }
        
        return Task.CompletedTask;
    }
    
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        string? sessionCode = null;
        bool wasController = false;
        
        lock (_lockObject)
        {
            if (_connectionToSession.TryRemove(Context.ConnectionId, out sessionCode))
            {
                if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
                {
                    wasController = session.ControllerConnectionId == Context.ConnectionId;
                    
                    // Store reconnection info for potential reconnection
                    if (wasController)
                    {
                        _recentDisconnections[Context.ConnectionId] = new ReconnectionInfo
                        {
                            SessionCode = sessionCode,
                            WasController = true,
                            DisconnectTime = DateTime.UtcNow
                        };
                    }
                }
                
                LeaveSessionInternal(Context.ConnectionId, sessionCode);
            }
        }
        
        if (exception != null)
        {
            _logger.LogWarning("Connection {ConnectionId} disconnected with exception: {Exception} (Was Controller: {WasController})", 
                Context.ConnectionId, exception.Message, wasController);
        }
        else
        {
            _logger.LogInformation("Connection {ConnectionId} disconnected normally (Was Controller: {WasController})", 
                Context.ConnectionId, wasController);
        }
        
        await base.OnDisconnectedAsync(exception);
    }
    
    private void LeaveSessionInternal(string connectionId, string sessionCode)
    {
        if (!_sessions.TryGetValue(sessionCode, out SessionInfo? session)) return;
        
        session.ConnectionIds.Remove(connectionId);
        session.ConnectionJoinTimes.Remove(connectionId);
        
        // Handle control transfer if controller left
        if (session.ControllerConnectionId == connectionId)
        {
            if (session.ConnectionIds.Any())
            {
                var newController = session.ConnectionIds.First();
                session.ControllerConnectionId = newController;
                session.LastActivity = DateTime.UtcNow;
                
                // Notify new controller
                _ = Task.Run(async () =>
                {
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                });
                
                _logger.LogInformation("Control automatically transferred to {NewId} in session {SessionCode}", newController, sessionCode);
            }
            else
            {
                session.ControllerConnectionId = string.Empty;
            }
        }
        
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
} 