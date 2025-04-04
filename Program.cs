using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// Add console logging.
builder.Logging.AddConsole();

// Add SignalR services with increased buffer size for smoother data flow
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
});

var app = builder.Build();

// Use top-level route registration for the hub.
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

// Enhanced AircraftData class with physics properties
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
    // Light states
    public double BeaconLights { get; set; }
    public double LandingLights { get; set; }
    public double TaxiLights { get; set; }
    public double NavLights { get; set; }
    public double StrobeLights { get; set; }
    // Source identifier for bidirectional communication
    public string Source { get; set; } = "Host";
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
        // Log based on source to better understand traffic
        if (data.Source == "Host")
        {
            _logger.LogInformation("Host data in session {SessionCode}: Alt={Alt:F1}, GS={GS:F1}, IAS={IAS:F1}", 
                sessionCode, data.Altitude, data.GroundSpeed, data.AirspeedIndicated);
        }
        else
        {
            _logger.LogInformation("Client light data in session {SessionCode}: " +
                "Beacon={Beacon}, Landing={Landing}, Taxi={Taxi}, Nav={Nav}, Strobe={Strobe}", 
                sessionCode, data.BeaconLights, data.LandingLights, data.TaxiLights, data.NavLights, data.StrobeLights);
        }
            
        // Send the data to all clients in the group (including the sender)
        await Clients.Group(sessionCode).SendAsync("ReceiveAircraftData", data);
    }
}