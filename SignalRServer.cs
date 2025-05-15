using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

var builder = WebApplication.CreateBuilder(args);

// Add console logging - reduce logging in production
builder.Logging.AddConsole();
if (builder.Environment.IsProduction())
{
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}

// Configure CORS properly
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policy =>
    {
        policy.WithOrigins("*") // Replace with your actual client origins in production
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("Content-Disposition");
    });
});

// Add SignalR services with optimized settings for realtime performance
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 50; // Increased from 20
    options.EnableDetailedErrors = true; // Helpful for debugging
    options.KeepAliveInterval = TimeSpan.FromSeconds(5); // Reduced for better responsiveness
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(15); // Reduced for faster timeout detection
}).AddJsonProtocol(options => {
    // Minimize JSON size
    options.PayloadSerializerOptions.IgnoreNullValues = true;
    options.PayloadSerializerOptions.WriteIndented = false;
});

// Add memory cache for potential throttling
builder.Services.AddMemoryCache();

var app = builder.Build();

// Use CORS before routing
app.UseCors("CorsPolicy");

// Map the hub
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

// --- Data Transfer Objects and Hub Implementation ---

public class AircraftData
{
    // Add timestamp for client-side interpolation
    public long Timestamp { get; set; } // Milliseconds since epoch
    
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

// --- Built-in Interpolation for Smooth Aircraft Movement ---

// This class should be used on the client-side (monitoring pilot side) part of your application
public class FlightInterpolation
{
    // Previous and current aircraft data
    private AircraftData _previousData;
    private AircraftData _currentData;
    
    // Time tracking
    private long _lastUpdateTime;
    
    // Initialize with empty data
    public FlightInterpolation()
    {
        _previousData = null;
        _currentData = null;
        _lastUpdateTime = 0;
    }
    
