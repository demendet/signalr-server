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
    options.MaximumReceiveMessageSize = 1048576; // 1MB - increased for better performance
    options.StreamBufferCapacity = 64; // Increased buffer capacity for more stable connections
    options.KeepAliveInterval = TimeSpan.FromSeconds(5); // More frequent pings to detect disconnects faster
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60); // Longer timeout to handle temporary network issues
    options.HandshakeTimeout = TimeSpan.FromSeconds(30); // More time for initial handshake
    options.EnableDetailedErrors = true; // Enable detailed errors for easier debugging
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
app.MapHub<MergedCockpitHub>("/cockpithub");

app.Run();

#region Data Transfer Objects

// Position and flight dynamics data for basic synchronization
public class AircraftPositionData
{
    // Position and attitude
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public double Pitch { get; set; }
    public double Bank { get; set; }
    public double Heading { get; set; }
    
    // Flight dynamics
    public double GroundSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public double AirspeedTrue { get; set; }
    public double AirspeedIndicated { get; set; }
    public double OnGround { get; set; }
    
    // Control surfaces
    public double Throttle { get; set; }
    public double Aileron { get; set; }
    public double Elevator { get; set; }
    public double Rudder { get; set; }
    public double ElevatorTrimPosition { get; set; }
    
    // Brakes and gear
    public double BrakeLeftPosition { get; set; }
    public double BrakeRightPosition { get; set; }
    public double ParkingBrakePosition { get; set; }
    public int GearHandlePosition { get; set; }
    public double FlapsHandlePosition { get; set; }
    
    // Engine controls
    public double MixturePosition { get; set; }
    
    // Motion data
    public double VelocityBodyX { get; set; }
    public double VelocityBodyY { get; set; }
    public double VelocityBodyZ { get; set; }
    
    // Lights
    public double LightBeacon { get; set; }
    public double LightLanding { get; set; }
    public double LightTaxi { get; set; }
    public double LightNav { get; set; }
    public double LightStrobe { get; set; }
    public double PitotHeat { get; set; }
    
    // Timestamp for ordering
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
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

// Light states DTO
public class LightStatesDto
{
    public bool LightBeacon { get; set; }
    public bool LightLanding { get; set; }
    public bool LightTaxi { get; set; }
    public bool LightNav { get; set; }
    public bool LightStrobe { get; set; }
}

// Pitot heat state DTO
public class PitotHeatStateDto
{
    public bool PitotHeatOn { get; set; }
}

// G1000 softkey press DTO
public class G1000SoftkeyPressDto
{
    public int SoftkeyNumber { get; set; }
}

// Radio frequency change DTO
public class RadioFrequencyChangeDto
{
    public string RadioType { get; set; } // "NAV1", "NAV2", "COM1", "COM2"
    public int SubIndex { get; set; } // 0=active, 1=standby, 2=swap
    public double Value { get; set; } // Frequency value or 1.0 for swap
}

// Transponder change DTO
public class TransponderChangeDto
{
    public int SubIndex { get; set; } // 0=code, 1=mode, 2=ident
    public double Value { get; set; } // Code value, mode value, or 1.0 for ident
}

// ADF change DTO
public class AdfChangeDto
{
    public int SubIndex { get; set; } // 0=frequency, 1=card
    public double Value { get; set; } // Frequency or card value
}

// OBS change DTO
public class ObsChangeDto
{
    public int SubIndex { get; set; } // 0=NAV1, 1=NAV2, 2=GPS
    public double Value { get; set; } // OBS value in degrees
}

// Avionics change DTO
public class AvionicsChangeDto
{
    public int SubIndex { get; set; } // 0=master, 1=bus1, 2=bus2
    public double Value { get; set; } // 0.0 or 1.0 for off/on
}

// Electrical master change DTO
public class ElectricalMasterChangeDto
{
    public int SubIndex { get; set; } // 0=battery, 1=alternator
    public double Value { get; set; } // 0.0 or 1.0 for off/on
}

// Light change DTO
public class LightChangeDto
{
    public int SubIndex { get; set; } // 0=nav, 1=beacon, 2=landing, 3=taxi, 4=strobe, 5=panel, 6=pitot heat
    public double Value { get; set; } // 0.0 or 1.0 for off/on
}

// Autopilot change DTO
public class AutopilotChangeDto
{
    public int SubIndex { get; set; } // 0=master, 1=FD, 2=HDG, 3=NAV, 4=APR, 5=ALT, 6=VS, 7=FLC, 8=HDG setting, etc.
    public double Value { get; set; } // 0.0 or 1.0 for mode toggles, or actual values for settings
}

// Connection quality enum
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

public class MergedCockpitHub : Hub
{
    private readonly ILogger<MergedCockpitHub> _logger;
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
    
    public MergedCockpitHub(ILogger<MergedCockpitHub> logger)
    {
        _logger = logger;
    }
    
