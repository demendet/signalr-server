using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Add console logging
builder.Logging.AddConsole();

// Add SignalR services with increased buffer size for smoother data flow
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
});

// Add CORS policy to allow client connections
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials() // Important for SignalR
              .SetIsOriginAllowed(_ => true); // Allow any origin
    });
});

var app = builder.Build();

// Enable CORS
app.UseCors();

// Use top-level route registration for the hub
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

// Enhanced AircraftData class with physics properties and lights
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
    
    // Aircraft lights
    public double LightBeacon { get; set; }   // 0.0 = OFF, 1.0 = ON
    public double LightLanding { get; set; }  // 0.0 = OFF, 1.0 = ON  
    public double LightTaxi { get; set; }     // 0.0 = OFF, 1.0 = ON
    public double LightNav { get; set; }      // 0.0 = OFF, 1.0 = ON
    public double LightStrobe { get; set; }   // 0.0 = OFF, 1.0 = ON
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

// DTO for pitot heat state
public class PitotHeatDto
{
    public bool IsOn { get; set; }
    public System.DateTime Timestamp { get; set; } = System.DateTime.UtcNow;
}

// The SignalR hub
public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    private static readonly Dictionary<string, string> _sessionControlMap = new();
    private static readonly Dictionary<string, HashSet<string>> _sessionMembers = new();

    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
    }

    // Create a new session
    public async Task CreateSession(string sessionCode)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        _logger.LogInformation("Client {ConnectionId} created session {SessionCode}", Context.ConnectionId, sessionCode);
        
        // Add to session members tracking
        if (!_sessionMembers.ContainsKey(sessionCode))
        {
            _sessionMembers[sessionCode] = new HashSet<string>();
        }
        _sessionMembers[sessionCode].Add(Context.ConnectionId);
        
        // Set this user as the controller
        _sessionControlMap[sessionCode] = Context.ConnectionId;
        
        await Clients.Caller.SendAsync("SessionCreated", sessionCode);
    }

    // Join an existing session
    public async Task JoinSession(string sessionCode)
    {
        if (!_sessionMembers.ContainsKey(sessionCode))
        {
            _logger.LogWarning("Client {ConnectionId} tried to join non-existent session {SessionCode}", Context.ConnectionId, sessionCode);
            await Clients.Caller.SendAsync("Error", $"Session {sessionCode} does not exist");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        _logger.LogInformation("Client {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
        
        // Add to session members tracking
        _sessionMembers[sessionCode].Add(Context.ConnectionId);
        
        await Clients.Caller.SendAsync("SessionJoined", sessionCode);
        await Clients.OthersInGroup(sessionCode).SendAsync("MemberJoined", Context.ConnectionId);
    }

    // Leave a session
    public async Task LeaveSession(string sessionCode)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, sessionCode);
        _logger.LogInformation("Client {ConnectionId} left session {SessionCode}", Context.ConnectionId, sessionCode);
        
        // Remove from session members tracking
        if (_sessionMembers.ContainsKey(sessionCode))
        {
            _sessionMembers[sessionCode].Remove(Context.ConnectionId);
            
            // If this was the controller, reassign or clean up
            if (_sessionControlMap.ContainsKey(sessionCode) && _sessionControlMap[sessionCode] == Context.ConnectionId)
            {
                if (_sessionMembers[sessionCode].Count > 0)
                {
                    // Assign control to another member
                    _sessionControlMap[sessionCode] = _sessionMembers[sessionCode].First();
                    await Clients.Client(_sessionControlMap[sessionCode]).SendAsync("ControlGranted");
                }
                else
                {
                    // No more members, remove session
                    _sessionControlMap.Remove(sessionCode);
                    _sessionMembers.Remove(sessionCode);
                }
            }
        }
        
        await Clients.Group(sessionCode).SendAsync("MemberLeft", Context.ConnectionId);
    }

    // Send aircraft data to other clients in the same session
    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        _logger.LogDebug("Received aircraft data from {ConnectionId} for session {SessionCode}", Context.ConnectionId, sessionCode);
        
        // Send to all other clients in the session
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
    }

    // Send light states to other clients in the same session
    public async Task SendLightStates(string sessionCode, LightStatesDto lights)
    {
        _logger.LogInformation("Received light states from client in session {SessionCode}: B={Beacon}, L={Landing}, T={Taxi}, N={Nav}, S={Strobe}", 
            sessionCode, lights.LightBeacon, lights.LightLanding, lights.LightTaxi, lights.LightNav, lights.LightStrobe);
        
        // Send to all other clients in the session
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightStates", lights);
    }

    // Send pitot heat state to other clients in the same session
    public async Task SendPitotHeatState(string sessionCode, PitotHeatDto state)
    {
        _logger.LogInformation("Received pitot heat state from client in session {SessionCode}: IsOn={IsOn}", 
            sessionCode, state.IsOn);
        
        // Send to all other clients in the session
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceivePitotHeatState", state);
    }

    // Override to handle client disconnections
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Find all sessions this user is part of
        var sessionsToLeave = _sessionMembers
            .Where(kvp => kvp.Value.Contains(Context.ConnectionId))
            .Select(kvp => kvp.Key)
            .ToList();
        
        // Leave each session
        foreach (var sessionCode in sessionsToLeave)
        {
            await LeaveSession(sessionCode);
        }
        
        await base.OnDisconnectedAsync(exception);
    }
}