using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

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
    
    // Keep track of who is the host (first client) for each session
    private static readonly ConcurrentDictionary<string, string> _sessionHostMap = new();
    
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

    // Add these new rate limiting fields to the class
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _clientRateLimits = new();
    private const int GLOBAL_RATE_LIMIT_MS = 250; // Global minimum time between any messages from the same client
    private const int CONNECTION_RATE_LIMIT_MS = 500; // Minimum time between connection change messages
    private const int CONTROL_ACTION_RATE_LIMIT_MS = 3000; // Minimum time between control take/give actions

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

            // Check rate limiting
            if (IsRateLimited(sessionCode, Context.ConnectionId, "connection"))
            {
                _logger.LogWarning("Rate limited connection attempt from {ConnectionId} to session {SessionCode}", 
                    Context.ConnectionId, sessionCode);
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
            
            // Determine if this is the host (first client) for this session
            bool isHost = false;
            
            // If no host is set for this session yet, this client becomes the host
            if (!_sessionHostMap.ContainsKey(sessionCode))
            {
                if (_sessionHostMap.TryAdd(sessionCode, Context.ConnectionId))
                {
                    isHost = true;
                    _logger.LogInformation("Client {ConnectionId} is the host for session {SessionCode}", 
                        Context.ConnectionId, sessionCode);
                }
            }
            else
            {
                // Check if this client is the host
                isHost = _sessionHostMap[sessionCode] == Context.ConnectionId;
            }
            
            // Determine control status based on host status
            bool hasControl = false;
            
            // Only attempt to update control if there's no pending transfer
            if (!_controlTransferLocks.ContainsKey(sessionCode))
            {
                if (isHost)
                {
                    // Host should always have control unless they explicitly gave it up
                    if (!_sessionControlMap.ContainsKey(sessionCode))
                    {
                        // No one has control yet, give it to the host
                        if (_sessionControlMap.TryAdd(sessionCode, Context.ConnectionId))
                        {
                            hasControl = true;
                            _logger.LogInformation("Host {ControlId} assigned initial control in session {SessionCode}", 
                                Context.ConnectionId, sessionCode);
                        }
                    }
                    else if (_sessionControlMap[sessionCode] != Context.ConnectionId)
                    {
                        // If host is reconnecting, they should get control back
                        string previousController = _sessionControlMap[sessionCode];
                        
                        // Check if previous controller is still connected
                        bool previousControllerConnected = sessionConnections.ContainsKey(previousController);
                        
                        if (!previousControllerConnected || 
                            (previousControllerConnected && _connectionStability.TryGetValue(previousController, out var stability) && !stability.IsStable))
                        {
                            // Previous controller is gone or unstable, host takes back control
                            _sessionControlMap[sessionCode] = Context.ConnectionId;
                            hasControl = true;
                            _logger.LogInformation("Host {ControlId} reclaimed control in session {SessionCode}", 
                                Context.ConnectionId, sessionCode);
                                
                            // If previous controller is still here, notify them of lost control
                            if (previousControllerConnected)
                            {
                                await Clients.Client(previousController).SendAsync("ControlStatusChanged", false);
                            }
                        }
                        else
                        {
                            // Previous controller is still connected and stable
                            // The host doesn't automatically take control back
                            hasControl = false;
                            _logger.LogInformation("Host {ControlId} joined but another client {CurrentController} has control in session {SessionCode}", 
                                Context.ConnectionId, previousController, sessionCode);
                        }
                    }
                    else
                    {
                        // Host already has control
                        hasControl = true;
                    }
                }
                else
                {
                    // Non-host clients don't get control automatically
                    // They only have control if they already had it before (e.g., the host gave it to them)
                    hasControl = _sessionControlMap.TryGetValue(sessionCode, out string controlId) && 
                                 controlId == Context.ConnectionId;
                                 
                    if (hasControl)
                    {
                        _logger.LogInformation("Client {ClientId} rejoined with existing control status in session {SessionCode}", 
                            Context.ConnectionId, sessionCode);
                    }
                    else
                    {
                        _logger.LogInformation("Client {ClientId} joined without control in session {SessionCode}", 
                            Context.ConnectionId, sessionCode);
                    }
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
                    var timeSinceLastUpdate = (DateTime.UtcNow - groundState.LastUpdate).TotalMilliseconds;
                    
                    // Limit ground state updates based on timing
                    if (timeSinceLastUpdate < MIN_GROUND_STATE_UPDATE_MS)
                    {
                        shouldUpdateGroundState = false;
                    }
                    // Also limit based on ground state change thresholds
                    else if (Math.Abs(data.OnGround - groundState.LastGroundState) < GROUND_STATE_THRESHOLD)
                    {
                        shouldUpdateGroundState = false;
                    }
                    // If this is an actual change in ground state, enforce a longer delay
                    else if (Math.Abs(data.OnGround - groundState.LastGroundState) >= GROUND_STATE_THRESHOLD && 
                            timeSinceLastUpdate < MIN_GROUND_STATE_CHANGE_MS)
                    {
                        shouldUpdateGroundState = false;
                    }
                }
                
                // Update the ground state if needed
                if (shouldUpdateGroundState)
                {
                    _sessionGroundStates[sessionCode] = (data.OnGround, DateTime.UtcNow);
                }
                else
                {
                    // Use the existing ground state
                    data.OnGround = groundState.LastGroundState;
                }
                
                // Similarly check and update speed state
                var timeSinceLastSpeedUpdate = (DateTime.UtcNow - speedState.LastUpdate).TotalMilliseconds;
                if (timeSinceLastSpeedUpdate >= MIN_SPEED_UPDATE_MS ||
                    Math.Abs(data.GroundSpeed - speedState.LastGroundSpeed) >= SPEED_CHANGE_THRESHOLD ||
                    Math.Abs(data.AirspeedTrue - speedState.LastAirspeedTrue) >= SPEED_CHANGE_THRESHOLD ||
                    Math.Abs(data.AirspeedIndicated - speedState.LastAirspeedIndicated) >= SPEED_CHANGE_THRESHOLD)
                {
                    // Update the speed state
                    _sessionSpeedStates[sessionCode] = (data.GroundSpeed, data.AirspeedTrue, data.AirspeedIndicated, DateTime.UtcNow);
                }
                else
                {
                    // Use existing speed values
                    data.GroundSpeed = speedState.LastGroundSpeed;
                    data.AirspeedTrue = speedState.LastAirspeedTrue;
                    data.AirspeedIndicated = speedState.LastAirspeedIndicated;
                }
                
                // Update session activity timestamp
                _sessionLastActivity[sessionCode] = DateTime.UtcNow;
                
                try 
                {
                    // Use JsonSerializerOptions to handle infinity values
                    var jsonOptions = new System.Text.Json.JsonSerializerOptions
                    {
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                    };
                    
                    // Send to all others in the group
                    await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftPosition", data, jsonOptions);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogError(ex, "Error serializing aircraft position data");
                    
                    // Additional cleanup in case of JSON serialization error - check for invalid values
                    if (double.IsInfinity(data.GroundSpeed) || double.IsNaN(data.GroundSpeed)) data.GroundSpeed = 0;
                    if (double.IsInfinity(data.AirspeedTrue) || double.IsNaN(data.AirspeedTrue)) data.AirspeedTrue = 0;
                    if (double.IsInfinity(data.AirspeedIndicated) || double.IsNaN(data.AirspeedIndicated)) data.AirspeedIndicated = 0;
                    
                    // Try again with sanitized data
                    try
                    {
                        var jsonOptions = new System.Text.Json.JsonSerializerOptions
                        {
                            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
                        };
                        
                        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftPosition", data, jsonOptions);
                    }
                    catch (Exception retryEx)
                    {
                        _logger.LogError(retryEx, "Failed to send aircraft position data even after sanitizing values");
                    }
                }
            }
            else
            {
                _logger.LogWarning("Client {ClientId} tried to send position data without control in session {SessionCode}", 
                    Context.ConnectionId, sessionCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending aircraft position for session {SessionCode}", sessionCode);
        }
    }
    
    /// <summary>
    /// Validate and sanitize aircraft data to prevent invalid values
    /// </summary>
    private void SanitizeAircraftData(ref AircraftPositionData data)
    {
        // Ensure onGround is either 0 or 1
        data.OnGround = data.OnGround > 0.5 ? 1.0 : 0.0;
        
        // Validate and sanitize speed data - use temporary variables
        double groundSpeed = data.GroundSpeed;
        double airspeedTrue = data.AirspeedTrue;
        double airspeedIndicated = data.AirspeedIndicated;
        
        SanitizeSpeedValue(ref groundSpeed, "Ground Speed");
        SanitizeSpeedValue(ref airspeedTrue, "True Airspeed");
        SanitizeSpeedValue(ref airspeedIndicated, "Indicated Airspeed");
        
        // Assign back to the properties
        data.GroundSpeed = groundSpeed;
        data.AirspeedTrue = airspeedTrue;
        data.AirspeedIndicated = airspeedIndicated;
        
        // Ensure speed values are consistent by using temp variables
        EnsureSpeedConsistencyImpl(ref groundSpeed, ref airspeedTrue, ref airspeedIndicated, data.OnGround);
        
        // Copy back to data object
        data.GroundSpeed = groundSpeed;
        data.AirspeedTrue = airspeedTrue;
        data.AirspeedIndicated = airspeedIndicated;
        
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
    
    // Add a new implementation that works with ref variables directly
    private void EnsureSpeedConsistencyImpl(ref double groundSpeed, ref double airspeedTrue, ref double airspeedIndicated, double onGround)
    {
        // If we're on the ground, ground speed should be close to true airspeed
        if (onGround > 0.5)
        {
            // If ground speed is very low but TAS is high, something's wrong
            if (groundSpeed < 1.0 && airspeedTrue > 10.0)
            {
                _logger.LogWarning("Inconsistent speeds on ground - GS: {GroundSpeed}, TAS: {TrueAirspeed}, adjusting",
                    groundSpeed, airspeedTrue);
                airspeedTrue = groundSpeed;
            }
        }
        
        // True airspeed should never be less than indicated airspeed
        if (airspeedTrue < airspeedIndicated)
        {
            _logger.LogWarning("True airspeed ({TrueAirspeed}) less than indicated airspeed ({IndicatedAirspeed}), adjusting",
                airspeedTrue, airspeedIndicated);
            airspeedTrue = airspeedIndicated;
        }
        
        // Ground speed should never be significantly higher than true airspeed
        if (groundSpeed > airspeedTrue * 1.5)
        {
            _logger.LogWarning("Ground speed ({GroundSpeed}) significantly higher than true airspeed ({TrueAirspeed}), adjusting",
                groundSpeed, airspeedTrue);
            groundSpeed = airspeedTrue;
        }
    }
    
    // Keep original method for backwards compatibility
    private void EnsureSpeedConsistency(ref AircraftPositionData data)
    {
        double groundSpeed = data.GroundSpeed;
        double airspeedTrue = data.AirspeedTrue;
        double airspeedIndicated = data.AirspeedIndicated;
        
        EnsureSpeedConsistencyImpl(ref groundSpeed, ref airspeedTrue, ref airspeedIndicated, data.OnGround);
        
        data.GroundSpeed = groundSpeed;
        data.AirspeedTrue = airspeedTrue;
        data.AirspeedIndicated = airspeedIndicated;
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
            // Check rate limiting
            if (IsRateLimited(sessionCode, Context.ConnectionId, "control"))
            {
                _logger.LogWarning("Rate limited control transfer attempt from {ConnectionId} in session {SessionCode}", 
                    Context.ConnectionId, sessionCode);
                return;
            }

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
            
            // Check if this client is the host for this session
            bool isHost = _sessionHostMap.TryGetValue(sessionCode, out string hostId) && 
                           hostId == Context.ConnectionId;
            
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
                                // If the host is in the list of other connections, prioritize giving control to them
                                var newController = isHost ? 
                                    otherConnections.First() : 
                                    (otherConnections.Contains(hostId) ? hostId : otherConnections.First());
                                
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
                        
                        // If the host is taking control, they get priority
                        bool allowTakeControl = isHost || string.IsNullOrEmpty(currentController);
                        
                        if (!isHost && !string.IsNullOrEmpty(currentController))
                        {
                            // Non-host can only take control from a non-host or an inactive controller
                            bool currentControllerIsHost = currentController == hostId;
                            bool controllerIsActive = _sessionConnections.TryGetValue(sessionCode, out var connections) && 
                                                    connections.ContainsKey(oldController) &&
                                                    IsConnectionStable(oldController);
                                                    
                            allowTakeControl = !currentControllerIsHost || !controllerIsActive;
                        }
                        
                        if (allowTakeControl)
                        {
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
                                        
                                        string takeType = isHost ? " (host reclaiming control)" : "";
                                        _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}{TakeType}", 
                                            Context.ConnectionId, oldController, sessionCode, takeType);
                                    }
                                }
                            }
                        }
                        else
                        {
                            // Client cannot take control
                            if (isHost)
                            {
                                _logger.LogWarning("Host {HostId} cannot take control in session {SessionCode} due to special handling", 
                                    Context.ConnectionId, sessionCode);
                            }
                            else
                            {
                                _logger.LogWarning("Client {ClientId} cannot take control from host {HostId} in session {SessionCode}", 
                                    Context.ConnectionId, oldController, sessionCode);
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
                
                // Check if this client is the host for this session
                bool isHost = _sessionHostMap.TryGetValue(sessionCode, out string hostId) && 
                              hostId == Context.ConnectionId;
                
                // Remove the connection from session
                if (_sessionConnections.TryGetValue(sessionCode, out var connections))
                {
                    connections.TryRemove(Context.ConnectionId, out _);
                    
                    // Clean up rate limiting entries for this client
                    if (_clientRateLimits.TryGetValue(sessionCode, out var rateLimits))
                    {
                        // Remove all rate limit entries for this connection
                        var keysToRemove = rateLimits.Keys
                            .Where(k => k.StartsWith($"{Context.ConnectionId}:"))
                            .ToList();
                            
                        foreach (var key in keysToRemove)
                        {
                            rateLimits.TryRemove(key, out _);
                        }
                        
                        // If no more rate limits in this session, remove the session entry
                        if (rateLimits.IsEmpty)
                        {
                            _clientRateLimits.TryRemove(sessionCode, out _);
                        }
                    }
                    
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
                            // If the host disconnected but there are other clients, temporarily transfer control
                            // Only do this if this client is NOT the host, or if we're explicitly told to
                            // This ensures the host can get control back when they reconnect
                            if (!isHost)
                            {
                                // Find a new temporary controller - take the first stable connection
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
                                            _logger.LogInformation("Control temporarily transferred to {NewId} in session {SessionCode} after non-host disconnect", 
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
                                            _logger.LogInformation("Control temporarily transferred to {NewId} in session {SessionCode} after non-host disconnect (no stable connections)", 
                                                newController, sessionCode);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // This is the host disconnecting - we'll keep track of control for when they reconnect
                                _logger.LogInformation("Host {HostId} disconnected from session {SessionCode}, control status preserved for reconnection",
                                    Context.ConnectionId, sessionCode);
                            }
                        }
                        else
                        {
                            // No other connections - don't remove session data yet if this was the host
                            // This allows the host to reconnect to the same session
                            
                            // Do remove controller info though, as the host will reclaim it on reconnect
                            _sessionControlMap.TryRemove(sessionCode, out _);
                            
                            _logger.LogInformation("Last client (host: {IsHost}) disconnected from session {SessionCode}, preserving session", 
                                isHost, sessionCode);
                            
                            // Start a cleanup timer that will remove the session after a longer timeout
                            // if no one reconnects
                            Task.Delay(TimeSpan.FromMinutes(10)).ContinueWith(_ => 
                            {
                                if (_sessionConnections.TryGetValue(sessionCode, out var connCheck) && 
                                    connCheck.Count == 0)
                                {
                                    // If still no connections after the timeout, clean up all session data
                                    ConcurrentDictionary<string, DateTime> connections;
                                    string controlId;
                                    ConcurrentDictionary<string, VariableChangeDto> variables;
                                    DateTime lastActivity;
                                    (double LastGroundState, DateTime LastUpdate) groundState;
                                    (double LastGroundSpeed, double LastAirspeedTrue, double LastAirspeedIndicated, DateTime LastUpdate) speedState;
                                    (string PendingId, DateTime LockTime) transferLock;
                                    string hostId;
                                    
                                    _sessionConnections.TryRemove(sessionCode, out connections);
                                    _sessionControlMap.TryRemove(sessionCode, out controlId);
                                    _sessionVariableValues.TryRemove(sessionCode, out variables);
                                    _sessionLastActivity.TryRemove(sessionCode, out lastActivity);
                                    _sessionGroundStates.TryRemove(sessionCode, out groundState); 
                                    _sessionSpeedStates.TryRemove(sessionCode, out speedState); 
                                    _controlTransferLocks.TryRemove(sessionCode, out transferLock);
                                    // Only remove host info if no one reconnected at all
                                    _sessionHostMap.TryRemove(sessionCode, out hostId);
                                    
                                    _logger.LogInformation("Session {SessionCode} removed after 10-minute empty timeout", 
                                        sessionCode);
                                }
                            });
                        }
                    }
                    
                    // If no more connections, clean up the session
                    if (connections.Count == 0)
                    {
                        _logger.LogInformation("All clients disconnected from session {SessionCode}, waiting for possible reconnection", 
                            sessionCode);
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

    // Add a new method to check and update rate limits
    private bool IsRateLimited(string sessionCode, string connectionId, string actionType)
    {
        var sessionLimits = _clientRateLimits.GetOrAdd(sessionCode, _ => new ConcurrentDictionary<string, DateTime>());
        
        string key = $"{connectionId}:{actionType}";
        string globalKey = $"{connectionId}:global";
        DateTime now = DateTime.UtcNow;
        
        // Check global rate limit for this client (applies to all actions)
        if (sessionLimits.TryGetValue(globalKey, out DateTime lastGlobalAction))
        {
            TimeSpan elapsed = now - lastGlobalAction;
            if (elapsed.TotalMilliseconds < GLOBAL_RATE_LIMIT_MS)
            {
                return true; // Rate limited
            }
        }
        
        // Update global timestamp for this client
        sessionLimits[globalKey] = now;
        
        // Check specific action rate limit
        if (sessionLimits.TryGetValue(key, out DateTime lastAction))
        {
            TimeSpan elapsed = now - lastAction;
            int limitMs = GLOBAL_RATE_LIMIT_MS; // Default
            
            // Different rate limits for different action types
            switch (actionType)
            {
                case "connection":
                    limitMs = CONNECTION_RATE_LIMIT_MS;
                    break;
                case "control":
                    limitMs = CONTROL_ACTION_RATE_LIMIT_MS;
                    break;
            }
            
            if (elapsed.TotalMilliseconds < limitMs)
            {
                return true; // Rate limited
            }
        }
        
        // Update the timestamp for this action
        sessionLimits[key] = now;
        return false; // Not rate limited
    }
}