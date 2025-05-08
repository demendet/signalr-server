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

// Add console logging with appropriate log levels
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

// Connection quality tracking for clients
public enum ConnectionQuality
{
    Unknown = 0,
    Poor = 1,
    Fair = 2,
    Good = 3,
    Excellent = 4
}

// Client health status for connection monitoring
public class ClientHealthStatus
{
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    public int MissedHeartbeats { get; set; } = 0;
    public ConnectionQuality ConnectionQuality { get; set; } = ConnectionQuality.Unknown;
    public int LatencyMs { get; set; } = 0;
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
    
    // Lock object for thread safety
    private static readonly object _lockObj = new object();
    
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
            
            lock (_lockObj)
            {
                if (!_sessionConnections.ContainsKey(sessionCode))
                {
                    _sessionConnections[sessionCode] = new List<string>();
                    _sessionVariableValues[sessionCode] = new Dictionary<string, VariableChangeDto>();
                    _clientHealthStatus[sessionCode] = new Dictionary<string, ClientHealthStatus>();
                }
                
                if (!_sessionConnections[sessionCode].Contains(Context.ConnectionId))
                {
                    _sessionConnections[sessionCode].Add(Context.ConnectionId);
                }
                
                // Track this client's health status
                _clientHealthStatus[sessionCode][Context.ConnectionId] = new ClientHealthStatus
                {
                    LastHeartbeat = DateTime.UtcNow,
                    ConnectionQuality = ConnectionQuality.Good
                };
            }
            
            bool hasControl = false;
            
            lock (_lockObj)
            {
                if (!_sessionControlMap.ContainsKey(sessionCode))
                {
                    _sessionControlMap[sessionCode] = Context.ConnectionId;
                    hasControl = true;
                    _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", Context.ConnectionId, sessionCode);
                }
                else
                {
                    hasControl = _sessionControlMap[sessionCode] == Context.ConnectionId;
                }
            }
            
            await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
            _logger.LogInformation("Notified joining client {ClientId} of control status ({HasControl}) in session {SessionCode}", Context.ConnectionId, hasControl, sessionCode);
            