    // Call this when receiving new aircraft data from server
    public void UpdateAircraftData(AircraftData newData)
    {
        // If we have current data, move it to previous
        if (_currentData != null)
        {
            _previousData = _currentData;
        }
        else
        {
            // First data received
            _previousData = newData;
        }
        
        // Update current data and timestamp
        _currentData = newData;
        _lastUpdateTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
    
    // Get interpolated position for rendering - CALL THIS IN YOUR RENDER LOOP
    public AircraftData GetInterpolatedPosition()
    {
        // If we don't have enough data yet, return what we have
        if (_previousData == null || _currentData == null)
        {
            return _currentData ?? _previousData;
        }
        
        // Calculate how far we are between previous and current data in time (0.0 to 1.0)
        long currentTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long timeDiff = _currentData.Timestamp - _previousData.Timestamp;
        
        // Guard against division by zero
        if (timeDiff <= 0)
        {
            return _currentData;
        }
        
        // Calculate interpolation factor (0.0 to 1.0)
        float factor = Math.Clamp((float)(currentTime - _previousData.Timestamp) / timeDiff, 0.0f, 1.0f);
        
        // For stability in fast turns, add a small amount of prediction based on velocity
        // This helps smooth out rapid changes when packets are delayed
        bool useExtrapolation = factor >= 0.9f; // Only predict when we're close to needing a new packet (was 0.85)
        float predictFactor = useExtrapolation ? (factor - 0.9f) * 1.0f : 0f; // Scale from 0-0.1 (was 0-0.3)
        
        // Create interpolated data
        var result = new AircraftData
        {
            Timestamp = currentTime,
            
            // Linear interpolation for position data with slight prediction for smoother transitions
            Latitude = Lerp(_previousData.Latitude, _currentData.Latitude, factor) + 
                      (useExtrapolation ? predictFactor * (_currentData.Latitude - _previousData.Latitude) : 0),
            Longitude = Lerp(_previousData.Longitude, _currentData.Longitude, factor) + 
                       (useExtrapolation ? predictFactor * (_currentData.Longitude - _previousData.Longitude) : 0),
            Altitude = Lerp(_previousData.Altitude, _currentData.Altitude, factor) + 
                      (useExtrapolation ? predictFactor * (_currentData.Altitude - _previousData.Altitude) : 0),
            
            // Spherical interpolation for rotational data - with no extrapolation on rotations for stability
            Pitch = SLerp(_previousData.Pitch, _currentData.Pitch, factor),
            Bank = SLerp(_previousData.Bank, _currentData.Bank, factor),
            Heading = AngleLerp(_previousData.Heading, _currentData.Heading, factor),
            
            // Linear interpolation for everything else with slight prediction for controls
            Throttle = Lerp(_previousData.Throttle, _currentData.Throttle, factor),
            Aileron = Lerp(_previousData.Aileron, _currentData.Aileron, factor),
            Elevator = Lerp(_previousData.Elevator, _currentData.Elevator, factor),
            Rudder = Lerp(_previousData.Rudder, _currentData.Rudder, factor),
            ElevatorTrimPosition = Lerp(_previousData.ElevatorTrimPosition, _currentData.ElevatorTrimPosition, factor),
            
            // Non-interpolated data (use current state)
            BrakeLeft = _currentData.BrakeLeft,
            BrakeRight = _currentData.BrakeRight,
            ParkingBrake = _currentData.ParkingBrake,
            Mixture = _currentData.Mixture,
            Flaps = _currentData.Flaps,
            Gear = _currentData.Gear,
            
            // Calculated velocities using interpolation with minimal prediction for smoother changes
            // Velocity data is especially sensitive - too much extrapolation causes jerky movement
            GroundSpeed = Lerp(_previousData.GroundSpeed, _currentData.GroundSpeed, factor) + 
                         (useExtrapolation ? predictFactor * 0.5 * (_currentData.GroundSpeed - _previousData.GroundSpeed) : 0),
            VerticalSpeed = Lerp(_previousData.VerticalSpeed, _currentData.VerticalSpeed, factor) + 
                           (useExtrapolation ? predictFactor * 0.5 * (_currentData.VerticalSpeed - _previousData.VerticalSpeed) : 0),
            AirspeedTrue = Lerp(_previousData.AirspeedTrue, _currentData.AirspeedTrue, factor) + 
                          (useExtrapolation ? predictFactor * 0.5 * (_currentData.AirspeedTrue - _previousData.AirspeedTrue) : 0),
            AirspeedIndicated = Lerp(_previousData.AirspeedIndicated, _currentData.AirspeedIndicated, factor) + 
                               (useExtrapolation ? predictFactor * 0.5 * (_currentData.AirspeedIndicated - _previousData.AirspeedIndicated) : 0),
            
            // Other non-interpolated state data
            OnGround = _currentData.OnGround,
            
            // Velocity interpolation with reduced prediction (50%) for smoother transitions
            VelocityBodyX = Lerp(_previousData.VelocityBodyX, _currentData.VelocityBodyX, factor) + 
                           (useExtrapolation ? predictFactor * 0.5 * (_currentData.VelocityBodyX - _previousData.VelocityBodyX) : 0),
            VelocityBodyY = Lerp(_previousData.VelocityBodyY, _currentData.VelocityBodyY, factor) + 
                           (useExtrapolation ? predictFactor * 0.5 * (_currentData.VelocityBodyY - _previousData.VelocityBodyY) : 0),
            VelocityBodyZ = Lerp(_previousData.VelocityBodyZ, _currentData.VelocityBodyZ, factor) + 
                           (useExtrapolation ? predictFactor * 0.5 * (_currentData.VelocityBodyZ - _previousData.VelocityBodyZ) : 0),
            
            // Light states - don't interpolate these
            LightBeacon = _currentData.LightBeacon,
            LightLanding = _currentData.LightLanding,
            LightTaxi = _currentData.LightTaxi,
            LightNav = _currentData.LightNav,
            LightStrobe = _currentData.LightStrobe,
            PitotHeat = _currentData.PitotHeat
        };
        
        return result;
    }
    
    // Linear interpolation between two values
    private double Lerp(double a, double b, float t)
    {
        return a + (b - a) * t;
    }
    
    // Spherical linear interpolation (for rotations)
    private double SLerp(double a, double b, float t)
    {
        // Convert to radians
        double angleA = a * Math.PI / 180.0;
        double angleB = b * Math.PI / 180.0;
        
        // For rotational data, we need to be careful about transitions
        // Use quaternions for smoother rotation interpolation
        double sinA = Math.Sin(angleA / 2.0);
        double cosA = Math.Cos(angleA / 2.0);
        double sinB = Math.Sin(angleB / 2.0);
        double cosB = Math.Cos(angleB / 2.0);
        
        // Compute dot product to determine shortest path
        double dot = sinA * sinB + cosA * cosB;
        
        // Ensure we take the shortest path
        if (dot < 0)
        {
            sinB = -sinB;
            cosB = -cosB;
            dot = -dot;
        }
        
        // Use less bias toward newer values for more stable rotation
        float adjustedT = 0.3f + (t * 0.7f); // Was 0.5 + (t * 0.5), which biased too much toward newer values
        
        // Calculate interpolation parameters
        double theta = Math.Acos(dot);
        double sinTheta = Math.Sin(theta);
        
        // Handle edge cases to avoid division by zero
        if (sinTheta < 0.001)
        {
            // Linear interpolation when angles are very close
            double s0Linear = 1.0 - adjustedT;
            double s1Linear = adjustedT;
            
            double newSinLinear = sinA * s0Linear + sinB * s1Linear;
            double newCosLinear = cosA * s0Linear + cosB * s1Linear;
            
            // Convert back to angle
            double resultAngleLinear = 2.0 * Math.Atan2(newSinLinear, newCosLinear);
            return resultAngleLinear * 180.0 / Math.PI;
        }
        
        // Proper spherical linear interpolation formula
        double s0 = Math.Sin((1.0 - adjustedT) * theta) / sinTheta;
        double s1 = Math.Sin(adjustedT * theta) / sinTheta;
        
        double newSin = sinA * s0 + sinB * s1;
        double newCos = cosA * s0 + cosB * s1;
        
        // Convert back to angle
        double resultAngle = 2.0 * Math.Atan2(newSin, newCos);
        return resultAngle * 180.0 / Math.PI;
    }
    
    // Special interpolation for angles that handles wrap-around (0-360)
    private double AngleLerp(double a, double b, float t)
    {
        // Normalize angles to 0-360 range
        a = (a % 360 + 360) % 360;
        b = (b % 360 + 360) % 360;
        
        // Find the shortest path
        double diff = ((b - a + 540) % 360) - 180;
        
        // Smooth out very rapid heading changes (greater than 120 degrees)
        if (Math.Abs(diff) > 120)
        {
            // Use a more gradual interpolation for extreme changes 
            // to avoid spinning aircraft too quickly
            float adjustedT = t * t * (3 - 2 * t); // Smooth step interpolation
            return (a + diff * adjustedT + 360) % 360;
        }
        
        return (a + diff * t + 360) % 360;
    }
}

// To use the interpolation in the client side of your app:
// 1. Create an instance: FlightInterpolation interpolator = new FlightInterpolation();
// 2. When receiving data: interpolator.UpdateAircraftData(receivedData);
// 3. In your render loop: var smoothData = interpolator.GetInterpolatedPosition();
// 4. Apply smoothData to your aircraft model

// ... existing LightStatesDto and other DTOs ...

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

// New DTOs for G1000 avionics synchronization
public class RadioFrequencyChangeDto
{
    public string RadioType { get; set; } // "NAV1", "NAV2", "COM1", "COM2"
    public int SubIndex { get; set; } // 0=active, 1=standby, 2=swap
    public double Value { get; set; } // Frequency value or 1.0 for swap
}

public class TransponderChangeDto
{
    public int SubIndex { get; set; } // 0=code, 1=mode, 2=ident
    public double Value { get; set; } // Code value, mode value, or 1.0 for ident
}

public class AdfChangeDto
{
    public int SubIndex { get; set; } // 0=frequency, 1=card
    public double Value { get; set; } // Frequency or card value
}

public class ObsChangeDto
{
    public int SubIndex { get; set; } // 0=NAV1, 1=NAV2, 2=GPS
    public double Value { get; set; } // OBS value in degrees
}

public class AvionicsChangeDto
{
    public int SubIndex { get; set; } // 0=master, 1=bus1, 2=bus2
    public double Value { get; set; } // 0.0 or 1.0 for off/on
}

public class ElectricalMasterChangeDto
{
    public int SubIndex { get; set; } // 0=battery, 1=alternator
    public double Value { get; set; } // 0.0 or 1.0 for off/on
}

public class LightChangeDto
{
    public int SubIndex { get; set; } // 0=nav, 1=beacon, 2=landing, 3=taxi, 4=strobe, 5=panel, 6=pitot heat
    public double Value { get; set; } // 0.0 or 1.0 for off/on
}

public class AutopilotChangeDto
{
    public int SubIndex { get; set; } // 0=master, 1=FD, 2=HDG, 3=NAV, 4=APR, 5=ALT, 6=VS, 7=FLC, 8=HDG setting, 9=ALT s
    public double Value { get; set; } // 0.0 or 1.0 for mode toggles, or actual values for settings
}

public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    private static readonly Dictionary<string, string> _sessionControlMap = new();
    private static readonly Dictionary<string, List<string>> _sessionConnections = new();
    
