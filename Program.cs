using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// Add console logging.
builder.Logging.AddConsole();

// Add SignalR services.
builder.Services.AddSignalR();

var app = builder.Build();

// Use top-level route registration for the hub.
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();


// Define AircraftData as a class with public properties.
// Note: ParkingBrake is an int to match SimConnect's INT32 "bool" and we include IndicatedAirspeed.
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
    public int ParkingBrake { get; set; }  // Changed from double to int.
    public double Mixture { get; set; }
    public int Flaps { get; set; }
    public int Gear { get; set; }
    public double IndicatedAirspeed { get; set; } // New field for IAS.
}

// The SignalR hub.
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
        _logger.LogInformation("Received aircraft data from host: {Data}", System.Text.Json.JsonSerializer.Serialize(data));
        await Clients.Group(sessionCode).SendAsync("ReceiveAircraftData", data);
        _logger.LogInformation("Broadcasted aircraft data to session {SessionCode}", sessionCode);
    }
}
