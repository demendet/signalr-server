using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add console logging with appropriate log levels - reduce verbosity for production
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Add SignalR services with increased buffer size and keep-alive settings
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 262144; // 256KB - increased for better performance
    options.StreamBufferCapacity = 30; // Increased buffer capacity
    options.KeepAliveInterval = TimeSpan.FromSeconds(10); // More frequent pings
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30); // Give clients more time before timeout
    options.HandshakeTimeout = TimeSpan.FromSeconds(15); // More time for initial handshake
});

// Add CORS to allow client connections with more flexible settings
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy",
        builder => builder
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true));
});

var app = builder.Build();

// Enable CORS
app.UseCors("CorsPolicy");

// Map the hub
app.MapHub<DynamicVariableHub>("/cockpithub");

app.Run();

#region Data Transfer Objects

// Position and flight dynamics data for basic synchronization
public class AircraftPositionData
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public double Pitch { get; set; }
    public double Bank { get; set; }
    public double Heading { get; set; }
    public double GroundSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public double AirspeedTrue { get; set; }
    public double AirspeedIndicated { get; set; }
    
    // Control surfaces
    public double Aileron { get; set; }
    public double Elevator { get; set; }
    public double Rudder { get; set; }
    public double ElevatorTrim { get; set; }
    public double FlapsHandlePosition { get; set; }
    
    // Brakes
    public double BrakeLeftPosition { get; set; }
    public double BrakeRightPosition { get; set; }
    public double ParkingBrakePosition { get; set; }
    
    // Engine controls
    public double Throttle { get; set; }
    public double MixturePosition { get; set; }
    
    // Gear
    public int GearHandlePosition { get; set; }
    
    // Motion data
    public double OnGround { get; set; }
    public double VelocityBodyX { get; set; }
    public double VelocityBodyY { get; set; }
    public double VelocityBodyZ { get; set; }
} 

// Dynamic variable change DTO
public class VariableChangeDto
{
    public string VariableName { get; set; }
    public string VariableType { get; set; } 
    public string AccessMethod { get; set; }
    public string Value { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
    public bool IsBroadcast { get; set; } = false; // Flag to prevent echo loops
    public string SourceClientId { get; set; } // Identifies which client sent the change
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // For ordering and conflict resolution
}

#endregion

public class DynamicVariableHub : Hub
{
    private readonly ILogger<DynamicVariableHub> _logger;
    private static readonly Dictionary<string, string> _sessionControlMap = new();
    private static readonly Dictionary<string, List<string>> _sessionConnections = new();
    
    // Track variable values per session for late-joining clients
    private static readonly Dictionary<string, Dictionary<string, VariableChangeDto>> _sessionVariableValues = new();
    
    // Track last position update per session
    private static readonly Dictionary<string, DateTime> _lastPositionUpdates = new();
    
    // Track client's connection stability per session
    private static readonly Dictionary<string, Dictionary<string, ClientHealthStatus>> _clientHealthStatus = new();
    
    public DynamicVariableHub(ILogger<DynamicVariableHub> logger)
    {
        _logger = logger;
    }
    
