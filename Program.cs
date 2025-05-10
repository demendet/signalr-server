using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Add console logging
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Add SignalR services with optimized settings
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1048576; // 1MB
    options.StreamBufferCapacity = 64;
    options.KeepAliveInterval = TimeSpan.FromSeconds(5);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
    options.EnableDetailedErrors = true;
});

// Add CORS to allow client connections
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy",
        builder => builder
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials()
            .SetIsOriginAllowed(_ => true));
});

var app = builder.Build();

// Enable CORS
app.UseCors("CorsPolicy");

// Map the hub
app.MapHub<RailwayHub>("/railwayhub");

app.Run();

#region Data Transfer Objects

// Train data for synchronization
public class TrainData
{
    public string TrainId { get; set; }
    public double PositionX { get; set; }
    public double PositionY { get; set; }
    public double Speed { get; set; }
    public double Direction { get; set; }
    public bool IsMoving { get; set; }
    public double Throttle { get; set; }
    public double Brake { get; set; }
    public string CurrentSegment { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
}

// Signal and switch state data
public class NetworkElementData
{
    public string ElementId { get; set; }
    public string ElementType { get; set; } // Signal, Switch, Crossing, etc.
    public string State { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new Dictionary<string, string>();
}

// Client connection information
public class ClientInfo
{
    public string ClientId { get; set; }
    public string ClientName { get; set; }
    public string Role { get; set; } // Controller, Observer, etc.
    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
}

// Connection quality tracking
public enum ConnectionQuality
{
    Unknown = 0,
    Poor = 1,
    Fair = 2,
    Good = 3,
    Excellent = 4
}

#endregion

public class RailwayHub : Hub
{
    private readonly ILogger<RailwayHub> _logger;
    
    // Session management
    private static readonly Dictionary<string, List<string>> _sessionConnections = new();
    private static readonly Dictionary<string, string> _sessionControllers = new();
    private static readonly Dictionary<string, Dictionary<string, ClientInfo>> _sessionClients = new();
    
    // Data caching for late-joining clients
    private static readonly Dictionary<string, Dictionary<string, TrainData>> _sessionTrains = new();
    private static readonly Dictionary<string, Dictionary<string, NetworkElementData>> _sessionNetworkElements = new();
    
    // Connection health tracking
    private static readonly Dictionary<string, Dictionary<string, ConnectionQuality>> _connectionQualities = new();
    
    // Lock object for thread safety
    private static readonly object _lockObj = new object();
    
    public RailwayHub(ILogger<RailwayHub> logger)
    {
        _logger = logger;
    }
    