    // Track last send time per connection to enable throttling
    private static readonly Dictionary<string, long> _lastSendTime = new();
    // Minimum time between position updates (ms)
    private const int MIN_UPDATE_INTERVAL = 33; // ~30 fps is sufficient for smooth animation

    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinSession(string sessionCode)
    {
        _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        if (!_sessionConnections.ContainsKey(sessionCode))
            _sessionConnections[sessionCode] = new List<string>();
        _sessionConnections[sessionCode].Add(Context.ConnectionId);
        
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
    }
    
    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        if (_sessionControlMap.TryGetValue(sessionCode, out string controlId) && controlId == Context.ConnectionId)
        {
            // Apply throttling to avoid network congestion
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            if (!_lastSendTime.TryGetValue(Context.ConnectionId, out long lastSend) || (now - lastSend) >= MIN_UPDATE_INTERVAL)
            {
                // Set timestamp on server to ensure accuracy
                data.Timestamp = now;
                
                _lastSendTime[Context.ConnectionId] = now;
                
                // Reduce logging for performance - log at debug level only
                _logger.LogDebug("Received data in session {SessionCode}: Alt={Alt:F1}", sessionCode, data.Altitude);
                
                await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
            }
        }
    }
    
    public async Task SendLightStates(string sessionCode, LightStatesDto lights)
    {
        _logger.LogInformation("Received light states in session {SessionCode}", sessionCode);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightStates", lights);
    }
    
