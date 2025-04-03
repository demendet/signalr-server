using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Create WebApplication builder.
var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddConsole();
builder.Services.AddSignalR();

var app = builder.Build();
app.MapHub<CockpitHub>("/sharedcockpithub");
app.Run();

// Define AircraftData as a class with public properties.
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
    public int ParkingBrake { get; set; } // ParkingBrake as int (represents bool)
    public double Mixture { get; set; }
    public int Flaps { get; set; }
    public int Gear { get; set; }
    public double IndicatedAirspeed { get; set; } // new field (in knots)
    public double TrueAirspeed { get; set; }        // new field (in knots)
}

// SignalR Hub for broadcasting flight data.
public class CockpitHub : Microsoft.AspNetCore.SignalR.Hub
{
    private readonly ILogger<CockpitHub> _logger;
    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
    }

    public async System.Threading.Tasks.Task JoinSession(string sessionCode)
    {
        _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
    }

    public async System.Threading.Tasks.Task SendAircraftData(string sessionCode, AircraftData data)
    {
        _logger.LogInformation("Received aircraft data from host: {Data}", System.Text.Json.JsonSerializer.Serialize(data));
        await Clients.Group(sessionCode).SendAsync("ReceiveAircraftData", data);
        _logger.LogInformation("Broadcasted aircraft data to session {SessionCode}", sessionCode);
    }
}
