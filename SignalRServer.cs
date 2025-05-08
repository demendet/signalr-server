using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System;

var builder = WebApplication.CreateBuilder(args);

// Add console logging
builder.Logging.AddConsole();

// Add SignalR services with increased buffer size for smoother data flow
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60); // Client timeout after 60 seconds of inactivity
    options.KeepAliveInterval = TimeSpan.FromSeconds(15); // Send keepalive every 15 seconds
    options.HandshakeTimeout = TimeSpan.FromSeconds(15); // Allow 15 seconds for initial handshake
    options.EnableDetailedErrors = true; // Enable detailed errors (consider disabling in production)
    options.MaximumParallelInvocationsPerClient = 3; // Allow up to 3 parallel invocations per client
});

// Add CORS to allow client connections
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy",
        builder => builder
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true));
});

// Add health checks
builder.Services.AddHealthChecks();

var app = builder.Build();

// Enable CORS
app.UseCors("CorsPolicy");

// Add basic health endpoint
app.MapHealthChecks("/health");

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
    
    // Timestamp for data age tracking
    public DateTime TimeStamp { get; set; } = DateTime.UtcNow;
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
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// Session statistics for monitoring
public class SessionStats
{
    public string SessionCode { get; set; }
    public int ConnectionCount { get; set; }
    public string ControllerConnectionId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public int VariableCount { get; set; }
}

#endregion

public class DynamicVariableHub : Hub
{
    private readonly ILogger<DynamicVariableHub> _logger;
    
    // Thread-safe collections for session state
    private static readonly ConcurrentDictionary<string, string> _sessionControlMap = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _sessionConnections = new();
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, VariableChangeDto>> _sessionVariableValues = new();
    
    // Track last activity timestamp for sessions
    private static readonly ConcurrentDictionary<string, DateTime> _sessionLastActivity = new();
    
    // Track ground state for each session
    private static readonly ConcurrentDictionary<string, (double LastGroundState, DateTime LastUpdate)> _sessionGroundStates = new();
    
    // Track speed data for each session
    private static readonly ConcurrentDictionary<string, (double LastGroundSpeed, double LastAirspeedTrue, double LastAirspeedIndicated, DateTime LastUpdate)> _sessionSpeedStates = new();
    
    // Track control transfer state to prevent race conditions
    private static readonly ConcurrentDictionary<string, (string PendingId, DateTime LockTime)> _controlTransferLocks = new();
    
    // Track client connection stability
    private static readonly ConcurrentDictionary<string, (bool IsStable, DateTime ConnectTime)> _connectionStability = new();
    
    // Constants for session management
    private const int MAX_VARIABLES_PER_SESSION = 2000; // Limit variables to prevent memory issues
    private const int SESSION_IDLE_TIMEOUT_MINUTES = 60; // Clean up sessions idle for 60 minutes
    private static readonly object _cleanupLock = new object();
    private static DateTime _lastCleanupTime = DateTime.UtcNow;

    // Ground state constants
    private const double GROUND_STATE_THRESHOLD = 0.1; // Threshold for considering ground state change
    private const int MIN_GROUND_STATE_UPDATE_MS = 500; // Minimum time between any ground state updates
    private const int MIN_GROUND_STATE_CHANGE_MS = 1000; // Minimum time between actual ground state changes

    // Speed data constants
    private const double SPEED_CHANGE_THRESHOLD = 0.5; // Threshold for considering speed change
    private const int MIN_SPEED_UPDATE_MS = 100; // Minimum time between speed updates
    
    // Connection stability constants
    private const int CONNECTION_STABILIZATION_MS = 2000; // Time to wait before considering a connection stable
    private const int CONTROL_TRANSFER_TIMEOUT_MS = 5000; // Time to wait for a control transfer to complete before timing out