    public async Task JoinSession(string sessionCode)
    {
        try
        {
            _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
            await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
            
            if (!_sessionConnections.ContainsKey(sessionCode))
            {
                _sessionConnections[sessionCode] = new List<string>();
                _sessionVariableValues[sessionCode] = new Dictionary<string, VariableChangeDto>();
                _clientHealthStatus[sessionCode] = new Dictionary<string, ClientHealthStatus>();
            }
            
            _sessionConnections[sessionCode].Add(Context.ConnectionId);
            
            // Track this client's health status
            _clientHealthStatus[sessionCode][Context.ConnectionId] = new ClientHealthStatus
            {
                LastHeartbeat = DateTime.UtcNow,
                ConnectionQuality = ConnectionQuality.Good
            };
            
            if (!_sessionControlMap.ContainsKey(sessionCode))
            {
                _sessionControlMap[sessionCode] = Context.ConnectionId;
                await Clients.Caller.SendAsync("ControlStatusChanged", true);
                _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", Context.ConnectionId, sessionCode);
            }
            else
            {
                bool hasControl = _sessionControlMap[sessionCode] == Context.ConnectionId;
                await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
                _logger.LogInformation("Notified joining client {ClientId} of control status ({HasControl}) in session {SessionCode}", Context.ConnectionId, hasControl, sessionCode);
            }
            
            // Send the current state of all variables to the new client
            if (_sessionVariableValues.TryGetValue(sessionCode, out var variables) && variables.Count > 0)
            {
                foreach (var variable in variables.Values)
                {
                    await Clients.Caller.SendAsync("ReceiveVariableChange", variable);
                }
                _logger.LogInformation("Sent {Count} existing variables to new client {ClientId}", variables.Count, Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling join session for client {ClientId}", Context.ConnectionId);
        }
    }
    
    public async Task SendHeartbeat(string sessionCode)
    {
        try
        {
            if (_clientHealthStatus.TryGetValue(sessionCode, out var clientStatuses) &&
                clientStatuses.TryGetValue(Context.ConnectionId, out var status))
            {
                status.LastHeartbeat = DateTime.UtcNow;
                status.HeartbeatCount++;
                
                // Respond with acknowledgment 
                await Clients.Caller.SendAsync("HeartbeatAck");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling heartbeat for client {ClientId}", Context.ConnectionId);
        }
    }
    
    public async Task SendAircraftPosition(string sessionCode, AircraftPositionData data)
    {
        try
        {
            if (_sessionControlMap.TryGetValue(sessionCode, out string controlId) && controlId == Context.ConnectionId)
            {
                _logger.LogDebug("Received position data in session {SessionCode}: Alt={Alt:F1}", sessionCode, data.Altitude);
                
                // Update last position update time
                _lastPositionUpdates[sessionCode] = DateTime.UtcNow;
                
                // Send to others with minimal overhead
                await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftPosition", data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling position update for client {ClientId}", Context.ConnectionId);
        }
    }
    
    public async Task SendVariableChange(string sessionCode, VariableChangeDto change)
    {
        try
        {
            // Set source client ID to detect and prevent echo loops
            change.SourceClientId = Context.ConnectionId;
            change.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            _logger.LogInformation("Received variable change in session {SessionCode}: {Variable}={Value} (IsBroadcast={IsBroadcast})", 
                sessionCode, change.VariableName, change.Value, change.IsBroadcast);
            
            // Store latest variable value for the session (useful for clients joining later)
            if (!_sessionVariableValues.ContainsKey(sessionCode))
            {
                _sessionVariableValues[sessionCode] = new Dictionary<string, VariableChangeDto>();
            }
            
            // Only store the value if it's not a broadcast (to avoid feedback loops)
            if (!change.IsBroadcast)
            {
                // Use timestamp to ensure we don't overwrite newer values with older ones
                if (!_sessionVariableValues[sessionCode].TryGetValue(change.VariableName, out var existingChange) || 
                    existingChange.Timestamp < change.Timestamp)
                {
                    _sessionVariableValues[sessionCode][change.VariableName] = change;
                }
            }
            
            // Set broadcast flag to prevent echo loops when received on the other end
            change.IsBroadcast = true;
            
            // Send to others in the group
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveVariableChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing variable change");
        }
    }
    
    public async Task RequestVariableSync(string sessionCode)
    {
        try
        {
            if (_sessionVariableValues.TryGetValue(sessionCode, out var variables) && variables.Count > 0)
            {
                foreach (var variable in variables.Values)
                {
                    await Clients.Caller.SendAsync("ReceiveVariableChange", variable);
                }
                _logger.LogInformation("Sent {Count} variables to client {ClientId} upon request", variables.Count, Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling variable sync request");
        }
    }
    
    public async Task TransferControl(string sessionCode, bool giving)
    {
        try
        {
            string currentController = _sessionControlMap.GetValueOrDefault(sessionCode, "");
            
            if (giving)
            {
                if (currentController == Context.ConnectionId)
                {
                    var otherConnections = _sessionConnections[sessionCode].Where(id => id != Context.ConnectionId).ToList();
                    if (otherConnections.Any())
                    {
                        // Find the client with the best connection quality
                        string newController = otherConnections.First();
                        if (_clientHealthStatus.TryGetValue(sessionCode, out var clientStatuses))
                        {
                            var bestClient = clientStatuses
                                .Where(c => otherConnections.Contains(c.Key))
                                .OrderByDescending(c => (int)c.Value.ConnectionQuality)
                                .FirstOrDefault();
                            
                            if (!string.IsNullOrEmpty(bestClient.Key))
                            {
                                newController = bestClient.Key;
                            }
                        }
                        
                        _sessionControlMap[sessionCode] = newController;
                        await Clients.Caller.SendAsync("ControlStatusChanged", false);
                        await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                        _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", 
                            Context.ConnectionId, newController, sessionCode);
                    }
                }
            }
            else
            {
                if (currentController != Context.ConnectionId)
                {
                    string oldController = currentController;
                    _sessionControlMap[sessionCode] = Context.ConnectionId;
                    await Clients.Caller.SendAsync("ControlStatusChanged", true);
                    if (!string.IsNullOrEmpty(oldController))
                        await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                    _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", 
                        Context.ConnectionId, oldController, sessionCode);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling control transfer");
        }
    }
    
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        try
        {
            var sessions = _sessionConnections.Where(kvp => kvp.Value.Contains(Context.ConnectionId))
                                           .Select(kvp => kvp.Key)
                                           .ToList();
            
            foreach (var session in sessions)
            {
                _sessionConnections[session].Remove(Context.ConnectionId);
                
                if (_clientHealthStatus.TryGetValue(session, out var clientStatuses))
                {
                    clientStatuses.Remove(Context.ConnectionId);
                }
                
                if (_sessionControlMap.GetValueOrDefault(session) == Context.ConnectionId)
                {
                    if (_sessionConnections[session].Any())
                    {
                        // Find the client with the best connection quality
                        string newController = _sessionConnections[session].First();
                        if (_clientHealthStatus.TryGetValue(session, out var clientStatuses2))
                        {
                            var bestClient = clientStatuses2
                                .OrderByDescending(c => (int)c.Value.ConnectionQuality)
                                .FirstOrDefault();
                            
                            if (!string.IsNullOrEmpty(bestClient.Key))
                            {
                                newController = bestClient.Key;
                            }
                        }
                        
                        _sessionControlMap[session] = newController;
                        await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                        _logger.LogInformation("Control automatically transferred to {NewId} in session {SessionCode}", 
                            newController, session);
                    }
                    else
                    {
                        _sessionControlMap.Remove(session);
                        _sessionConnections.Remove(session);
                        _sessionVariableValues.Remove(session);
                        _clientHealthStatus.Remove(session);
                        _lastPositionUpdates.Remove(session);
                        _logger.LogInformation("Session {SessionCode} removed as all clients disconnected", session);
                    }
                }
                
                if (!_sessionConnections[session].Any())
                {
                    _sessionConnections.Remove(session);
                    _sessionControlMap.Remove(session);
                    _sessionVariableValues.Remove(session);
                    _clientHealthStatus.Remove(session);
                    _lastPositionUpdates.Remove(session);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnDisconnectedAsync");
        }
        
        await base.OnDisconnectedAsync(exception);
    }
}

#region Helper Classes

public enum ConnectionQuality
{
    Poor = 0,
    Fair = 1,
    Good = 2,
    Excellent = 3
}

public class ClientHealthStatus
{
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public int HeartbeatCount { get; set; } = 0;
    public ConnectionQuality ConnectionQuality { get; set; } = ConnectionQuality.Good;
    public int PacketLossCount { get; set; } = 0;
}

#endregion 