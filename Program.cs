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

// Add SignalR services with increased buffer size for smoother data flow
builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
    options.EnableDetailedErrors = true; // Help with debugging
});

// Add CORS for client connections
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

// Use top-level route registration for the hub.
app.MapHub<CockpitHub>("/cockpithub");

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

// Simple DTO for variable changes
public class VariableChangeDto
{
    public string VariableName { get; set; }
    public string VariableType { get; set; }
    public string Value { get; set; }
    public string SourceClientId { get; set; }
}

// The SignalR hub
public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    private static readonly Dictionary<string, string> _sessionControlMap = new();
    private static readonly Dictionary<string, List<string>> _sessionConnections = new();
    private static readonly Dictionary<string, Dictionary<string, VariableChangeDto>> _sessionVariableValues = new();

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
            _sessionVariableValues[sessionCode] = new Dictionary<string, VariableChangeDto>();
        }
        _sessionConnections[sessionCode].Add(Context.ConnectionId);

        // By default, the first connection (host) has control
        if (!_sessionControlMap.ContainsKey(sessionCode))
        {
            _sessionControlMap[sessionCode] = Context.ConnectionId;
            // Inform the client they have control (host always starts with control)
            await Clients.Caller.SendAsync("ControlStatusChanged", true);
            _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", 
                Context.ConnectionId, sessionCode);
        }
        else
        {
            // Inform joining client about current control status
            bool hasControl = _sessionControlMap[sessionCode] == Context.ConnectionId;
            await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
            _logger.LogInformation("Notified joining client {ClientId} of control status ({HasControl}) in session {SessionCode}", 
                Context.ConnectionId, hasControl, sessionCode);
        }

        // Send current controller info to the new client
        string currentController = _sessionControlMap.GetValueOrDefault(sessionCode, "");
        bool controllerIsHost = _sessionConnections[sessionCode].FirstOrDefault() == currentController;
        await Clients.Caller.SendAsync("ControlInfo", currentController, controllerIsHost ? "host" : "client");

        // Send all stored variable values to the new client
        if (_sessionVariableValues.TryGetValue(sessionCode, out var variables) && variables.Count > 0)
        {
            foreach (var variable in variables.Values)
            {
                await Clients.Caller.SendAsync("ReceiveVariableChange", variable);
            }
            _logger.LogInformation("Sent {Count} cached variables to client {ClientId}", variables.Count, Context.ConnectionId);
        }
    }

    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        // Verify this client has control before broadcasting data
        if (_sessionControlMap.TryGetValue(sessionCode, out var controlId) && controlId == Context.ConnectionId)
        {
            // Only log essential info to avoid console spam
            _logger.LogInformation("Received data from controller in session {SessionCode}: Alt={Alt:F1}, GS={GS:F1}", 
                sessionCode, data.Altitude, data.GroundSpeed);

            // Send to all others in the session
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
        }
    }

    public async Task SendVariableChange(string sessionCode, VariableChangeDto variable)
    {
        try
        {
            // Add source client ID for tracking
            variable.SourceClientId = Context.ConnectionId;
            
            _logger.LogDebug("Variable change from {ConnectionId}: {VariableName}={Value}", 
                Context.ConnectionId, variable.VariableName, variable.Value);
            
            // Store the variable value for this session
            if (_sessionVariableValues.TryGetValue(sessionCode, out var variables))
            {
                variables[variable.VariableName] = variable;
            }
            
            // Send to all clients in the session except the sender
            await Clients.GroupExcept(sessionCode, Context.ConnectionId).SendAsync("ReceiveVariableChange", variable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendVariableChange for {ConnectionId}", Context.ConnectionId);
        }
    }

    public async Task TransferControl(string sessionCode, bool giving)
    {
        string currentController = _sessionControlMap.GetValueOrDefault(sessionCode, "");

        if (giving) // Host is giving control
        {
            if (currentController == Context.ConnectionId)
            {
                // Find other clients in this session
                var otherConnections = _sessionConnections[sessionCode]
                    .Where(id => id != Context.ConnectionId)
                    .ToList();

                if (otherConnections.Any())
                {
                    var newController = otherConnections.First();
                    _sessionControlMap[sessionCode] = newController;

                    // Notify both parties
                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);

                    // Notify all clients about the control transfer
                    await Clients.Group(sessionCode).SendAsync("ControlTransferred", newController, "client");

                    _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", 
                        Context.ConnectionId, newController, sessionCode);
                }
            }
        }
        else // Client is taking control
        {
            if (currentController != Context.ConnectionId)
            {
                var oldController = currentController;
                _sessionControlMap[sessionCode] = Context.ConnectionId;

                // Notify both parties
                await Clients.Caller.SendAsync("ControlStatusChanged", true);
                if (!string.IsNullOrEmpty(oldController))
                    await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);

                // Notify all clients about the control transfer
                var controllerType = _sessionConnections[sessionCode].FirstOrDefault() == Context.ConnectionId ? "host" : "client";
                await Clients.Group(sessionCode).SendAsync("ControlTransferred", Context.ConnectionId, controllerType);

                _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", 
                    Context.ConnectionId, oldController, sessionCode);
            }
        }
    }

    public async Task RequestVariableSync(string sessionCode)
    {
        try
        {
            // Send all variable values for this session to the requesting client
            if (_sessionVariableValues.TryGetValue(sessionCode, out var variables) && variables.Count > 0)
            {
                foreach (var variable in variables.Values)
                {
                    await Clients.Caller.SendAsync("ReceiveVariableChange", variable);
                }
                _logger.LogInformation("Synced {Count} variables to client {ClientId}", variables.Count, Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in RequestVariableSync for {ConnectionId}", Context.ConnectionId);
        }
    }

    public override async Task OnDisconnectedAsync(Exception exception)
    {
        _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);

        // Find any sessions this client is part of
        var sessions = _sessionConnections
            .Where(kvp => kvp.Value.Contains(Context.ConnectionId))
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var session in sessions)
        {
            // Remove client from the session
            _sessionConnections[session].Remove(Context.ConnectionId);

            // If this was the controlling client, reassign control
            if (_sessionControlMap.GetValueOrDefault(session) == Context.ConnectionId)
            {
                if (_sessionConnections[session].Any())
                {
                    var newController = _sessionConnections[session].First();
                    _sessionControlMap[session] = newController;
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    
                    // Notify all clients about the control transfer
                    var controllerType = _sessionConnections[session].FirstOrDefault() == newController ? "host" : "client";
                    await Clients.Group(session).SendAsync("ControlTransferred", newController, controllerType);
                    
                    _logger.LogInformation("Control automatically transferred to {NewId} after disconnect in session {SessionCode}", 
                        newController, session);
                }
                else
                {
                    // If no other connections, remove the session
                    _sessionControlMap.Remove(session);
                    _logger.LogInformation("Session {SessionCode} removed as all clients disconnected", session);
                }
            }

            // If session is empty, clean up
            if (!_sessionConnections[session].Any())
            {
                _sessionConnections.Remove(session);
                _sessionControlMap.Remove(session);
                _sessionVariableValues.Remove(session);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }
}
