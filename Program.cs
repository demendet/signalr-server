using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Add console logging
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Add SignalR services with needed buffer size
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 102400; // 100KB 
    options.StreamBufferCapacity = 20;
    options.EnableDetailedErrors = true; // For easier debugging
});

// Add CORS with the most permissive settings
builder.Services.AddCors(options => {
    options.AddDefaultPolicy(builder => {
        builder
            .SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

var app = builder.Build();

// Enable CORS
app.UseCors();

// IMPORTANT: Use the exact endpoint name the client is trying to connect to
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

// Aircraft data DTO that matches the client's expectations
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
    public double ParkingBrake { get; set; }
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
}

// DTO for light states
public class LightStatesDto
{
    public bool LightBeacon { get; set; }
    public bool LightLanding { get; set; }
    public bool LightTaxi { get; set; }
    public bool LightNav { get; set; }
    public bool LightStrobe { get; set; }
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

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
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

        if (!_sessionConnections[sessionCode].Contains(Context.ConnectionId))
        {
            _sessionConnections[sessionCode].Add(Context.ConnectionId);
        }

        // Assign control to the first client (host)
        if (!_sessionControlMap.ContainsKey(sessionCode))
        {
            _sessionControlMap[sessionCode] = Context.ConnectionId;
            await Clients.Caller.SendAsync("ControlStatusChanged", true);
            _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", 
                Context.ConnectionId, sessionCode);
        }
        else
        {
            // Tell the client if they have control
            bool hasControl = _sessionControlMap[sessionCode] == Context.ConnectionId;
            await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
            _logger.LogInformation("Notified client {ClientId} of control status: {HasControl}", 
                Context.ConnectionId, hasControl);
        }
    }

    public async Task SendAircraftData(string sessionCode, AircraftDataDto data)
    {
        if (_sessionControlMap.TryGetValue(sessionCode, out var controlId) && controlId == Context.ConnectionId)
        {
            _logger.LogDebug("Received data from controller in session {SessionCode}", sessionCode);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
        }
    }

    public async Task SendLightStates(string sessionCode, LightStatesDto lightStates)
    {
        _logger.LogDebug("Received light states in session {SessionCode}", sessionCode);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightStates", lightStates);
    }

    public async Task TransferControl(string sessionCode, bool giving)
    {
        string currentController = _sessionControlMap.GetValueOrDefault(sessionCode, "");

        if (giving) // Current controller is giving up control
        {
            if (currentController == Context.ConnectionId)
            {
                // Find another client in the session
                var otherClients = _sessionConnections[sessionCode]
                    .Where(id => id != Context.ConnectionId)
                    .ToList();

                if (otherClients.Any())
                {
                    var newController = otherClients.First();
                    _sessionControlMap[sessionCode] = newController;

                    // Notify both clients
                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);

                    _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", 
                        Context.ConnectionId, newController, sessionCode);
                }
            }
        }
        else // Someone is taking control
        {
            if (currentController != Context.ConnectionId)
            {
                var oldController = currentController;
                _sessionControlMap[sessionCode] = Context.ConnectionId;

                // Notify both clients
                await Clients.Caller.SendAsync("ControlStatusChanged", true);
                if (!string.IsNullOrEmpty(oldController))
                {
                    await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                }

                _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", 
                    Context.ConnectionId, oldController, sessionCode);
            }
        }
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);

        // Find sessions this client was in
        var sessions = _sessionConnections
            .Where(kvp => kvp.Value.Contains(Context.ConnectionId))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var session in sessions)
        {
            // Remove from session
            _sessionConnections[session].Remove(Context.ConnectionId);

            // If this client had control, reassign it
            if (_sessionControlMap.GetValueOrDefault(session) == Context.ConnectionId)
            {
                if (_sessionConnections[session].Any())
                {
                    var newController = _sessionConnections[session].First();
                    _sessionControlMap[session] = newController;
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    
                    _logger.LogInformation("Control automatically transferred to {NewId} in session {SessionCode}", 
                        newController, session);
                }
                else
                {
                    _sessionControlMap.Remove(session);
                }
            }

            // Clean up empty session
            if (_sessionConnections[session].Count == 0)
            {
                _sessionConnections.Remove(session);
                _sessionControlMap.Remove(session);
                _logger.LogInformation("Removed empty session {SessionCode}", session);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
} 