    public async Task JoinSession(string sessionId, string clientName, string role)
    {
        try
        {
            _logger.LogInformation("Client {ConnectionId} joined session {SessionId} as {Role}", 
                Context.ConnectionId, sessionId, role);
                
            // Add client to the session group
            await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
            
            lock (_lockObj)
            {
                // Initialize session collections if needed
                if (!_sessionConnections.ContainsKey(sessionId))
                {
                    _sessionConnections[sessionId] = new List<string>();
                    _sessionClients[sessionId] = new Dictionary<string, ClientInfo>();
                    _sessionTrains[sessionId] = new Dictionary<string, TrainData>();
                    _sessionNetworkElements[sessionId] = new Dictionary<string, NetworkElementData>();
                    _connectionQualities[sessionId] = new Dictionary<string, ConnectionQuality>();
                }
                
                // Add client to session
                if (!_sessionConnections[sessionId].Contains(Context.ConnectionId))
                {
                    _sessionConnections[sessionId].Add(Context.ConnectionId);
                }
                
                // Store client info
                _sessionClients[sessionId][Context.ConnectionId] = new ClientInfo
                {
                    ClientId = Context.ConnectionId,
                    ClientName = clientName,
                    Role = role
                };
                
                // Initialize connection quality
                _connectionQualities[sessionId][Context.ConnectionId] = ConnectionQuality.Good;
                
                // Assign controller if this is the first client or has controller role
                bool isFirstClient = _sessionConnections[sessionId].Count == 1;
                bool isController = false;
                
                if ((isFirstClient || role.Equals("controller", StringComparison.OrdinalIgnoreCase)) && 
                    !_sessionControllers.ContainsKey(sessionId))
                {
                    _sessionControllers[sessionId] = Context.ConnectionId;
                    isController = true;
                    _logger.LogInformation("Control assigned to {ControlId} in session {SessionId}", 
                        Context.ConnectionId, sessionId);
                }
                else
                {
                    isController = _sessionControllers.TryGetValue(sessionId, out var controllerId) && 
                        controllerId == Context.ConnectionId;
                }
            }
            
            // Notify the client of their current control status
            await Clients.Caller.SendAsync("ControlStatusChanged", 
                _sessionControllers.TryGetValue(sessionId, out var controller) && controller == Context.ConnectionId);
            
            // Notify everyone about the new client
            await Clients.GroupExcept(sessionId, Context.ConnectionId).SendAsync("ClientJoined", 
                new { ClientId = Context.ConnectionId, ClientName = clientName, Role = role });
            
            // Send current session state to the new client
            await SendSessionState(sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in JoinSession for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    private async Task SendSessionState(string sessionId)
    {
        // Send the current controller
        if (_sessionControllers.TryGetValue(sessionId, out var controllerId))
        {
            var controllerInfo = _sessionClients[sessionId][controllerId];
            await Clients.Caller.SendAsync("ControllerInfo", new { 
                ClientId = controllerId, 
                ClientName = controllerInfo.ClientName,
                Role = controllerInfo.Role
            });
        }
        
        // Send all connected clients
        var clients = _sessionClients[sessionId].Values.ToList();
        await Clients.Caller.SendAsync("ClientList", clients);
        
        // Send all train data
        var trains = _sessionTrains[sessionId].Values.ToList();
        if (trains.Count > 0)
        {
            await Clients.Caller.SendAsync("TrainDataSync", trains);
        }
        
        // Send all network element data
        var elements = _sessionNetworkElements[sessionId].Values.ToList();
        if (elements.Count > 0)
        {
            await Clients.Caller.SendAsync("NetworkElementSync", elements);
        }
    }
    
    public async Task UpdateTrainData(string sessionId, TrainData data)
    {
        try
        {
            // Only allow from controller
            if (!IsController(sessionId))
            {
                _logger.LogWarning("Non-controller client {ClientId} attempted to update train data", 
                    Context.ConnectionId);
                return;
            }
            
            // Store the updated train data
            lock (_lockObj)
            {
                _sessionTrains[sessionId][data.TrainId] = data;
            }
            
            // Broadcast to all other clients in the session
            await Clients.GroupExcept(sessionId, Context.ConnectionId).SendAsync("TrainDataUpdated", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in UpdateTrainData for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task UpdateNetworkElement(string sessionId, NetworkElementData data)
    {
        try
        {
            // Only allow from controller
            if (!IsController(sessionId))
            {
                _logger.LogWarning("Non-controller client {ClientId} attempted to update network element", 
                    Context.ConnectionId);
                return;
            }
            
            // Store the updated element data
            lock (_lockObj)
            {
                _sessionNetworkElements[sessionId][data.ElementId] = data;
            }
            
            // Broadcast to all other clients in the session
            await Clients.GroupExcept(sessionId, Context.ConnectionId).SendAsync("NetworkElementUpdated", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in UpdateNetworkElement for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task RequestControl(string sessionId)
    {
        try
        {
            bool controlGranted = false;
            string previousController = null;
            
            lock (_lockObj)
            {
                // Check if client already has control
                if (_sessionControllers.TryGetValue(sessionId, out var currentController) && 
                    currentController == Context.ConnectionId)
                {
                    controlGranted = true;
                }
                else
                {
                    // Store previous controller for notification
                    previousController = currentController;
                    
                    // Grant control to the requesting client
                    _sessionControllers[sessionId] = Context.ConnectionId;
                    controlGranted = true;
                }
            }
            
            if (controlGranted)
            {
                // Notify the new controller
                await Clients.Caller.SendAsync("ControlStatusChanged", true);
                
                // Notify the previous controller if there was one
                if (!string.IsNullOrEmpty(previousController))
                {
                    await Clients.Client(previousController).SendAsync("ControlStatusChanged", false);
                }
                
                // Get the client info for the new controller
                ClientInfo controllerInfo = null;
                lock (_lockObj)
                {
                    if (_sessionClients.TryGetValue(sessionId, out var clients) && 
                        clients.TryGetValue(Context.ConnectionId, out var info))
                    {
                        controllerInfo = info;
                    }
                }
                
                // Notify all clients about the control change
                await Clients.Group(sessionId).SendAsync("ControllerChanged", new {
                    ClientId = Context.ConnectionId,
                    ClientName = controllerInfo?.ClientName,
                    Role = controllerInfo?.Role
                });
                
                _logger.LogInformation("Control transferred to {NewController} from {OldController}",
                    Context.ConnectionId, previousController);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in RequestControl for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task ReleaseControl(string sessionId)
    {
        try
        {
            // Check if this client currently has control
            if (!IsController(sessionId))
            {
                return;
            }
            
            string newController = null;
            
            lock (_lockObj)
            {
                // Remove this client as controller
                _sessionControllers.Remove(sessionId);
                
                // Find another client to give control to (if any)
                if (_sessionConnections.TryGetValue(sessionId, out var connections) && connections.Count > 0)
                {
                    // Skip the current client
                    var otherClients = connections.Where(c => c != Context.ConnectionId).ToList();
                    if (otherClients.Count > 0)
                    {
                        // For simplicity, give control to the first client
                        newController = otherClients.First();
                        _sessionControllers[sessionId] = newController;
                    }
                }
            }
            
            // Notify this client that it no longer has control
            await Clients.Caller.SendAsync("ControlStatusChanged", false);
            
            // If a new controller was assigned, notify them
            if (!string.IsNullOrEmpty(newController))
            {
                await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                
                // Get the client info for the new controller
                ClientInfo controllerInfo = null;
                lock (_lockObj)
                {
                    if (_sessionClients.TryGetValue(sessionId, out var clients) && 
                        clients.TryGetValue(newController, out var info))
                    {
                        controllerInfo = info;
                    }
                }
                
                // Notify all clients about the control change
                await Clients.Group(sessionId).SendAsync("ControllerChanged", new {
                    ClientId = newController,
                    ClientName = controllerInfo?.ClientName,
                    Role = controllerInfo?.Role
                });
                
                _logger.LogInformation("Control automatically transferred to {NewController} after release by {OldController}",
                    newController, Context.ConnectionId);
            }
            else
            {
                // Notify all clients that no one has control
                await Clients.Group(sessionId).SendAsync("ControllerChanged", null);
                _logger.LogInformation("Control released by {Controller} with no new controller assigned",
                    Context.ConnectionId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ReleaseControl for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    public async Task SendHeartbeat(string sessionId)
    {
        try
        {
            // Update client's last activity time
            lock (_lockObj)
            {
                if (_sessionClients.TryGetValue(sessionId, out var clients) &&
                    clients.TryGetValue(Context.ConnectionId, out var clientInfo))
                {
                    clientInfo.LastActivity = DateTime.UtcNow;
                }
            }
            
            // Send heartbeat acknowledgment
            await Clients.Caller.SendAsync("HeartbeatAck");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendHeartbeat for {ConnectionId}", Context.ConnectionId);
        }
    }
    
    private bool IsController(string sessionId)
    {
        return _sessionControllers.TryGetValue(sessionId, out var controllerId) && 
            controllerId == Context.ConnectionId;
    }
    
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        try
        {
            // Find all sessions this client is part of
            var sessionsToProcess = new List<string>();
            
            lock (_lockObj)
            {
                foreach (var kvp in _sessionConnections)
                {
                    if (kvp.Value.Contains(Context.ConnectionId))
                    {
                        sessionsToProcess.Add(kvp.Key);
                    }
                }
            }
            
            foreach (var sessionId in sessionsToProcess)
            {
                // Remove client from session
                string clientName = "";
                lock (_lockObj)
                {
                    // Get client info before removing
                    if (_sessionClients.TryGetValue(sessionId, out var clients) &&
                        clients.TryGetValue(Context.ConnectionId, out var clientInfo))
                    {
                        clientName = clientInfo.ClientName;
                    }
                    
                    // Remove from collections
                    if (_sessionConnections.TryGetValue(sessionId, out var connections))
                    {
                        connections.Remove(Context.ConnectionId);
                    }
                    
                    if (_sessionClients.TryGetValue(sessionId, out var sessionClients))
                    {
                        sessionClients.Remove(Context.ConnectionId);
                    }
                    
                    if (_connectionQualities.TryGetValue(sessionId, out var qualities))
                    {
                        qualities.Remove(Context.ConnectionId);
                    }
                }
                
                // Notify other clients about this client disconnecting
                await Clients.Group(sessionId).SendAsync("ClientLeft", new { 
                    ClientId = Context.ConnectionId, 
                    ClientName = clientName 
                });
                
                // Check if this client had control
                bool hadControl = false;
                lock (_lockObj)
                {
                    if (_sessionControllers.TryGetValue(sessionId, out var controllerId) && 
                        controllerId == Context.ConnectionId)
                    {
                        hadControl = true;
                        _sessionControllers.Remove(sessionId);
                    }
                }
                
                // If this client had control, find a new controller
                if (hadControl)
                {
                    string newController = null;
                    ClientInfo newControllerInfo = null;
                    
                    lock (_lockObj)
                    {
                        if (_sessionConnections.TryGetValue(sessionId, out var connections) && 
                            connections.Count > 0)
                        {
                            newController = connections.First();
                            _sessionControllers[sessionId] = newController;
                            
                            if (_sessionClients.TryGetValue(sessionId, out var clients) &&
                                clients.TryGetValue(newController, out var clientInfo))
                            {
                                newControllerInfo = clientInfo;
                            }
                        }
                    }
                    
                    if (!string.IsNullOrEmpty(newController))
                    {
                        // Notify the new controller
                        await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                        
                        // Notify all clients about the control change
                        await Clients.Group(sessionId).SendAsync("ControllerChanged", new {
                            ClientId = newController,
                            ClientName = newControllerInfo?.ClientName,
                            Role = newControllerInfo?.Role
                        });
                        
                        _logger.LogInformation("Control automatically transferred to {NewController} after disconnect of {OldController}",
                            newController, Context.ConnectionId);
                    }
                    else
                    {
                        // Notify all clients that no one has control
                        await Clients.Group(sessionId).SendAsync("ControllerChanged", null);
                    }
                }
                
                // Clean up empty sessions
                lock (_lockObj)
                {
                    if (_sessionConnections.TryGetValue(sessionId, out var connections) && 
                        connections.Count == 0)
                    {
                        _sessionConnections.Remove(sessionId);
                        _sessionClients.Remove(sessionId);
                        _sessionTrains.Remove(sessionId);
                        _sessionNetworkElements.Remove(sessionId);
                        _connectionQualities.Remove(sessionId);
                        _sessionControllers.Remove(sessionId);
                        
                        _logger.LogInformation("Removed empty session {SessionId}", sessionId);
                    }
                }
            }
            
            _logger.LogInformation("Client {ConnectionId} disconnected", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnDisconnectedAsync for {ConnectionId}", Context.ConnectionId);
            await base.OnDisconnectedAsync(exception);
        }
    }
} 
