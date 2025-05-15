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
    options.StreamBufferCapacity = 30; // Reduced from 50 to decrease memory usage
    options.EnableDetailedErrors = true; // Helpful for debugging
    options.KeepAliveInterval = TimeSpan.FromSeconds(5); // Increased from 2 to 5 seconds for less network overhead
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(15); // Increased for more stability
    options.HandshakeTimeout = TimeSpan.FromSeconds(10); // Increased for more reliable connections
    options.MaximumParallelInvocationsPerClient = 1; // Ensure ordered delivery
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
        
        // If the current time is past the last received update, use a small amount of prediction
        bool usePrediction = factor >= 1.0f;
        if (usePrediction)
        {
            // Calculate how far past the last update we are (maximum 100ms prediction)
            long predictionTime = Math.Min(currentTime - _currentData.Timestamp, 100);
            
            // Use velocity information for more accurate prediction
            // Create a new prediction based on extrapolating from the current point
            AircraftData prediction = CreatePrediction(_currentData, predictionTime, usePrediction);
            return prediction;
        }
        
        // Otherwise use normal interpolation with some rate-aware smoothing
        
        // Create interpolated data
        var result = new AircraftData
        {
            Timestamp = currentTime,
            
            // Linear interpolation for position data
            Latitude = Lerp(_previousData.Latitude, _currentData.Latitude, factor),
            Longitude = Lerp(_previousData.Longitude, _currentData.Longitude, factor),
            Altitude = Lerp(_previousData.Altitude, _currentData.Altitude, factor),
            
            // Spherical interpolation for rotational data
            Pitch = SLerp(_previousData.Pitch, _currentData.Pitch, factor),
            Bank = SLerp(_previousData.Bank, _currentData.Bank, factor),
            Heading = AngleLerp(_previousData.Heading, _currentData.Heading, factor),
            
            // Linear interpolation for everything else
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
            
            // Calculated velocities using interpolation
            GroundSpeed = Lerp(_previousData.GroundSpeed, _currentData.GroundSpeed, factor),
            VerticalSpeed = Lerp(_previousData.VerticalSpeed, _currentData.VerticalSpeed, factor),
            AirspeedTrue = Lerp(_previousData.AirspeedTrue, _currentData.AirspeedTrue, factor),
            AirspeedIndicated = Lerp(_previousData.AirspeedIndicated, _currentData.AirspeedIndicated, factor),
            
            // Other non-interpolated state data
            OnGround = _currentData.OnGround,
            VelocityBodyX = Lerp(_previousData.VelocityBodyX, _currentData.VelocityBodyX, factor),
            VelocityBodyY = Lerp(_previousData.VelocityBodyY, _currentData.VelocityBodyY, factor),
            VelocityBodyZ = Lerp(_previousData.VelocityBodyZ, _currentData.VelocityBodyZ, factor),
            
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
        
        // Create quaternions
        Quaternion qA = Quaternion.CreateFromYawPitchRoll(0, 0, (float)angleA);
        Quaternion qB = Quaternion.CreateFromYawPitchRoll(0, 0, (float)angleB);
        
        // Spherical interpolation
        Quaternion result = Quaternion.Slerp(qA, qB, t);
        
        // Convert back to angle
        double slerpedAngle = Math.Atan2(2.0 * (result.W * result.Z + result.X * result.Y), 
                                        1.0 - 2.0 * (result.Y * result.Y + result.Z * result.Z));
        
        // Return in degrees
        return slerpedAngle * 180.0 / Math.PI;
    }
    
    // Special interpolation for angles that handles wrap-around (0-360)
    private double AngleLerp(double a, double b, float t)
    {
        // Find the shortest path
        double diff = ((b - a + 540) % 360) - 180;
        return (a + diff * t + 360) % 360;
    }

    // Create a prediction based on current position and velocities
    private AircraftData CreatePrediction(AircraftData currentData, long predictionTimeMs, bool fullPrediction)
    {
        // For stability, limit prediction time 
        predictionTimeMs = Math.Min(predictionTimeMs, 50); // Max 50ms prediction to prevent overshooting
        
        // Convert prediction time to seconds for physical calculations
        float predictionTimeSec = predictionTimeMs / 1000.0f;
        
        // Create a copy of the current data
        var prediction = new AircraftData
        {
            Timestamp = currentData.Timestamp + predictionTimeMs,
            
            // Copy all properties initially
            Latitude = currentData.Latitude,
            Longitude = currentData.Longitude,
            Altitude = currentData.Altitude,
            Pitch = currentData.Pitch,
            Bank = currentData.Bank,
            Heading = currentData.Heading,
            Throttle = currentData.Throttle,
            Aileron = currentData.Aileron,
            Elevator = currentData.Elevator,
            Rudder = currentData.Rudder,
            BrakeLeft = currentData.BrakeLeft,
            BrakeRight = currentData.BrakeRight,
            ParkingBrake = currentData.ParkingBrake,
            Mixture = currentData.Mixture,
            Flaps = currentData.Flaps,
            Gear = currentData.Gear,
            GroundSpeed = currentData.GroundSpeed,
            VerticalSpeed = currentData.VerticalSpeed,
            AirspeedTrue = currentData.AirspeedTrue,
            AirspeedIndicated = currentData.AirspeedIndicated,
            OnGround = currentData.OnGround,
            VelocityBodyX = currentData.VelocityBodyX,
            VelocityBodyY = currentData.VelocityBodyY,
            VelocityBodyZ = currentData.VelocityBodyZ,
            ElevatorTrimPosition = currentData.ElevatorTrimPosition,
            LightBeacon = currentData.LightBeacon,
            LightLanding = currentData.LightLanding,
            LightTaxi = currentData.LightTaxi,
            LightNav = currentData.LightNav,
            LightStrobe = currentData.LightStrobe,
            PitotHeat = currentData.PitotHeat
        };
        
        if (!fullPrediction)
            return prediction;
        
        // Simplified prediction - only update position based on velocity
        // Avoid predicting rotations which can cause oscillations
        
        // Update altitude based on vertical speed
        prediction.Altitude += currentData.VerticalSpeed * predictionTimeSec;
        
        // Update position based on ground speed and heading
        // Convert heading to radians for trig functions
        double headingRad = currentData.Heading * Math.PI / 180.0;
        
        // Calculate velocity components
        double eastVelocity = currentData.GroundSpeed * Math.Sin(headingRad);
        double northVelocity = currentData.GroundSpeed * Math.Cos(headingRad);
        
        // Earth's radius in meters
        const double earthRadius = 6371000.0;
        
        // Convert velocities to changes in latitude/longitude
        // This is a simplified calculation that works for short time periods
        double latChange = (northVelocity * predictionTimeSec) / earthRadius * (180.0 / Math.PI);
        double lonChange = (eastVelocity * predictionTimeSec) / (earthRadius * Math.Cos(prediction.Latitude * Math.PI / 180.0)) * (180.0 / Math.PI);
        
        prediction.Latitude += latChange;
        prediction.Longitude += lonChange;
        
        // Do NOT predict angular rates - too likely to cause oscillation
        
        return prediction;
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
    private const int MIN_UPDATE_INTERVAL = 50; // Changed from 30 to 50ms (~20fps) for more stability
    private const int RAPID_UPDATE_INTERVAL = 33; // ~30fps for turns/rapid changes

    // Added for adaptive throttling
    private static readonly Dictionary<string, AircraftData> _lastAircraftData = new();

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
            // Apply simpler, more stable throttling
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            // Check if we have previous data to compare
            if (_lastAircraftData.TryGetValue(Context.ConnectionId, out AircraftData prevData))
            {
                // Only calculate heading change - the most important for visual smoothness
                double headingChange = Math.Abs(data.Heading - prevData.Heading);
                if (headingChange > 180) headingChange = 360 - headingChange; // Handle 359->0 wraparound
                
                // Use a simpler fixed-interval approach based on turning vs straight flight
                int updateInterval = headingChange > 0.5 ? RAPID_UPDATE_INTERVAL : MIN_UPDATE_INTERVAL;
                
                if (!_lastSendTime.TryGetValue(Context.ConnectionId, out long lastSend) || (now - lastSend) >= updateInterval)
                {
                    // Set timestamp on server to ensure accuracy
                    data.Timestamp = now;
                    
                    _lastSendTime[Context.ConnectionId] = now;
                    _lastAircraftData[Context.ConnectionId] = data; // Store for next comparison
                    
                    // Reduce logging for performance
                    _logger.LogDebug("Sent data in session {SessionCode}", sessionCode);
                    
                    await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
                }
            }
            else
            {
                // First data packet, just send it immediately
                data.Timestamp = now;
                _lastSendTime[Context.ConnectionId] = now;
                _lastAircraftData[Context.ConnectionId] = data;
                
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