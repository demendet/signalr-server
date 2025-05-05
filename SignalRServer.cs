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

// Add SignalR services with increased buffer size for smoother data flow
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
});

var app = builder.Build();

// Map the hub
app.MapHub<AircraftHub>("/aircrafthub");

app.Run();

// --- Aircraft Variable Data Transfer Objects ---

/// <summary>
/// Data Transfer Object for a single aircraft variable
/// </summary>
public class AircraftVariableDto
{
    /// <summary>
    /// Unique identifier matching the profile variable
    /// </summary>
    public string Id { get; set; }

    /// <summary>
    /// Value as a string (can represent any type)
    /// </summary>
    public string StringValue { get; set; }

    /// <summary>
    /// Value as a double (for most numeric variables)
    /// </summary>
    public double? NumericValue { get; set; }

    /// <summary>
    /// Value as a boolean
    /// </summary>
    public bool? BoolValue { get; set; }
}

/// <summary>
/// Collection of variable DTOs for batch updates
/// </summary>
public class AircraftVariablesUpdateDto
{
    /// <summary>
    /// The profile ID this update belongs to
    /// </summary>
    public string ProfileId { get; set; }

    /// <summary>
    /// Collection of variables being updated
    /// </summary>
    public List<AircraftVariableDto> Variables { get; set; } = new List<AircraftVariableDto>();
}

/// <summary>
/// Session information for connected clients
/// </summary>
public class SessionInfo
{
    /// <summary>
    /// The session code (group name)
    /// </summary>
    public string SessionCode { get; set; }
    
    /// <summary>
    /// ConnectionId that currently has control
    /// </summary>
    public string ControllingConnectionId { get; set; }
    
    /// <summary>
    /// All connected clients in this session
    /// </summary>
    public List<string> ConnectedClients { get; set; } = new List<string>();
}

/// <summary>
/// SignalR hub for aircraft data synchronization
/// </summary>
public class AircraftHub : Hub
{
    private readonly ILogger<AircraftHub> _logger;
    private static readonly Dictionary<string, SessionInfo> _sessions = new();

    public AircraftHub(ILogger<AircraftHub> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Join a session with the given session code
    /// </summary>
    public async Task JoinSession(string sessionCode)
    {
        if (string.IsNullOrEmpty(sessionCode))
        {
            _logger.LogWarning("Client {ConnectionId} attempted to join with empty session code", Context.ConnectionId);
            return;
        }

        _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
        
        // Add the client to the SignalR group
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        // Create or update session info
        if (!_sessions.TryGetValue(sessionCode, out SessionInfo session))
        {
            session = new SessionInfo
            {
                SessionCode = sessionCode,
                ControllingConnectionId = Context.ConnectionId
            };
            _sessions[sessionCode] = session;
            
            await Clients.Caller.SendAsync("ControlStatusChanged", true);
            _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", 
                Context.ConnectionId, sessionCode);
        }
        
        // Add the client to the connected clients list
        session.ConnectedClients.Add(Context.ConnectionId);
        
        // Send control status to the joining client
        bool hasControl = session.ControllingConnectionId == Context.ConnectionId;
        await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
        
        _logger.LogInformation("Client {ClientId} joined session {SessionCode} with control status: {HasControl}", 
            Context.ConnectionId, sessionCode, hasControl);
    }
    
    /// <summary>
    /// Send a batch of aircraft variables to other clients in the session
    /// </summary>
    public async Task SendAircraftVariables(string sessionCode, AircraftVariablesUpdateDto update)
    {
        if (string.IsNullOrEmpty(sessionCode) || update == null || update.Variables == null || update.Variables.Count == 0)
        {
            return;
        }
        
        // Check if the session exists and this client has control
        if (!_sessions.TryGetValue(sessionCode, out SessionInfo session) || 
            session.ControllingConnectionId != Context.ConnectionId)
        {
            return;
        }
        
        _logger.LogInformation("Received {Count} aircraft variables in session {SessionCode} for profile {ProfileId}", 
            update.Variables.Count, sessionCode, update.ProfileId);
            
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftVariables", update);
    }
    