            // Send the current state of all variables to the new client
            if (_sessionVariableValues.TryGetValue(sessionCode, out var variables) && variables.Count > 0)
            {
                foreach (var variable in variables.Values)
                {
                    await Clients.Caller.SendAsync("ReceiveVariableChange", variable);
                }
                _logger.LogInformation("Sent {Count} cached variables to client {ClientId}", variables.Count, Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in JoinSession for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task SendVariableChange(string sessionCode, VariableChangeDto variable)
    {
        try
        {
            // Add source client ID for tracking
            variable.SourceClientId = Context.ConnectionId;
            
            // Update timestamp for conflict resolution
            variable.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            _logger.LogDebug("Variable change from {ConnectionId}: {VariableName}={Value}", 
                Context.ConnectionId, variable.VariableName, variable.Value);
            
            // Store the variable value for this session
            lock (_lockObj)
            {
                if (_sessionVariableValues.TryGetValue(sessionCode, out var variables))
                {
                    variables[variable.VariableName] = variable;
                }
            }
            
            // Send to all clients in the session except the sender
            await Clients.GroupExcept(sessionCode, Context.ConnectionId).SendAsync("ReceiveVariableChange", variable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendVariableChange for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task RequestVariableSync(string sessionCode)
    {
        try
        {
            // Get all variable values for this session and send to the requesting client
            if (_sessionVariableValues.TryGetValue(sessionCode, out var variables) && variables.Count > 0)
            {
                foreach (var variable in variables.Values)
                {
                    await Clients.Caller.SendAsync("ReceiveVariableChange", variable);
                }
                _logger.LogInformation("Synced {Count} variables to client {ClientId}", variables.Count, Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in RequestVariableSync for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task SendAircraftPosition(string sessionCode, AircraftPositionData data)
    {
        try
        {
            // Only process if this client has control
            if (_sessionControlMap.TryGetValue(sessionCode, out var controllerId) && 
                controllerId == Context.ConnectionId)
            {
                // Update timestamp for rate limiting
                lock (_lockObj)
                {
                    _lastPositionUpdates[sessionCode] = DateTime.UtcNow;
                }
                
                // Send to all clients in the session except the sender
                await Clients.GroupExcept(sessionCode, Context.ConnectionId)
                    .SendAsync("ReceiveAircraftPosition", data);
            }
            else
            {
                _logger.LogDebug("Ignoring position update from non-controller client {ConnectionId}", Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendAircraftPosition for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task TransferControl(string sessionCode, bool giving)
    {
        try
        {
            string currentController = null;
            
            // Get the current controller
            lock (_lockObj)
            {
                _sessionControlMap.TryGetValue(sessionCode, out currentController);
            }
            
            // For taking control (giving=false)
            if (!giving)
            {
                // Check if someone else has control and we want to take it
                if (currentController != Context.ConnectionId)
                {
                    string oldController = currentController;
                    
                    // Save the new controller in our map
                    lock (_lockObj)
                    {
                        _sessionControlMap[sessionCode] = Context.ConnectionId;
                    }
                    
                    // Notify the requester they now have control
                    await Clients.Caller.SendAsync("ControlStatusChanged", true);
                    _logger.LogInformation("Control transferred to {NewController} from {OldController}", 
                        Context.ConnectionId, oldController);
                    
                    // Notify the previous controller they lost control (if they exist)
                    if (!string.IsNullOrEmpty(oldController))
                    {
                        await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                    }
                }
                else
                {
                    // Already has control, just confirm
                    await Clients.Caller.SendAsync("ControlStatusChanged", true);
                }
            }
            // For giving up control (giving=true)
            else
            {
                // Check if this client currently has control
                if (currentController == Context.ConnectionId)
                {
                    // Remove controller mapping
                    lock (_lockObj)
                    {
                        _sessionControlMap.Remove(sessionCode);
                    }
                    
                    // Notify this client they no longer have control
                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                    _logger.LogInformation("Control released by {Controller} in session {SessionCode}", 
                        Context.ConnectionId, sessionCode);
                    
                    // Find another client to give control to (if any)
                    string newController = null;
                    
                    lock (_lockObj)
                    {
                        if (_sessionConnections.TryGetValue(sessionCode, out var connections) && 
                            connections.Count > 0 && 
                            connections.Contains(Context.ConnectionId))
                        {
                            // Find clients with good connection quality first
                            var healthyClients = 
                                from conn in connections
                                where conn != Context.ConnectionId && 
                                      _clientHealthStatus[sessionCode].ContainsKey(conn) &&
                                      _clientHealthStatus[sessionCode][conn].ConnectionQuality >= ConnectionQuality.Good
                                select conn;
                                
                            // Choose first healthy client or any client if none are healthy
                            newController = healthyClients.FirstOrDefault() ?? 
                                connections.FirstOrDefault(c => c != Context.ConnectionId);
                                
                            if (!string.IsNullOrEmpty(newController))
                            {
                                // Give control to the new controller
                                _sessionControlMap[sessionCode] = newController;
                            }
                        }
                    }
                    
                    // Notify the new controller they have control (if any)
                    if (!string.IsNullOrEmpty(newController))
                    {
                        await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                        _logger.LogInformation("Control automatically transferred to {NewController}", newController);
                    }
                }
                else
                {
                    // Didn't have control to begin with, just confirm no control
                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TransferControl for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task SendHeartbeat(string sessionCode)
    {
        try
        {
            // Update this client's last heartbeat time
            lock (_lockObj)
            {
                if (_clientHealthStatus.TryGetValue(sessionCode, out var clientsHealth) &&
                    clientsHealth.TryGetValue(Context.ConnectionId, out var health))
                {
                    health.LastHeartbeat = DateTime.UtcNow;
                    health.MissedHeartbeats = 0;
                }
            }
            
            // Send acknowledgment back to the client
            await Clients.Caller.SendAsync("HeartbeatAck");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendHeartbeat for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        try
        {
            // Find all sessions this client is part of
            var sessionsToProcess = new List<string>();
            
            lock (_lockObj)
            {
                foreach (var kvp in _sessionConnections)
                {
                    string sessionCode = kvp.Key;
                    var clients = kvp.Value;
                    
                    if (clients.Contains(Context.ConnectionId))
                    {
                        sessionsToProcess.Add(sessionCode);
                    }
                }
            }
            
            foreach (var sessionCode in sessionsToProcess)
            {
                // Remove client from session
                lock (_lockObj)
                {
                    if (_sessionConnections.TryGetValue(sessionCode, out var clients))
                    {
                        clients.Remove(Context.ConnectionId);
                        
                        // Remove health status
                        if (_clientHealthStatus.TryGetValue(sessionCode, out var healthStatuses))
                        {
                            healthStatuses.Remove(Context.ConnectionId);
                        }
                    }
                }
                
                // Check if this client had control of the session
                bool hadControl = false;
                string newController = null;
                
                lock (_lockObj)
                {
                    if (_sessionControlMap.TryGetValue(sessionCode, out var controllerId) && 
                        controllerId == Context.ConnectionId)
                    {
                        hadControl = true;
                        _sessionControlMap.Remove(sessionCode);
                        
                        // Find another client to transfer control to
                        if (_sessionConnections.TryGetValue(sessionCode, out var remainingClients) && 
                            remainingClients.Count > 0)
                        {
                            // Find clients with good connection quality first
                            var healthyClients = 
                                from conn in remainingClients
                                where _clientHealthStatus[sessionCode].ContainsKey(conn) &&
                                      _clientHealthStatus[sessionCode][conn].ConnectionQuality >= ConnectionQuality.Good
                                select conn;
                                
                            // Choose first healthy client or any client if none are healthy
                            newController = healthyClients.FirstOrDefault() ?? remainingClients.FirstOrDefault();
                            
                            if (!string.IsNullOrEmpty(newController))
                            {
                                _sessionControlMap[sessionCode] = newController;
                            }
                        }
                    }
                }
                
                // If this client had control, transfer it to another client
                if (hadControl && !string.IsNullOrEmpty(newController))
                {
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    _logger.LogInformation("Control transferred to {NewController} after disconnect of {OldController}", 
                        newController, Context.ConnectionId);
                }
                
                // Clean up empty sessions
                lock (_lockObj)
                {
                    if (_sessionConnections.TryGetValue(sessionCode, out var clients) && clients.Count == 0)
                    {
                        _sessionConnections.Remove(sessionCode);
                        _sessionVariableValues.Remove(sessionCode);
                        _sessionControlMap.Remove(sessionCode);
                        _clientHealthStatus.Remove(sessionCode);
                        _lastPositionUpdates.Remove(sessionCode);
                        
                        _logger.LogInformation("Removed empty session {SessionCode}", sessionCode);
                    }
                }
            }
            
            _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnDisconnectedAsync for {ConnectionId}", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
    }
} 