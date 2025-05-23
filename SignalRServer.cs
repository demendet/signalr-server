using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.ResponseCompression;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();

builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 102400;
    options.StreamBufferCapacity = 20;
    options.EnableDetailedErrors = false;
    options.HandshakeTimeout = TimeSpan.FromSeconds(5);
    options.KeepAliveInterval = TimeSpan.FromSeconds(5);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(10);
    options.MaximumParallelInvocationsPerClient = 1;
});

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});

builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = System.IO.Compression.CompressionLevel.Fastest;
});

var app = builder.Build();

app.UseResponseCompression();

app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

public class AircraftData
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
}

public class LightStatesDto
{
    public bool LightBeacon { get; set; }
    public bool LightLanding { get; set; }
    public bool LightTaxi { get; set; }
    public bool LightNav { get; set; }
    public bool LightStrobe { get; set; }
}

public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    private static readonly Dictionary<string, string> _sessionControlMap = new();
    private static readonly Dictionary<string, List<string>> _sessionConnections = new();
    private static readonly object _lockObject = new object();
    private static readonly Dictionary<string, DateTime> _lastSendTimes = new();
    private const int MIN_SEND_INTERVAL_MS = 15;

    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinSession(string sessionCode)
    {
        lock (_lockObject)
        {
            _logger.LogInformation("Connection {ConnectionId} joining session {SessionCode}", Context.ConnectionId, sessionCode);
            
            if (!_sessionConnections.ContainsKey(sessionCode))
            {
                _sessionConnections[sessionCode] = new List<string>();
                _logger.LogInformation("Created new session {SessionCode}", sessionCode);
            }
            
            // Remove connection from any existing sessions first
            var existingSessions = _sessionConnections
                .Where(kvp => kvp.Value.Contains(Context.ConnectionId))
                .Select(kvp => kvp.Key)
                .ToList();
                
            foreach (var existingSession in existingSessions)
            {
                _sessionConnections[existingSession].Remove(Context.ConnectionId);
                _logger.LogInformation("Removed connection {ConnectionId} from existing session {ExistingSession}", 
                    Context.ConnectionId, existingSession);
            }
            
            // Add to new session
            _sessionConnections[sessionCode].Add(Context.ConnectionId);
            _logger.LogInformation("Connection {ConnectionId} added to session {SessionCode}. Total connections: {Count}", 
                Context.ConnectionId, sessionCode, _sessionConnections[sessionCode].Count);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        lock (_lockObject)
        {
            if (!_sessionControlMap.ContainsKey(sessionCode))
            {
                _sessionControlMap[sessionCode] = Context.ConnectionId;
                _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", 
                    Context.ConnectionId, sessionCode);
            }
        }
        
        bool hasControl = _sessionControlMap.GetValueOrDefault(sessionCode) == Context.ConnectionId;
        await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
        _logger.LogInformation("Notified client {ClientId} of control status ({HasControl}) in session {SessionCode}", 
            Context.ConnectionId, hasControl, sessionCode);
    }

    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        lock (_lockObject)
        {
            if (_lastSendTimes.TryGetValue(Context.ConnectionId, out DateTime lastSend))
            {
                var timeSinceLastSend = (DateTime.UtcNow - lastSend).TotalMilliseconds;
                if (timeSinceLastSend < MIN_SEND_INTERVAL_MS)
                {
                    return;
                }
            }
            _lastSendTimes[Context.ConnectionId] = DateTime.UtcNow;
        }

        bool isController;
        lock (_lockObject)
        {
            isController = _sessionControlMap.TryGetValue(sessionCode, out var controlId) && controlId == Context.ConnectionId;
        }

        if (isController)
        {
            if (DateTime.UtcNow.Second % 5 == 0)
            {
                _logger.LogInformation("Received data from controller in session {SessionCode}: Alt={Alt:F1}, GS={GS:F1}",
                    sessionCode, data.Altitude, data.GroundSpeed);
            }

            try
            {
                await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending aircraft data to clients in session {SessionCode}", sessionCode);
            }
        }
        else
        {
            _logger.LogWarning("Rejected data from non-controller {ConnectionId} in session {SessionCode}",
                Context.ConnectionId, sessionCode);
        }
    }
    
    public async Task SendLightStates(string sessionCode, LightStatesDto lights)
    {
        _logger.LogInformation("Received light states from client in session {SessionCode}: B={Beacon}, L={Landing}, T={Taxi}, N={Nav}, S={Strobe}", 
            sessionCode, lights.LightBeacon, lights.LightLanding, lights.LightTaxi, lights.LightNav, lights.LightStrobe);
        
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightStates", lights);
    }
    
    public async Task TransferControl(string sessionCode, bool giving)
    {
        lock (_lockObject)
        {
            string currentController = _sessionControlMap.GetValueOrDefault(sessionCode, "");
            
            if (giving) 
            {
                if (currentController == Context.ConnectionId)
                {
                    var otherConnections = _sessionConnections.GetValueOrDefault(sessionCode, new List<string>())
                        .Where(id => id != Context.ConnectionId)
                        .ToList();
                        
                    if (otherConnections.Any())
                    {
                        var newController = otherConnections.First();
                        _sessionControlMap[sessionCode] = newController;
                        
                        _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", 
                            Context.ConnectionId, newController, sessionCode);
                    }
                }
            }
            else
            {
                if (currentController != Context.ConnectionId)
                {
                    var oldController = currentController;
                    _sessionControlMap[sessionCode] = Context.ConnectionId;
                    
                    _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", 
                        Context.ConnectionId, oldController, sessionCode);
                }
            }
        }
        
        // Send status updates outside the lock
        bool hasControl = _sessionControlMap.GetValueOrDefault(sessionCode) == Context.ConnectionId;
        await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
        
        var currentControllerAfter = _sessionControlMap.GetValueOrDefault(sessionCode);
        if (!string.IsNullOrEmpty(currentControllerAfter) && currentControllerAfter != Context.ConnectionId)
        {
            bool controllerHasControl = currentControllerAfter == Context.ConnectionId;
            await Clients.Client(currentControllerAfter).SendAsync("ControlStatusChanged", !controllerHasControl);
        }
    }
    
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        List<string> sessionsToCleanup = new List<string>();
        
        lock (_lockObject)
        {
            _logger.LogInformation("Connection {ConnectionId} disconnecting", Context.ConnectionId);
            
            // Find all sessions this connection was part of
            var sessions = _sessionConnections
                .Where(kvp => kvp.Value.Contains(Context.ConnectionId))
                .Select(kvp => kvp.Key)
                .ToList();
                
            foreach (var session in sessions)
            {
                // Remove connection from session
                if (_sessionConnections.ContainsKey(session))
                {
                    _sessionConnections[session].Remove(Context.ConnectionId);
                    _logger.LogInformation("Removed connection {ConnectionId} from session {SessionCode}", 
                        Context.ConnectionId, session);
                    
                    // Check if this was the controller
                    if (_sessionControlMap.GetValueOrDefault(session) == Context.ConnectionId)
                    {
                        var remainingConnections = _sessionConnections[session];
                        if (remainingConnections.Any())
                        {
                            // Transfer control to first remaining connection
                            var newController = remainingConnections.First();
                            _sessionControlMap[session] = newController;
                            _logger.LogInformation("Control automatically transferred to {NewId} after disconnect in session {SessionCode}", 
                                newController, session);
                            
                            // We'll notify the new controller outside the lock
                            sessionsToCleanup.Add($"notify:{session}:{newController}");
                        }
                        else
                        {
                            // No remaining connections, mark session for removal
                            _sessionControlMap.Remove(session);
                            _logger.LogInformation("Removed control mapping for empty session {SessionCode}", session);
                        }
                    }
                    
                    // If no connections left in session, mark for cleanup
                    if (!_sessionConnections[session].Any())
                    {
                        sessionsToCleanup.Add($"remove:{session}");
                    }
                }
            }
            
            // Clean up empty sessions
            foreach (var cleanup in sessionsToCleanup)
            {
                if (cleanup.StartsWith("remove:"))
                {
                    var sessionToRemove = cleanup.Substring(7);
                    _sessionConnections.Remove(sessionToRemove);
                    _sessionControlMap.Remove(sessionToRemove);
                    _logger.LogInformation("Session {SessionCode} removed as all clients disconnected", sessionToRemove);
                }
            }
        }
        
        // Send notifications outside the lock to avoid deadlocks
        foreach (var cleanup in sessionsToCleanup)
        {
            if (cleanup.StartsWith("notify:"))
            {
                var parts = cleanup.Split(':');
                var session = parts[1];
                var newController = parts[2];
                
                try
                {
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to notify new controller {NewController} in session {Session}", 
                        newController, session);
                }
            }
        }
        
        await base.OnDisconnectedAsync(exception);
    }
}