    public async Task SendPitotHeatState(string sessionCode, PitotHeatStateDto state)
    {
        _logger.LogInformation("Received pitot heat state in session {SessionCode}: {State}", sessionCode, state.PitotHeatOn);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceivePitotHeatState", state);
    }
    
    public async Task SendG1000SoftkeyPress(string sessionCode, G1000SoftkeyPressDto press)
    {
        _logger.LogInformation("Received G1000 softkey press in session {SessionCode}: {Number}", sessionCode, press.SoftkeyNumber);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveG1000SoftkeyPress", press);
    }
    
    // New methods for G1000 avionics synchronization
    public async Task SendRadioFrequencyChange(string sessionCode, RadioFrequencyChangeDto change)
    {
        _logger.LogInformation("Received radio frequency change in session {SessionCode}: {Radio} subIndex={SubIndex}", 
            sessionCode, change.RadioType, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveRadioFrequencyChange", change);
    }

    public async Task SendTransponderChange(string sessionCode, TransponderChangeDto change)
    {
        _logger.LogInformation("Received transponder change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveTransponderChange", change);
    }

    public async Task SendAdfChange(string sessionCode, AdfChangeDto change)
    {
        _logger.LogInformation("Received ADF change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAdfChange", change);
    }

    public async Task SendObsChange(string sessionCode, ObsChangeDto change)
    {
        _logger.LogInformation("Received OBS change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveObsChange", change);
    }

    public async Task SendAvionicsChange(string sessionCode, AvionicsChangeDto change)
    {
        _logger.LogInformation("Received avionics change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAvionicsChange", change);
    }

    public async Task SendElectricalMasterChange(string sessionCode, ElectricalMasterChangeDto change)
    {
        _logger.LogInformation("Received electrical master change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveElectricalMasterChange", change);
    }

    public async Task SendLightChange(string sessionCode, LightChangeDto change)
    {
        _logger.LogInformation("Received light change in session {SessionCode}: subIndex={SubIndex}, value={Value}", 
            sessionCode, change.SubIndex, change.Value);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightChange", change);
    }

    public async Task SendAutopilotChange(string sessionCode, AutopilotChangeDto change)
    {
        _logger.LogInformation("Received autopilot change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAutopilotChange", change);
    }
    
    public async Task TransferControl(string sessionCode, bool giving)
    {
        string currentController = _sessionControlMap.GetValueOrDefault(sessionCode, "");
        if (giving)
        {
            if (currentController == Context.ConnectionId)
            {
                var otherConnections = _sessionConnections[sessionCode].Where(id => id != Context.ConnectionId).ToList();
                if (otherConnections.Any())
                {
                    var newController = otherConnections.First();
                    _sessionControlMap[sessionCode] = newController;
                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", Context.ConnectionId, newController, sessionCode);
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
                _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", Context.ConnectionId, oldController, sessionCode);
            }
        }
    }
    
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }
    
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        // Clean up throttling tracker
        _lastSendTime.Remove(Context.ConnectionId);
        
        var sessions = _sessionConnections.Where(kvp => kvp.Value.Contains(Context.ConnectionId))
                                           .Select(kvp => kvp.Key)
                                           .ToList();
        foreach (var session in sessions)
        {
            _sessionConnections[session].Remove(Context.ConnectionId);
            if (_sessionControlMap.GetValueOrDefault(session) == Context.ConnectionId)
            {
                if (_sessionConnections[session].Any())
                {
                    var newController = _sessionConnections[session].First();
                    _sessionControlMap[session] = newController;
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    _logger.LogInformation("Control automatically transferred to {NewId} in session {SessionCode}", newController, session);
                }
                else
                {
                    _sessionControlMap.Remove(session);
                    _sessionConnections.Remove(session);
                    _logger.LogInformation("Session {SessionCode} removed as all clients disconnected", session);
                }
            }
            if (!_sessionConnections[session].Any())
            {
                _sessionConnections.Remove(session);
                _sessionControlMap.Remove(session);
            }
        }
        
        await base.OnDisconnectedAsync(exception);
    }
} 