    public async Task JoinSession(string sessionCode)
    {
        try
        {
            _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
            await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);

            bool isFirstClient = false;

            lock (_lockObj)
            {
                if (!_sessionConnections.ContainsKey(sessionCode))
                {
                    _sessionConnections[sessionCode] = new List<string>();
                    _sessionVariableValues[sessionCode] = new Dictionary<string, VariableChangeDto>();
                    _clientHealthStatus[sessionCode] = new Dictionary<string, ClientHealthStatus>();
                    isFirstClient = true;
                }

                if (!_sessionConnections[sessionCode].Contains(Context.ConnectionId))
                {
                    // Add new client to the session connections list
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
            string previousController = null;
            bool shouldNotifyPreviousController = false;
            
            lock (_lockObj)
            {
                // If this is the first client (host), or no controller exists yet
                if (isFirstClient || !_sessionControlMap.ContainsKey(sessionCode))
                {
                    _sessionControlMap[sessionCode] = Context.ConnectionId;
                    hasControl = true;
                    _logger.LogInformation("Control assigned to {ControlId} in session {SessionCode} (isHost: {IsHost})",
                        Context.ConnectionId, sessionCode, isFirstClient);
                }
                else
                {
                    // If this is not the first client, check if they already had control (reconnecting case)
                    hasControl = _sessionControlMap[sessionCode] == Context.ConnectionId;

                    // Special case: If the joining client is reconnecting as host, give them control
                    bool isHost = _sessionConnections[sessionCode].FirstOrDefault() == Context.ConnectionId;
                    if (isHost && !hasControl)
                    {
                        // Host is reconnecting, transfer control back to them
                        previousController = _sessionControlMap[sessionCode];
                        _sessionControlMap[sessionCode] = Context.ConnectionId;
                        hasControl = true;
                        shouldNotifyPreviousController = true;

                        _logger.LogInformation("Host {HostId} reconnected - control transferred from {PreviousId}",
                            Context.ConnectionId, previousController);
                    }
                }
            }
            
            // We've moved these async calls outside the lock
            if (shouldNotifyPreviousController && !string.IsNullOrEmpty(previousController))
            {
                await Clients.Client(previousController).SendAsync("ControlStatusChanged", false);
                await Clients.Groups(sessionCode).SendAsync("ControlTransferred", Context.ConnectionId, "host");
            }

            // Notify this client of their control status
            await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
            _logger.LogInformation("Notified client {ClientId} of control status ({HasControl}) in session {SessionCode}",
                Context.ConnectionId, hasControl, sessionCode);

            // Send the current state of all variables to the new client
            if (_sessionVariableValues.TryGetValue(sessionCode, out var variables) && variables.Count > 0)
            {
                foreach (var variable in variables.Values)
                {
                    await Clients.Caller.SendAsync("ReceiveVariableChange", variable);
                }
                _logger.LogInformation("Sent {Count} cached variables to client {ClientId}", variables.Count, Context.ConnectionId);
            }

            // Tell everyone who the current controller is
            string currentController = _sessionControlMap.TryGetValue(sessionCode, out var controller) ? controller : "";
            bool controllerIsHost = _sessionConnections[sessionCode].FirstOrDefault() == currentController;

            await Clients.Group(sessionCode).SendAsync("ControlInfo",
                currentController, controllerIsHost ? "host" : "client");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in JoinSession for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    #region Custom Variable Methods
    
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
    
    #endregion
    
    #region Position and Control Methods
    
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
    
    #endregion
    
    #region Avionics Methods
    
    public async Task SendLightStates(string sessionCode, LightStatesDto lights)
    {
        try
        {
            _logger.LogInformation("Received light states in session {SessionCode}", sessionCode);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightStates", lights);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendLightStates for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task SendPitotHeatState(string sessionCode, PitotHeatStateDto state)
    {
        try
        {
            _logger.LogInformation("Received pitot heat state in session {SessionCode}: {State}", sessionCode, state.PitotHeatOn);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceivePitotHeatState", state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendPitotHeatState for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task SendG1000SoftkeyPress(string sessionCode, G1000SoftkeyPressDto press)
    {
        try
        {
            _logger.LogInformation("Received G1000 softkey press in session {SessionCode}: {Number}", sessionCode, press.SoftkeyNumber);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveG1000SoftkeyPress", press);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendG1000SoftkeyPress for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task SendRadioFrequencyChange(string sessionCode, RadioFrequencyChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received radio frequency change in session {SessionCode}: {Radio} subIndex={SubIndex}", 
                sessionCode, change.RadioType, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveRadioFrequencyChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendRadioFrequencyChange for {ConnectionId}", Context.ConnectionId);
        }
    }

    public async Task SendTransponderChange(string sessionCode, TransponderChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received transponder change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveTransponderChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendTransponderChange for {ConnectionId}", Context.ConnectionId);
        }
    }

    public async Task SendAdfChange(string sessionCode, AdfChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received ADF change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAdfChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendAdfChange for {ConnectionId}", Context.ConnectionId);
        }
    }

    public async Task SendObsChange(string sessionCode, ObsChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received OBS change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveObsChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendObsChange for {ConnectionId}", Context.ConnectionId);
        }
    }

    public async Task SendAvionicsChange(string sessionCode, AvionicsChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received avionics change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAvionicsChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendAvionicsChange for {ConnectionId}", Context.ConnectionId);
        }
    }

    public async Task SendElectricalMasterChange(string sessionCode, ElectricalMasterChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received electrical master change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveElectricalMasterChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendElectricalMasterChange for {ConnectionId}", Context.ConnectionId);
        }
    }

    public async Task SendLightChange(string sessionCode, LightChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received light change in session {SessionCode}: subIndex={SubIndex}, value={Value}", 
                sessionCode, change.SubIndex, change.Value);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendLightChange for {ConnectionId}", Context.ConnectionId);
        }
    }