    public DynamicVariableHub(ILogger<DynamicVariableHub> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Join a specific session by code
    /// </summary>
    public async Task JoinSession(string sessionCode)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionCode))
            {
                _logger.LogWarning("Client {ConnectionId} attempted to join with empty session code", Context.ConnectionId);
                return;
            }

            _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
            
            // Mark connection as unstable initially
            _connectionStability[Context.ConnectionId] = (false, DateTime.UtcNow);
            
            // Add client to the session group
            await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
            
            // Ensure session collections exist and add the connection
            var sessionConnections = _sessionConnections.GetOrAdd(sessionCode, 
                _ => new ConcurrentDictionary<string, DateTime>());
            sessionConnections[Context.ConnectionId] = DateTime.UtcNow;
            
            // Initialize variable storage for session if needed
            _sessionVariableValues.GetOrAdd(sessionCode, 
                _ => new ConcurrentDictionary<string, VariableChangeDto>());
            
            // Update session activity timestamp
            _sessionLastActivity[sessionCode] = DateTime.UtcNow;
            
            // Wait for connection to stabilize before processing control
            await Task.Delay(500);
            
            // Determine control status - first client gets control
            bool hasControl = false;
            
            // Only attempt to claim control if there's no pending transfer
            if (!_controlTransferLocks.ContainsKey(sessionCode))
            {
                if (!_sessionControlMap.ContainsKey(sessionCode))
                {
                    if (_sessionControlMap.TryAdd(sessionCode, Context.ConnectionId))
                    {
                        hasControl = true;
                        _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", 
                            Context.ConnectionId, sessionCode);
                    }
                }
                else
                {
                    hasControl = _sessionControlMap[sessionCode] == Context.ConnectionId;
                }
            }
            else
            {
                _logger.LogInformation("Client {ConnectionId} joined during a control transfer, waiting for completion", 
                    Context.ConnectionId);
                
                // Wait for pending transfer to complete
                await Task.Delay(100);
                
                // Check again
                hasControl = _sessionControlMap.TryGetValue(sessionCode, out var controlId) && 
                             controlId == Context.ConnectionId;
            }
            
            await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
            _logger.LogInformation("Notified joining client {ClientId} of control status ({HasControl}) in session {SessionCode}", 
                Context.ConnectionId, hasControl, sessionCode);
            
            // Mark connection as stable after a delay
            Task.Delay(CONNECTION_STABILIZATION_MS).ContinueWith(_ => {
                if (_connectionStability.TryGetValue(Context.ConnectionId, out var stability))
                {
                    _connectionStability[Context.ConnectionId] = (true, stability.ConnectTime);
                    _logger.LogInformation("Connection {ConnectionId} is now considered stable", Context.ConnectionId);
                }
            });
            
            // Send existing variables to new client
            await SyncVariablesToClient(sessionCode);
            
            // Periodically check for idle sessions
            CheckAndCleanupIdleSessions();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in JoinSession for client {ConnectionId}, session {SessionCode}", 
                Context.ConnectionId, sessionCode);
        }
    }
    
    /// <summary>
    /// Send position data from controller to other clients
    /// </summary>
    public async Task SendAircraftPosition(string sessionCode, AircraftPositionData data)
    {
        try
        {
            // Only the controller can send position data
            if (_sessionControlMap.TryGetValue(sessionCode, out string controlId) && 
                controlId == Context.ConnectionId)
            {
                // Validate and sanitize the data before processing
                SanitizeAircraftData(ref data);
                
                // Get current ground state for this session
                var groundState = _sessionGroundStates.GetOrAdd(sessionCode, (0.0, DateTime.UtcNow));
                
                // Get current speed state for this session
                var speedState = _sessionSpeedStates.GetOrAdd(sessionCode, (0.0, 0.0, 0.0, DateTime.UtcNow));
                
                // Check if we should update the ground state
                bool shouldUpdateGroundState = true;
                
                // If we have a previous ground state
                if (groundState.LastGroundState != 0)
                {
                    // Calculate time since last update
                    var timeSinceLastUpdate = DateTime.UtcNow - groundState.LastUpdate;
                    
                    // If less than minimum time has passed, don't update ground state
                    if (timeSinceLastUpdate.TotalMilliseconds < MIN_GROUND_STATE_UPDATE_MS)
                    {
                        shouldUpdateGroundState = false;
                    }
                    
                    // Normalize to binary values (0 or 1) to prevent odd values
                    double normalizedNewGroundState = data.OnGround > 0.5 ? 1.0 : 0.0;
                    double normalizedLastGroundState = groundState.LastGroundState > 0.5 ? 1.0 : 0.0;
                    
                    // If ground state changed significantly, require minimum time between changes
                    if (normalizedNewGroundState != normalizedLastGroundState)
                    {
                        if (timeSinceLastUpdate.TotalMilliseconds < MIN_GROUND_STATE_CHANGE_MS)
                        {
                            shouldUpdateGroundState = false;
                        }
                    }
                }
                
                // Update ground state if needed
                if (shouldUpdateGroundState)
                {
                    // Normalize to binary value
                    double normalizedGroundState = data.OnGround > 0.5 ? 1.0 : 0.0;
                    _sessionGroundStates[sessionCode] = (normalizedGroundState, DateTime.UtcNow);
                    
                    _logger.LogDebug("Updated ground state in session {SessionCode}: {GroundState}", 
                        sessionCode, normalizedGroundState);
                }
                else
                {
                    // Use previous ground state
                    data.OnGround = groundState.LastGroundState;
                    _logger.LogDebug("Using previous ground state in session {SessionCode}: {GroundState}", 
                        sessionCode, data.OnGround);
                }
                
                // Handle speed data updates
                var timeSinceLastSpeedUpdate = DateTime.UtcNow - speedState.LastUpdate;
                
                // Only update speed data if enough time has passed
                if (timeSinceLastSpeedUpdate.TotalMilliseconds >= MIN_SPEED_UPDATE_MS)
                {
                    // Update speed state
                    _sessionSpeedStates[sessionCode] = (
                        data.GroundSpeed,
                        data.AirspeedTrue,
                        data.AirspeedIndicated,
                        DateTime.UtcNow
                    );
                    
                    _logger.LogDebug("Updated speed data in session {SessionCode}: GS={GroundSpeed}, TAS={TrueAirspeed}, IAS={IndicatedAirspeed}", 
                        sessionCode, data.GroundSpeed, data.AirspeedTrue, data.AirspeedIndicated);
                }
                else
                {
                    // Use previous speed data
                    data.GroundSpeed = speedState.LastGroundSpeed;
                    data.AirspeedTrue = speedState.LastAirspeedTrue;
                    data.AirspeedIndicated = speedState.LastAirspeedIndicated;
                    
                    _logger.LogDebug("Using previous speed data in session {SessionCode}: GS={GroundSpeed}, TAS={TrueAirspeed}, IAS={IndicatedAirspeed}", 
                        sessionCode, data.GroundSpeed, data.AirspeedTrue, data.AirspeedIndicated);
                }
                
                // Set timestamp for data age tracking
                data.TimeStamp = DateTime.UtcNow;
                
                // Update session activity timestamp
                _sessionLastActivity[sessionCode] = DateTime.UtcNow;
                
                // Log only at debug level to avoid log flooding
                _logger.LogDebug("Received position data in session {SessionCode}: Alt={Alt:F1}, Gs={Gs:F1}, OnGround={OnGround}", 
                    sessionCode, data.Altitude, data.GroundSpeed, data.OnGround);
                    
                // Send to others in the session
                await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftPosition", data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing aircraft position data in session {SessionCode}", sessionCode);
        }
    }
    
    /// <summary>
    /// Validate and sanitize aircraft data to prevent invalid values
    /// </summary>
    private void SanitizeAircraftData(ref AircraftPositionData data)
    {
        // Validate ground state - ensure it's a valid double value
        if (double.IsNaN(data.OnGround) || double.IsInfinity(data.OnGround))
        {
            _logger.LogWarning("Received invalid ground state value: {GroundState}, sanitizing to 0", data.OnGround);
            data.OnGround = 0.0;
        }
        
        // Normalize ground state to binary value (0.0 or 1.0)
        data.OnGround = data.OnGround > 0.5 ? 1.0 : 0.0;
        
        // Validate and sanitize speed data
        SanitizeSpeedValue(ref data.GroundSpeed, "Ground Speed");
        SanitizeSpeedValue(ref data.AirspeedTrue, "True Airspeed");
        SanitizeSpeedValue(ref data.AirspeedIndicated, "Indicated Airspeed");
        
        // Ensure speed values are consistent
        EnsureSpeedConsistency(ref data);
        
        // Log speed values for debugging
        _logger.LogDebug("Sanitized speeds - GS: {GroundSpeed:F1}, TAS: {TrueAirspeed:F1}, IAS: {IndicatedAirspeed:F1}",
            data.GroundSpeed, data.AirspeedTrue, data.AirspeedIndicated);
    }
    
    private void SanitizeSpeedValue(ref double speed, string speedName)
    {
        // Handle invalid values
        if (double.IsNaN(speed) || double.IsInfinity(speed))
        {
            _logger.LogWarning("Received invalid {SpeedName} value: {Speed}, sanitizing to 0", speedName, speed);
            speed = 0.0;
            return;
        }
        
        // Ensure positive value
        speed = Math.Abs(speed);
        
        // Cap at reasonable limit
        const double MAX_SPEED = 2000.0; // Max reasonable speed in knots
        if (speed > MAX_SPEED)
        {
            _logger.LogWarning("{SpeedName} value {Speed} exceeds maximum, capping at {MaxSpeed}", 
                speedName, speed, MAX_SPEED);
            speed = MAX_SPEED;
        }
    }
    
    private void EnsureSpeedConsistency(ref AircraftPositionData data)
    {
        // If we're on the ground, ground speed should be close to true airspeed
        if (data.OnGround > 0.5)
        {
            // If ground speed is very low but TAS is high, something's wrong
            if (data.GroundSpeed < 1.0 && data.AirspeedTrue > 10.0)
            {
                _logger.LogWarning("Inconsistent speeds on ground - GS: {GroundSpeed}, TAS: {TrueAirspeed}, adjusting",
                    data.GroundSpeed, data.AirspeedTrue);
                data.AirspeedTrue = data.GroundSpeed;
            }
        }
        
        // True airspeed should never be less than indicated airspeed
        if (data.AirspeedTrue < data.AirspeedIndicated)
        {
            _logger.LogWarning("True airspeed ({TrueAirspeed}) less than indicated airspeed ({IndicatedAirspeed}), adjusting",
                data.AirspeedTrue, data.AirspeedIndicated);
            data.AirspeedTrue = data.AirspeedIndicated;
        }
        
        // Ground speed should never be significantly higher than true airspeed
        if (data.GroundSpeed > data.AirspeedTrue * 1.5)
        {
            _logger.LogWarning("Ground speed ({GroundSpeed}) significantly higher than true airspeed ({TrueAirspeed}), adjusting",
                data.GroundSpeed, data.AirspeedTrue);
            data.GroundSpeed = data.AirspeedTrue;
        }
    }
    
    /// <summary>
    /// Receive and broadcast variable changes within a session
    /// </summary>
    public async Task SendVariableChange(string sessionCode, VariableChangeDto change)
    {
        try
        {
            // Set source client ID to detect and prevent echo loops
            change.SourceClientId = Context.ConnectionId;
            change.Timestamp = DateTime.UtcNow;
            
            // Update session activity timestamp
            _sessionLastActivity[sessionCode] = DateTime.UtcNow;
            
            _logger.LogInformation("Received variable change in session {SessionCode}: {Variable}={Value}", 
                sessionCode, change.VariableName, change.Value);
            
            // Get or create variable storage for this session
            var sessionVariables = _sessionVariableValues.GetOrAdd(sessionCode, 
                _ => new ConcurrentDictionary<string, VariableChangeDto>());
            
            // Check variable count limit to prevent memory issues
            if (sessionVariables.Count >= MAX_VARIABLES_PER_SESSION && 
                !sessionVariables.ContainsKey(change.VariableName))
            {
                _logger.LogWarning("Session {SessionCode} reached variable limit ({Limit}), ignoring new variable {Variable}", 
                    sessionCode, MAX_VARIABLES_PER_SESSION, change.VariableName);
                return;
            }
            
            // Only store the value if it's not a broadcast (to avoid feedback loops)
            if (!change.IsBroadcast)
            {
                sessionVariables[change.VariableName] = change;
            }
            
            // Set broadcast flag to prevent echo loops when received on the other end
            change.IsBroadcast = true;
            
            // Send to others in the group
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveVariableChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing variable change for {Variable} in session {SessionCode}", 
                change?.VariableName, sessionCode);
        }
    }
    
    /// <summary>
    /// Synchronize all session variables to the requesting client
    /// </summary>
    public async Task RequestVariableSync(string sessionCode)
    {
        try
        {
            await SyncVariablesToClient(sessionCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error synchronizing variables to client {ConnectionId} in session {SessionCode}", 
                Context.ConnectionId, sessionCode);
        }
    }
    
    /// <summary>
    /// Helper method to synchronize variables to a client
    /// </summary>
    private async Task SyncVariablesToClient(string sessionCode)
    {
        if (_sessionVariableValues.TryGetValue(sessionCode, out var variables) && variables.Count > 0)
        {
            // Get a snapshot of the variables to avoid issues with concurrent modifications
            var variableSnapshot = variables.Values.ToArray();
            
            // Send variables in batches to avoid overwhelming client
            const int batchSize = 25;
            for (int i = 0; i < variableSnapshot.Length; i += batchSize)
            {
                var batch = variableSnapshot.Skip(i).Take(batchSize);
                foreach (var variable in batch)
                {
                    await Clients.Caller.SendAsync("ReceiveVariableChange", variable);
                }
                
                // Small delay between batches to avoid overwhelming client
                if (i + batchSize < variableSnapshot.Length)
                {
                    await Task.Delay(50);
                }
            }
            
            _logger.LogInformation("Sent {Count} variables to client {ClientId} in session {SessionCode}", 
                variableSnapshot.Length, Context.ConnectionId, sessionCode);
        }
    }
    
    /// <summary>
    /// Transfer control between clients
    /// </summary>
    public async Task TransferControl(string sessionCode, bool giving)
    {
        try
        {
            // Check if connection is stable
            if (!IsConnectionStable(Context.ConnectionId))
            {
                _logger.LogWarning("Client {ConnectionId} tried to transfer control while connection is not stable", 
                    Context.ConnectionId);
                return;
            }
            
            // Check for ongoing transfer
            if (_controlTransferLocks.TryGetValue(sessionCode, out var lockInfo))
            {
                // If this is the same client that initiated the lock
                if (lockInfo.PendingId == Context.ConnectionId)
                {
                    // Check if lock has expired
                    if ((DateTime.UtcNow - lockInfo.LockTime).TotalMilliseconds > CONTROL_TRANSFER_TIMEOUT_MS)
                    {
                        // Release the expired lock
                        _controlTransferLocks.TryRemove(sessionCode, out _);
                        _logger.LogWarning("Control transfer lock for session {SessionCode} expired and was removed", 
                            sessionCode);
                    }
                    else
                    {
                        // Still locked by this client, allow the operation to proceed
                    }
                }
                else
                {
                    // Another transfer is in progress
                    _logger.LogWarning("Client {ConnectionId} tried to transfer control while another transfer is in progress", 
                        Context.ConnectionId);
                    return;
                }
            }
            
            // Safely try to get the current controller
            if (!_sessionControlMap.TryGetValue(sessionCode, out string currentController))
            {
                // If no controller exists, this client can take control
                currentController = string.Empty;
            }
            
            // Update session activity timestamp
            _sessionLastActivity[sessionCode] = DateTime.UtcNow;
            
            // Create a lock for this transfer
            _controlTransferLocks[sessionCode] = (Context.ConnectionId, DateTime.UtcNow);
            
            try
            {
                if (giving)
                {
                    // Only the current controller can give control
                    if (currentController == Context.ConnectionId)
                    {
                        // Try to find another connection to transfer control to
                        if (_sessionConnections.TryGetValue(sessionCode, out var connections))
                        {
                            var otherConnections = connections.Keys
                                .Where(id => id != Context.ConnectionId && IsConnectionStable(id))
                                .ToList();
                                
                            if (otherConnections.Any())
                            {
                                var newController = otherConnections.First();
                                // Atomically update the control map
                                if (_sessionControlMap.TryUpdate(sessionCode, newController, currentController))
                                {
                                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                                    _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", 
                                        Context.ConnectionId, newController, sessionCode);
                                }
                            }
                        }
                    }
                }
                else // Taking control
                {
                    // Client is taking control and is not currently the controller
                    if (currentController != Context.ConnectionId)
                    {
                        string oldController = currentController;
                        // Atomically update the control map
                        if (string.IsNullOrEmpty(currentController))
                        {
                            // No current controller - add a new entry
                            if (_sessionControlMap.TryAdd(sessionCode, Context.ConnectionId))
                            {
                                await Clients.Caller.SendAsync("ControlStatusChanged", true);
                                _logger.LogInformation("Control taken by {NewId} in session {SessionCode} (no previous controller)", 
                                    Context.ConnectionId, sessionCode);
                            }
                        }
                        else
                        {
                            // Check if the current controller is still connected and stable
                            bool controllerIsActive = _sessionConnections.TryGetValue(sessionCode, out var connections) && 
                                                   connections.ContainsKey(oldController) &&
                                                   IsConnectionStable(oldController);
                                                   
                            if (!controllerIsActive)
                            {
                                // Previous controller is gone or unstable, force the change
                                _sessionControlMap[sessionCode] = Context.ConnectionId;
                                await Clients.Caller.SendAsync("ControlStatusChanged", true);
                                _logger.LogInformation("Control forcibly taken by {NewId} from inactive controller {OldId} in session {SessionCode}", 
                                    Context.ConnectionId, oldController, sessionCode);
                            }
                            else
                            {
                                // Replace existing controller
                                if (_sessionControlMap.TryUpdate(sessionCode, Context.ConnectionId, currentController))
                                {
                                    await Clients.Caller.SendAsync("ControlStatusChanged", true);
                                    if (!string.IsNullOrEmpty(oldController))
                                    {
                                        await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                                    }
                                    _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", 
                                        Context.ConnectionId, oldController, sessionCode);
                                }
                            }
                        }
                    }
                }
            }
            finally
            {
                // Always remove the lock when done
                _controlTransferLocks.TryRemove(sessionCode, out _);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error transferring control in session {SessionCode}", sessionCode);
            
            // Clean up lock in case of exception
            _controlTransferLocks.TryRemove(sessionCode, out _);
        }
    }
    
    /// <summary>
    /// Check if a connection is considered stable
    /// </summary>
    private bool IsConnectionStable(string connectionId)
    {
        if (_connectionStability.TryGetValue(connectionId, out var stability))
        {
            if (stability.IsStable)
                return true;
                
            // Check if enough time has passed to consider it stable anyway
            return (DateTime.UtcNow - stability.ConnectTime).TotalMilliseconds >= CONNECTION_STABILIZATION_MS;
        }
        
        return false;
    }
    
    /// <summary>
    /// Get statistics about the current session
    /// </summary>
    public SessionStats GetSessionStats(string sessionCode)
    {
        try
        {
            var stats = new SessionStats
            {
                SessionCode = sessionCode,
                ConnectionCount = 0,
                ControllerConnectionId = _sessionControlMap.TryGetValue(sessionCode, out string controller) ? controller : "None",
                LastActivity = _sessionLastActivity.TryGetValue(sessionCode, out DateTime lastActivity) ? lastActivity : DateTime.MinValue,
                VariableCount = 0
            };
            
            if (_sessionConnections.TryGetValue(sessionCode, out var connections))
            {
                stats.ConnectionCount = connections.Count;
            }
            
            if (_sessionVariableValues.TryGetValue(sessionCode, out var variables))
            {
                stats.VariableCount = variables.Count;
            }
            
            return stats;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting session stats for {SessionCode}", sessionCode);
            return new SessionStats { SessionCode = sessionCode };
        }
    }
    
    /// <summary>
    /// Handle client disconnection
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        try
        {
            _logger.LogInformation("Client {ConnectionId} disconnected. Reason: {Reason}", 
                Context.ConnectionId, exception?.Message ?? "Unknown");
                
            // Remove connection stability tracking
            _connectionStability.TryRemove(Context.ConnectionId, out _);
                
            // Find all sessions this client is part of
            var sessionsToCheck = _sessionConnections.Where(kvp => 
                    kvp.Value.ContainsKey(Context.ConnectionId))
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var sessionCode in sessionsToCheck)
            {
                // Update session activity timestamp
                _sessionLastActivity[sessionCode] = DateTime.UtcNow;
                
                // Remove the connection from session
                if (_sessionConnections.TryGetValue(sessionCode, out var connections))
                {
                    connections.TryRemove(Context.ConnectionId, out _);
                    
                    // Clear any pending control transfer locks by this client
                    if (_controlTransferLocks.TryGetValue(sessionCode, out var lockInfo) && 
                        lockInfo.PendingId == Context.ConnectionId)
                    {
                        _controlTransferLocks.TryRemove(sessionCode, out _);
                    }
                    
                    // If this client had control, transfer it
                    if (_sessionControlMap.TryGetValue(sessionCode, out string controlId) && 
                        controlId == Context.ConnectionId)
                    {
                        if (connections.Count > 0)
                        {
                            // Find a new controller - take the first stable connection
                            var stableConnections = connections.Keys
                                .Where(IsConnectionStable)
                                .ToList();
                                
                            if (stableConnections.Any())
                            {
                                var newController = stableConnections.First();
                                
                                // Wait a moment before transferring control to prevent race conditions
                                await Task.Delay(500);
                                
                                // Atomically update the controller if it hasn't changed
                                if (_sessionControlMap.TryGetValue(sessionCode, out var currentControlId) && 
                                    currentControlId == Context.ConnectionId)
                                {
                                    if (_sessionControlMap.TryUpdate(sessionCode, newController, Context.ConnectionId))
                                    {
                                        await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                                        _logger.LogInformation("Control automatically transferred to {NewId} in session {SessionCode} after disconnect", 
                                            newController, sessionCode);
                                    }
                                }
                            }
                            else if (connections.Count > 0) 
                            {
                                // No stable connections yet, but there are connections
                                // Wait a bit and try again with any connection
                                await Task.Delay(1000);
                                
                                if (connections.Count > 0 && 
                                    _sessionControlMap.TryGetValue(sessionCode, out var currentControlId) && 
                                    currentControlId == Context.ConnectionId)
                                {
                                    var newController = connections.Keys.First();
                                    if (_sessionControlMap.TryUpdate(sessionCode, newController, Context.ConnectionId))
                                    {
                                        await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                                        _logger.LogInformation("Control automatically transferred to {NewId} in session {SessionCode} after disconnect (no stable connections)", 
                                            newController, sessionCode);
                                    }
                                }
                            }
                        }
                        else
                        {
                            // No other connections - remove session data
                            _sessionControlMap.TryRemove(sessionCode, out _);
                            _sessionConnections.TryRemove(sessionCode, out _);
                            _sessionVariableValues.TryRemove(sessionCode, out _);
                            _sessionLastActivity.TryRemove(sessionCode, out _);
                            _sessionGroundStates.TryRemove(sessionCode, out _); 
                            _sessionSpeedStates.TryRemove(sessionCode, out _); 
                            _controlTransferLocks.TryRemove(sessionCode, out _);
                            _logger.LogInformation("Session {SessionCode} removed as all clients disconnected", sessionCode);
                        }
                    }
                    
                    // If no more connections, clean up the session
                    if (connections.Count == 0)
                    {
                        _sessionConnections.TryRemove(sessionCode, out _);
                        _sessionControlMap.TryRemove(sessionCode, out _);
                        _sessionVariableValues.TryRemove(sessionCode, out _);
                        _sessionLastActivity.TryRemove(sessionCode, out _);
                        _sessionGroundStates.TryRemove(sessionCode, out _); 
                        _sessionSpeedStates.TryRemove(sessionCode, out _); 
                        _controlTransferLocks.TryRemove(sessionCode, out _);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling disconnect for client {ConnectionId}", Context.ConnectionId);
        }
        
        await base.OnDisconnectedAsync(exception);
    }
    
    /// <summary>
    /// Periodically check for and clean up idle sessions to prevent memory leaks
    /// </summary>
    private void CheckAndCleanupIdleSessions()
    {
        // Use a lock to ensure only one cleanup operation runs at a time
        if (!Monitor.TryEnter(_cleanupLock))
            return;
        
        try
        {
            var now = DateTime.UtcNow;
            
            // Only run cleanup every 10 minutes
            if ((now - _lastCleanupTime).TotalMinutes < 10)
                return;
                
            _lastCleanupTime = now;
            
            // Find sessions that have been inactive for too long
            var idleSessions = _sessionLastActivity
                .Where(kvp => (now - kvp.Value).TotalMinutes > SESSION_IDLE_TIMEOUT_MINUTES)
                .Select(kvp => kvp.Key)
                .ToList();
                
            foreach (var sessionCode in idleSessions)
            {
                _logger.LogInformation("Cleaning up idle session {SessionCode} (inactive for {MinutesInactive} minutes)",
                    sessionCode, (now - _sessionLastActivity[sessionCode]).TotalMinutes);
                    
                // Remove session data
                _sessionLastActivity.TryRemove(sessionCode, out _);
                _sessionControlMap.TryRemove(sessionCode, out _);
                _sessionConnections.TryRemove(sessionCode, out _);
                _sessionVariableValues.TryRemove(sessionCode, out _);
            }
            
            if (idleSessions.Count > 0)
            {
                _logger.LogInformation("Cleaned up {Count} idle sessions", idleSessions.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during idle session cleanup");
        }
        finally
        {
            Monitor.Exit(_cleanupLock);
        }
    }
}