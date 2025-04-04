using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add console logging
builder.Logging.AddConsole();

// Add SignalR services with increased buffer size and JSON handling
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
})
.AddJsonProtocol(options => {
    // Allow serialization of special floating-point values
    options.PayloadSerializerOptions.NumberHandling = 
        JsonNumberHandling.AllowNamedFloatingPointLiterals;
});

var app = builder.Build();

// Use top-level route registration for the hub
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

// Enhanced AircraftData class with all properties
public class AircraftData
{
    // Position and Attitude
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public double Pitch { get; set; }
    public double Bank { get; set; }
    public double Heading { get; set; }
    
    // Controls
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
    
    // Physics data
    public double GroundSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public double AirspeedTrue { get; set; }
    public double AirspeedIndicated { get; set; }
    public double OnGround { get; set; }
    public double VelocityBodyX { get; set; }
    public double VelocityBodyY { get; set; }
    public double VelocityBodyZ { get; set; }
    
    // Trim Controls
    public double AileronTrim { get; set; }
    public double ElevatorTrim { get; set; }
    public double RudderTrim { get; set; }
    
    // Lights
    public double BeaconLight { get; set; }
    public double LandingLight { get; set; }
    public double TaxiLight { get; set; }
    public double NavLight { get; set; }
    public double StrobeLight { get; set; }
    public double PanelLight { get; set; }
    public double CabinLight { get; set; }
    
    // Electrical
    public double BatteryMaster { get; set; }
    public double AlternatorMaster { get; set; }
    public double AvionicsMaster { get; set; }
    public double BatteryVoltage { get; set; }
    public double BusVoltage { get; set; }
    
    // Engine
    public double EngineRPM { get; set; }
    public double EngineCombustion { get; set; }
    public double OilTemperature { get; set; }
    public double OilPressure { get; set; }
    public double EGT { get; set; }
    public double FuelFlow { get; set; }
    public double FuelQuantityLeft { get; set; }
    public double FuelQuantityRight { get; set; }
    
    // Radios
    public double Com1Active { get; set; }
    public double Com1Standby { get; set; }
    public double Nav1Active { get; set; }
    public double Nav1Standby { get; set; }
    public double Com2Active { get; set; }
    public double Com2Standby { get; set; }
    public double Nav2Active { get; set; }
    public double Nav2Standby { get; set; }
    public double TransponderCode { get; set; }
    
    // Autopilot
    public double APMaster { get; set; }
    public double APHeadingLock { get; set; }
    public double APHeadingValue { get; set; }
    public double APAltitudeLock { get; set; }
    public double APAltitudeValue { get; set; }
    public double APAirspeedLock { get; set; }
    public double APAirspeedValue { get; set; }
    public double APVerticalSpeedLock { get; set; }
    public double APVerticalSpeedValue { get; set; }
    public double APNavLock { get; set; }
    public double APApproachLock { get; set; }
    
    // G1000 Controls
    public double G1000ActivePage { get; set; }
    public double KohlsmanSetting { get; set; }
    public double HeadingBug { get; set; }
    public double AltitudeBug { get; set; }
    public double NAV1OBS { get; set; }
    public double NAV2OBS { get; set; }
    
    // G1000 Event
    public int G1000EventID { get; set; }
    public int G1000EventParam { get; set; }
}

// G1000 Event Data class for button presses
public class G1000EventData
{
    public int EventID { get; set; }
    public int EventParam { get; set; }
}

// The SignalR hub
public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;

    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinSession(string sessionCode)
    {
        _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
    }

    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        // Log minimally to avoid console spam
        _logger.LogInformation("Data from host in session {SessionCode}: Alt={Alt:F1}, RPM={RPM:F0}", 
            sessionCode, data.Altitude, data.EngineRPM);
            
        await Clients.Group(sessionCode).SendAsync("ReceiveAircraftData", data);
    }
    
    // New method for G1000 events
    public async Task SendG1000Event(string sessionCode, G1000EventData eventData)
    {
        _logger.LogInformation("G1000 Event from host in session {SessionCode}: ID={ID}, Param={Param}", 
            sessionCode, eventData.EventID, eventData.EventParam);
            
        await Clients.Group(sessionCode).SendAsync("ReceiveG1000Event", eventData);
    }
}