    public async Task SendAutopilotChange(string sessionCode, AutopilotChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received autopilot change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAutopilotChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendAutopilotChange for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    #endregion
    
    #region Control Management
    
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

            // Check if this is a "host" client based on being the first client in the session
            bool isHostClient = false;
            string mostLikelyHost = null;

            lock (_lockObj)
            {
                if (_sessionConnections.TryGetValue(sessionCode, out var connections) && connections.Count > 0)
                {
                    // First connection in the list is considered the host
                    mostLikelyHost = connections.FirstOrDefault();
                    isHostClient = Context.ConnectionId == mostLikelyHost;
                }
            }

            // For taking control (giving=false)
            if (!giving)
            {
                // Priority logic for control requests:
                // 1. Host client always gets control if requested
                // 2. Other clients can take control if no controller exists
                // 3. Other clients can take control from non-host controller
                // 4. Other clients cannot take control from host controller

                bool shouldGetControl = false;

                if (isHostClient)
                {
                    // Host always gets control when requested
                    shouldGetControl = true;
                    _logger.LogInformation("Host client {HostId} requested control - priority granted", Context.ConnectionId);
                }
                else if (string.IsNullOrEmpty(currentController))
                {
                    // No controller exists, allow taking control
                    shouldGetControl = true;
                }
                else if (currentController != mostLikelyHost)
                {
                    // Current controller is not the host, allow transfer
                    shouldGetControl = true;
                }
                else
                {
                    // Current controller is the host, don't allow taking control
                    _logger.LogInformation("Request from {ClientId} denied - host {HostId} has control",
                        Context.ConnectionId, mostLikelyHost);
                    shouldGetControl = false;

                    // Confirm current control status
                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                    return;
                }

                if (shouldGetControl && currentController != Context.ConnectionId)
                {
                    string oldController = currentController;

                    // Save the new controller in our map
                    lock (_lockObj)
                    {
                        _sessionControlMap[sessionCode] = Context.ConnectionId;
                    }

                    // Notify the requester they now have control
                    await Clients.Caller.SendAsync("ControlStatusChanged", true);

                    // Notify all clients about the control change for UI updates
                    await Clients.Groups(sessionCode).SendAsync("ControlTransferred", Context.ConnectionId,
                        isHostClient ? "host" : "client");

                    _logger.LogInformation("Control transferred to {NewController} from {OldController}",
                        Context.ConnectionId, oldController);

                    // Notify the previous controller they lost control (if they exist)
                    if (!string.IsNullOrEmpty(oldController))
                    {
                        await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                    }
                }
                else if (currentController == Context.ConnectionId)
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
                    // Find another client to give control to (if any)
                    string newController = null;
                    bool newControllerIsHost = false;

                    lock (_lockObj)
                    {
                        if (_sessionConnections.TryGetValue(sessionCode, out var connections) &&
                            connections.Count > 0)
                        {
                            // Prioritize giving control back to host if available
                            if (connections.Contains(mostLikelyHost) && mostLikelyHost != Context.ConnectionId)
                            {
                                newController = mostLikelyHost;
                                newControllerIsHost = true;
                                _logger.LogInformation("Returning control to host {HostId}", newController);
                            }
                            else
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
                            }

                            if (!string.IsNullOrEmpty(newController))
                            {
                                // Give control to the new controller
                                _sessionControlMap[sessionCode] = newController;
                            }
                            else
                            {
                                // If no other clients to give control to
                                _sessionControlMap.Remove(sessionCode);
                            }
                        }
                        else
                        {
                            // No connections left
                            _sessionControlMap.Remove(sessionCode);
                        }
                    }

                    // Notify this client they no longer have control
                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                    _logger.LogInformation("Control released by {Controller} in session {SessionCode}",
                        Context.ConnectionId, sessionCode);

                    // Notify all clients about the control change
                    if (!string.IsNullOrEmpty(newController))
                    {
                        await Clients.Groups(sessionCode).SendAsync("ControlTransferred", newController,
                            newControllerIsHost ? "host" : "client");

                        // Notify the new controller they have control
                        await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                        _logger.LogInformation("Control automatically transferred to {NewController}", newController);
                    }
                    else
                    {
                        await Clients.Groups(sessionCode).SendAsync("ControlTransferred", "", "none");
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
    
    #endregion
    
    #region Connection Management
    
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
    
    #endregion
} 