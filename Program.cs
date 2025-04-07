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

    // Method to handle regular AircraftData DTO
    public async Task SendAircraftData(string sessionCode, object data)
    {
        // Verify this client has control before broadcasting data
        if (_sessionControlMap.TryGetValue(sessionCode, out var controlId) && controlId == Context.ConnectionId)
        {
            try
            {
                AircraftData aircraftData;
                
                // Check if the data is a dictionary (fallback mechanism)
                if (data is Dictionary<string, object> dict)
                {
                    aircraftData = ConvertDictionaryToAircraftData(dict);
                }
                else
                {
                    // Try to convert to AircraftData
                    aircraftData = data is AircraftData typedData 
                        ? typedData 
                        : JsonSerializer.Deserialize<AircraftData>(JsonSerializer.Serialize(data));
                }
                
                // Sanitize data to prevent issues with special floating point values
                SanitizeData(aircraftData);
                
                // Only log essential info to avoid console spam
                _logger.LogInformation("Received data from controller in session {SessionCode}: Alt={Alt:F1}, GS={GS:F1}", 
                    sessionCode, aircraftData.Altitude, aircraftData.GroundSpeed);
                    
                await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", aircraftData);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing aircraft data in session {SessionCode}", sessionCode);
            }
        }
    }
    
    // Helper method to convert dictionary to AircraftData
    private AircraftData ConvertDictionaryToAircraftData(Dictionary<string, object> dict)
    {
        var data = new AircraftData();
        
        // Set properties from dictionary if they exist
        if (dict.TryGetValue("Latitude", out var lat) && lat != null) data.Latitude = Convert.ToDouble(lat);
        if (dict.TryGetValue("Longitude", out var lon) && lon != null) data.Longitude = Convert.ToDouble(lon);
        if (dict.TryGetValue("Altitude", out var alt) && alt != null) data.Altitude = Convert.ToDouble(alt);
        if (dict.TryGetValue("Pitch", out var pitch) && pitch != null) data.Pitch = Convert.ToDouble(pitch);
        if (dict.TryGetValue("Bank", out var bank) && bank != null) data.Bank = Convert.ToDouble(bank);
        if (dict.TryGetValue("Heading", out var heading) && heading != null) data.Heading = Convert.ToDouble(heading);
        if (dict.TryGetValue("Throttle", out var throttle) && throttle != null) data.Throttle = Convert.ToDouble(throttle);
        if (dict.TryGetValue("Aileron", out var aileron) && aileron != null) data.Aileron = Convert.ToDouble(aileron);
        if (dict.TryGetValue("Elevator", out var elevator) && elevator != null) data.Elevator = Convert.ToDouble(elevator);
        if (dict.TryGetValue("Rudder", out var rudder) && rudder != null) data.Rudder = Convert.ToDouble(rudder);
        if (dict.TryGetValue("BrakeLeft", out var brakeLeft) && brakeLeft != null) data.BrakeLeft = Convert.ToDouble(brakeLeft);
        if (dict.TryGetValue("BrakeRight", out var brakeRight) && brakeRight != null) data.BrakeRight = Convert.ToDouble(brakeRight);
        if (dict.TryGetValue("ParkingBrake", out var parkingBrake) && parkingBrake != null) data.ParkingBrake = Convert.ToDouble(parkingBrake);
        if (dict.TryGetValue("Mixture", out var mixture) && mixture != null) data.Mixture = Convert.ToDouble(mixture);
        if (dict.TryGetValue("Flaps", out var flaps) && flaps != null) data.Flaps = Convert.ToInt32(flaps);
        if (dict.TryGetValue("Gear", out var gear) && gear != null) data.Gear = Convert.ToInt32(gear);
        if (dict.TryGetValue("GroundSpeed", out var groundSpeed) && groundSpeed != null) data.GroundSpeed = Convert.ToDouble(groundSpeed);
        if (dict.TryGetValue("VerticalSpeed", out var verticalSpeed) && verticalSpeed != null) data.VerticalSpeed = Convert.ToDouble(verticalSpeed);
        if (dict.TryGetValue("AirspeedTrue", out var airspeedTrue) && airspeedTrue != null) data.AirspeedTrue = Convert.ToDouble(airspeedTrue);
        if (dict.TryGetValue("AirspeedIndicated", out var airspeedIndicated) && airspeedIndicated != null) data.AirspeedIndicated = Convert.ToDouble(airspeedIndicated);
        if (dict.TryGetValue("OnGround", out var onGround) && onGround != null) data.OnGround = Convert.ToDouble(onGround);
        if (dict.TryGetValue("VelocityBodyX", out var velocityBodyX) && velocityBodyX != null) data.VelocityBodyX = Convert.ToDouble(velocityBodyX);
        if (dict.TryGetValue("VelocityBodyY", out var velocityBodyY) && velocityBodyY != null) data.VelocityBodyY = Convert.ToDouble(velocityBodyY);
        if (dict.TryGetValue("VelocityBodyZ", out var velocityBodyZ) && velocityBodyZ != null) data.VelocityBodyZ = Convert.ToDouble(velocityBodyZ);
        if (dict.TryGetValue("ElevatorTrimPosition", out var elevatorTrimPosition) && elevatorTrimPosition != null) data.ElevatorTrimPosition = Convert.ToDouble(elevatorTrimPosition);
        
        // Lighting properties
        if (dict.TryGetValue("LightBeacon", out var lightBeacon) && lightBeacon != null) data.LightBeacon = Convert.ToDouble(lightBeacon);
        if (dict.TryGetValue("LightLanding", out var lightLanding) && lightLanding != null) data.LightLanding = Convert.ToDouble(lightLanding);
        if (dict.TryGetValue("LightTaxi", out var lightTaxi) && lightTaxi != null) data.LightTaxi = Convert.ToDouble(lightTaxi);
        if (dict.TryGetValue("LightNav", out var lightNav) && lightNav != null) data.LightNav = Convert.ToDouble(lightNav);
        if (dict.TryGetValue("LightStrobe", out var lightStrobe) && lightStrobe != null) data.LightStrobe = Convert.ToDouble(lightStrobe);
        
        return data;
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