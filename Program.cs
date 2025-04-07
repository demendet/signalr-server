using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add console logging.
builder.Logging.AddConsole();

// Add SignalR services with increased buffer size for smoother data flow
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
}).AddJsonProtocol(options => {
    // Configure JSON serialization to handle NaN, Infinity properly
    options.PayloadSerializerOptions = new JsonSerializerOptions {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };
});

var app = builder.Build();

// Use top-level route registration for the hub.
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

// Enhanced AircraftData class with physics properties and lighting
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
    public double ParkingBrake { get; set; } // Keep as double for consistency with client
    public double Mixture { get; set; }
    public int Flaps { get; set; }
    public int Gear { get; set; }
    // Physics properties
    public double GroundSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public double AirspeedTrue { get; set; }
    public double AirspeedIndicated { get; set; }
    public double OnGround { get; set; }
    public double VelocityBodyX { get; set; }
    public double VelocityBodyY { get; set; }
    public double VelocityBodyZ { get; set; }
    public double ElevatorTrimPosition { get; set; }
    // Lighting properties
    public double LightBeacon { get; set; }
    public double LightLanding { get; set; }
    public double LightTaxi { get; set; }
    public double LightNav { get; set; }
    public double LightStrobe { get; set; }
}

// The SignalR hub
public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    private static readonly Dictionary<string, string> _sessionControlMap = new();
    private static readonly Dictionary<string, List<string>> _sessionConnections = new();

    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinSession(string sessionCode)
    {
        _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        // Track connections in the session
        if (!_sessionConnections.ContainsKey(sessionCode))
        {
            _sessionConnections[sessionCode] = new List<string>();
        }
        _sessionConnections[sessionCode].Add(Context.ConnectionId);
        
        // By default, the first connection (host) has control
        if (!_sessionControlMap.ContainsKey(sessionCode))
        {
            _sessionControlMap[sessionCode] = Context.ConnectionId;
            // Inform the client they have control (host always starts with control)
            await Clients.Caller.SendAsync("ControlStatusChanged", true);
            _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", 
                Context.ConnectionId, sessionCode);
        }
        else
        {
            // Inform joining client about current control status
            bool hasControl = _sessionControlMap[sessionCode] == Context.ConnectionId;
            await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
            _logger.LogInformation("Notified joining client {ClientId} of control status ({HasControl}) in session {SessionCode}", 
                Context.ConnectionId, hasControl, sessionCode);
        }
    }

    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        // Verify this client has control before broadcasting data
        if (_sessionControlMap.TryGetValue(sessionCode, out var controlId) && controlId == Context.ConnectionId)
        {
            // Sanitize data to prevent issues with special floating point values
            SanitizeData(data);
            
            // Only log essential info to avoid console spam
            _logger.LogInformation("Received data from controller in session {SessionCode}: Alt={Alt:F1}, GS={GS:F1}", 
                sessionCode, data.Altitude, data.GroundSpeed);
                
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
        }
    }
    
    // Helper method to sanitize incoming data
    private void SanitizeData(AircraftData data)
    {
        // Check for NaN or Infinity values and replace with defaults
        if (double.IsNaN(data.Latitude) || double.IsInfinity(data.Latitude)) data.Latitude = 0;
        if (double.IsNaN(data.Longitude) || double.IsInfinity(data.Longitude)) data.Longitude = 0;
        if (double.IsNaN(data.Altitude) || double.IsInfinity(data.Altitude)) data.Altitude = 0;
        if (double.IsNaN(data.Pitch) || double.IsInfinity(data.Pitch)) data.Pitch = 0;
        if (double.IsNaN(data.Bank) || double.IsInfinity(data.Bank)) data.Bank = 0;
        if (double.IsNaN(data.Heading) || double.IsInfinity(data.Heading)) data.Heading = 0;
        if (double.IsNaN(data.Throttle) || double.IsInfinity(data.Throttle)) data.Throttle = 0;
        if (double.IsNaN(data.Aileron) || double.IsInfinity(data.Aileron)) data.Aileron = 0;
        if (double.IsNaN(data.Elevator) || double.IsInfinity(data.Elevator)) data.Elevator = 0;
        if (double.IsNaN(data.Rudder) || double.IsInfinity(data.Rudder)) data.Rudder = 0;
        if (double.IsNaN(data.BrakeLeft) || double.IsInfinity(data.BrakeLeft)) data.BrakeLeft = 0;
        if (double.IsNaN(data.BrakeRight) || double.IsInfinity(data.BrakeRight)) data.BrakeRight = 0;
        if (double.IsNaN(data.ParkingBrake) || double.IsInfinity(data.ParkingBrake)) data.ParkingBrake = 0;
        if (double.IsNaN(data.Mixture) || double.IsInfinity(data.Mixture)) data.Mixture = 0;
        if (double.IsNaN(data.GroundSpeed) || double.IsInfinity(data.GroundSpeed)) data.GroundSpeed = 0;
        if (double.IsNaN(data.VerticalSpeed) || double.IsInfinity(data.VerticalSpeed)) data.VerticalSpeed = 0;
        if (double.IsNaN(data.AirspeedTrue) || double.IsInfinity(data.AirspeedTrue)) data.AirspeedTrue = 0;
        if (double.IsNaN(data.AirspeedIndicated) || double.IsInfinity(data.AirspeedIndicated)) data.AirspeedIndicated = 0;
        if (double.IsNaN(data.OnGround) || double.IsInfinity(data.OnGround)) data.OnGround = 1;
        if (double.IsNaN(data.VelocityBodyX) || double.IsInfinity(data.VelocityBodyX)) data.VelocityBodyX = 0;
        if (double.IsNaN(data.VelocityBodyY) || double.IsInfinity(data.VelocityBodyY)) data.VelocityBodyY = 0;
        if (double.IsNaN(data.VelocityBodyZ) || double.IsInfinity(data.VelocityBodyZ)) data.VelocityBodyZ = 0;
        if (double.IsNaN(data.ElevatorTrimPosition) || double.IsInfinity(data.ElevatorTrimPosition)) data.ElevatorTrimPosition = 0;
        
        // Sanitize lighting values too
        if (double.IsNaN(data.LightBeacon) || double.IsInfinity(data.LightBeacon)) data.LightBeacon = 0;
        if (double.IsNaN(data.LightLanding) || double.IsInfinity(data.LightLanding)) data.LightLanding = 0;
        if (double.IsNaN(data.LightTaxi) || double.IsInfinity(data.LightTaxi)) data.LightTaxi = 0;
        if (double.IsNaN(data.LightNav) || double.IsInfinity(data.LightNav)) data.LightNav = 0;
        if (double.IsNaN(data.LightStrobe) || double.IsInfinity(data.LightStrobe)) data.LightStrobe = 0;
    }
    
    public async Task TransferControl(string sessionCode, bool giving)
    {
        string currentController = _sessionControlMap.GetValueOrDefault(sessionCode, "");
        
        if (giving) // Host is giving control
        {
            if (currentController == Context.ConnectionId)
            {
                // Find other clients in this session
                var otherConnections = _sessionConnections[sessionCode]
                    .Where(id => id != Context.ConnectionId)
                    .ToList();
                    
                if (otherConnections.Any())
                {
                    var newController = otherConnections.First();
                    _sessionControlMap[sessionCode] = newController;
                    
                    // Notify both parties
                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    
                    _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", 
                        Context.ConnectionId, newController, sessionCode);
                }
            }
        }
        else // Client is taking control
        {
            if (currentController != Context.ConnectionId)
            {
                var oldController = currentController;
                _sessionControlMap[sessionCode] = Context.ConnectionId;
                
                // Notify both parties
                await Clients.Caller.SendAsync("ControlStatusChanged", true);
                if (!string.IsNullOrEmpty(oldController))
                    await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                
                _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", 
                    Context.ConnectionId, oldController, sessionCode);
            }
        }
    }
    
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        // Find any sessions this client is part of
        var sessions = _sessionConnections
            .Where(kvp => kvp.Value.Contains(Context.ConnectionId))
            .Select(kvp => kvp.Key)
            .ToList();
            
        foreach (var session in sessions)
        {
            // Remove client from the session
            _sessionConnections[session].Remove(Context.ConnectionId);
            
            // If this was the controlling client, reassign control
            if (_sessionControlMap.GetValueOrDefault(session) == Context.ConnectionId)
            {
                if (_sessionConnections[session].Any())
                {
                    var newController = _sessionConnections[session].First();
                    _sessionControlMap[session] = newController;
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    _logger.LogInformation("Control automatically transferred to {NewId} after disconnect in session {SessionCode}", 
                        newController, session);
                }
                else
                {
                    // If no other connections, remove the session
                    _sessionControlMap.Remove(session);
                    _sessionConnections.Remove(session);
                    _logger.LogInformation("Session {SessionCode} removed as all clients disconnected", session);
                }
            }
            
            // If session is empty, clean up
            if (!_sessionConnections[session].Any())
            {
                _sessionConnections.Remove(session);
                _sessionControlMap.Remove(session);
            }
        }
        
        await base.OnDisconnectedAsync(exception);
    }
}