    /// <summary>
    /// Send a single aircraft variable to other clients in the session
    /// </summary>
    public async Task SendAircraftVariable(string sessionCode, string profileId, AircraftVariableDto variable)
    {
        if (string.IsNullOrEmpty(sessionCode) || variable == null)
        {
            return;
        }
        
        // Check if the session exists and this client has control
        if (!_sessions.TryGetValue(sessionCode, out SessionInfo session) || 
            session.ControllingConnectionId != Context.ConnectionId)
        {
            return;
        }
            
        _logger.LogInformation("Received aircraft variable update in session {SessionCode}: {Id}={Value}", 
            sessionCode, variable.Id, variable.StringValue);
            
        var update = new AircraftVariablesUpdateDto
        {
            ProfileId = profileId,
            Variables = new List<AircraftVariableDto> { variable }
        };
            
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftVariables", update);
    }
    
    /// <summary>
    /// Send a system event (like button press) to other clients in the session
    /// </summary>
    public async Task SendSystemEvent(string sessionCode, string eventType, object eventData)
    {
        if (string.IsNullOrEmpty(sessionCode) || string.IsNullOrEmpty(eventType))
        {
            return;
        }
        
        _logger.LogInformation("Received system event {EventType} in session {SessionCode}", 
            eventType, sessionCode);
            
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveSystemEvent", eventType, eventData);
    }
    
    /// <summary>
    /// Transfer or take control in a session
    /// </summary>
    public async Task TransferControl(string sessionCode, bool giving)
    {
        if (string.IsNullOrEmpty(sessionCode) || !_sessions.TryGetValue(sessionCode, out SessionInfo session))
        {
            return;
        }
        
        if (giving)
        {
            // Only the controlling client can give control
            if (session.ControllingConnectionId == Context.ConnectionId)
            {
                var otherClients = session.ConnectedClients.Where(id => id != Context.ConnectionId).ToList();
                if (otherClients.Any())
                {
                    // Transfer control to the first other client
                    var newController = otherClients.First();
                    session.ControllingConnectionId = newController;
                    
                    // Notify clients of the control change
                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    
                    _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", 
                        Context.ConnectionId, newController, sessionCode);
                }
            }
        }
        else // Taking control
        {
            // Can't take control if already controlling
            if (session.ControllingConnectionId != Context.ConnectionId)
            {
                string oldController = session.ControllingConnectionId;
                session.ControllingConnectionId = Context.ConnectionId;
                
                // Notify clients of the control change
                await Clients.Caller.SendAsync("ControlStatusChanged", true);
                
                if (!string.IsNullOrEmpty(oldController))
                    await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                
                _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", 
                    Context.ConnectionId, oldController, sessionCode);
            }
        }
    }
    
    /// <summary>
    /// Handle client disconnection
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        // Find all sessions this client is part of
        var clientSessions = _sessions.Values
            .Where(s => s.ConnectedClients.Contains(Context.ConnectionId))
            .ToList();
            
        foreach (var session in clientSessions)
        {
            // Remove the client from connected clients
            session.ConnectedClients.Remove(Context.ConnectionId);
            
            // If this client had control, transfer it to another client
            if (session.ControllingConnectionId == Context.ConnectionId)
            {
                if (session.ConnectedClients.Any())
                {
                    // Transfer control to the first remaining client
                    var newController = session.ConnectedClients.First();
                    session.ControllingConnectionId = newController;
                    
                    // Notify the new controller
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    
                    _logger.LogInformation("Control automatically transferred to {NewId} in session {SessionCode}", 
                        newController, session.SessionCode);
                }
                else
                {
                    // No clients left, remove the session
                    _sessions.Remove(session.SessionCode);
                    
                    _logger.LogInformation("Session {SessionCode} removed as all clients disconnected", 
                        session.SessionCode);
                }
            }
            
            // If no clients remain, clean up the session
            if (!session.ConnectedClients.Any())
            {
                _sessions.Remove(session.SessionCode);
            }
        }
        await base.OnDisconnectedAsync(exception);
    }
} 