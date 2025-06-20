using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// Add console logging.
builder.Logging.AddConsole();

// Add SignalR services optimized for the new buffered interpolation system
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
    options.KeepAliveInterval = TimeSpan.FromSeconds(10); // Reduced from default 15s for better responsiveness
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(25); // Reduced from default 30s
    options.EnableDetailedErrors = true; // Enable detailed errors for debugging
    options.MaximumParallelInvocationsPerClient = 2; // Allow parallel processing
});

var app = builder.Build();

// Use top-level route registration for the hub.
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

// Enhanced AircraftDataDto class with physics properties and CRITICAL timestamp
public class AircraftDataDto
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
    
    // CRITICAL: Timestamp for buffered interpolation system
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

// The SignalR hub with enhanced error handling and performance monitoring
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

    public async Task SendAircraftData(string sessionCode, AircraftDataDto data)
    {
        // Ensure timestamp is set (fallback if client doesn't set it)
        if (data.Timestamp == 0)
        {
            data.Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        
        // Only log essential info to avoid console spam, but include timing info
        _logger.LogInformation("Host data in session {SessionCode}: Alt={Alt:F1}, GS={GS:F1}, Timestamp={Timestamp}", 
            sessionCode, data.Altitude, data.GroundSpeed, data.Timestamp);
            
        await Clients.Group(sessionCode).SendAsync("ReceiveAircraftData", data);
    }
    
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client {ConnectionId} connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception != null)
        {
            _logger.LogWarning("Client {ConnectionId} disconnected with error: {Error}", Context.ConnectionId, exception.Message);
        }
        else
        {
            _logger.LogInformation("Client {ConnectionId} disconnected cleanly", Context.ConnectionId);
        }
        await base.OnDisconnectedAsync(exception);
    }
} 