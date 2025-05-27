using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Threading;

var builder = WebApplication.CreateBuilder(args);

// Add console logging with reduced verbosity
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning); // Reduce log spam

// Add SignalR services with MAXIMUM buffer sizes and optimizations for ultra-stable connections
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 1024000; // 1MB - massive buffer
    options.StreamBufferCapacity = 50; // Large buffer capacity
    options.EnableDetailedErrors = false; // Reduce overhead
    options.KeepAliveInterval = TimeSpan.FromSeconds(5); // Aggressive keepalive
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(15); // Longer timeout
    options.HandshakeTimeout = TimeSpan.FromSeconds(10); // Longer handshake
    options.MaximumParallelInvocationsPerClient = 10; // Allow parallel calls
});

// Configure Kestrel for better performance
builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(options =>
{
    options.Limits.MaxConcurrentConnections = 1000;
    options.Limits.MaxConcurrentUpgradedConnections = 1000;
    options.Limits.MaxRequestBodySize = 1024000; // 1MB
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
});

var app = builder.Build();

// Use top-level route registration for the hub.
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

// Enhanced AircraftData class with physics properties and timestamp
public class AircraftData
{
    // Timestamp in UTC ticks for precise synchronization
    public long Timestamp { get; set; }
    
    // Aircraft position
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    
    // Aircraft attitude
    public double Pitch { get; set; }
    public double Bank { get; set; }
    public double Heading { get; set; }
    
    // Aircraft controls
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
    public double VelocityBodyX { get; set; }
    public double VelocityBodyY { get; set; }
    public double VelocityBodyZ { get; set; }
    public double OnGround { get; set; }
}

// The SignalR hub - BULLETPROOF
public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    private static int _messageCount = 0;

    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
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
            _logger.LogWarning("Client {ConnectionId} disconnected with error: {Error}", 
                Context.ConnectionId, exception.Message);
        }
        else
        {
            _logger.LogInformation("Client {ConnectionId} disconnected normally", Context.ConnectionId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task JoinSession(string sessionCode)
    {
        try
        {
            _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", 
                Context.ConnectionId, sessionCode);
            await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error joining session {SessionCode} for connection {ConnectionId}", 
                sessionCode, Context.ConnectionId);
            throw;
        }
    }

    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        try
        {
            // Ensure the data has a timestamp
            if (data.Timestamp == 0)
            {
                data.Timestamp = DateTime.UtcNow.Ticks;
            }
            
            // Increment message counter
            Interlocked.Increment(ref _messageCount);
            
            // Only log every 100th message to reduce spam but still monitor health
            if (_messageCount % 100 == 0)
            {
                _logger.LogInformation("Processed {MessageCount} messages. Latest from session {SessionCode}: Alt={Alt:F1}, GS={GS:F1}", 
                    _messageCount, sessionCode, data.Altitude, data.GroundSpeed);
            }
                
            // Send the data to all clients in the session group with error handling
            await Clients.Group(sessionCode).SendAsync("ReceiveAircraftData", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending aircraft data for session {SessionCode}", sessionCode);
            // Don't rethrow - we want to keep the connection alive
        }
    }
}
