using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

// Add console logging
builder.Logging.AddConsole();

// Add SignalR services
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 1024000; // 1MB for audio data
    options.EnableDetailedErrors = true;
});

// Add CORS for cross-origin requests
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// Use CORS
app.UseCors();

// Map the hub
app.MapHub<MissionHub>("/missionhub");

app.Run();

// --- Data Transfer Objects ---

public class AircraftDataDto
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }
    public double Heading { get; set; }
    public double Airspeed { get; set; }
    public double VerticalSpeed { get; set; }
    public string Callsign { get; set; } = string.Empty;
    public string AircraftTitle { get; set; } = string.Empty;
    public double Weight { get; set; }
    public DateTime LastUpdate { get; set; }
}

public class MissionDto
{
    public string MissionId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "pickup", "transport", "emergency"
    public string PassengerName { get; set; } = string.Empty;
    public string PassengerType { get; set; } = string.Empty; // "tourist", "drunk", "emergency"
    public LocationDto DepartureLocation { get; set; } = new();
    public LocationDto DestinationLocation { get; set; } = new();
    public DateTime ScheduledTime { get; set; }
    public string Status { get; set; } = "pending"; // pending, accepted, in_progress, completed
    public string AcceptedBy { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsUrgent { get; set; }
    public double PaymentAmount { get; set; }
    public string Language { get; set; } = "norwegian"; // norwegian, english
}

public class LocationDto
{
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class PhoneCallDto
{
    public string CallId { get; set; } = string.Empty;
    public string CallerName { get; set; } = string.Empty;
    public string CallerType { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime StartTime { get; set; }
    public string SessionCode { get; set; } = string.Empty;
    public string AssignedTo { get; set; } = string.Empty;
}

public class ConversationMessageDto
{
    public string CallId { get; set; } = string.Empty;
    public string Speaker { get; set; } = string.Empty; // "pilot", "passenger"
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsAudioMessage { get; set; }
}

public class PilotStatusDto
{
    public string PilotId { get; set; } = string.Empty;
    public string Callsign { get; set; } = string.Empty;
    public bool IsReady { get; set; }
    public bool IsOnMission { get; set; }
    public LocationDto CurrentLocation { get; set; } = new();
    public DateTime LastUpdate { get; set; }
}

// --- Session Management ---
public class SessionInfo
{
    public HashSet<string> ConnectionIds { get; set; } = new();
    public Dictionary<string, PilotStatusDto> Pilots { get; set; } = new();
    public List<MissionDto> Missions { get; set; } = new();
    public List<PhoneCallDto> ActiveCalls { get; set; } = new();
    public DateTime LastActivity { get; set; } = DateTime.UtcNow;
    public string CurrentMissionId { get; set; } = string.Empty;
}

// --- SignalR Hub Implementation ---
public class MissionHub : Hub
{
    private readonly ILogger<MissionHub> _logger;
    
    private static readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private static readonly ConcurrentDictionary<string, string> _connectionToSession = new();
    private static readonly ConcurrentDictionary<string, string> _connectionToPilotId = new();
    private static readonly object _lockObject = new object();

    public MissionHub(ILogger<MissionHub> logger)
    {
        _logger = logger;
    }

    // Session Management
    public async Task JoinSession(string sessionCode, string pilotId, string callsign)
    {
        if (string.IsNullOrWhiteSpace(sessionCode) || string.IsNullOrWhiteSpace(pilotId))
        {
            _logger.LogWarning("Connection {ConnectionId} attempted to join with invalid parameters", Context.ConnectionId);
            return;
        }

        sessionCode = sessionCode.ToLowerInvariant().Trim();
        
        lock (_lockObject)
        {
            // Remove from old session if exists
            if (_connectionToSession.TryGetValue(Context.ConnectionId, out string? existingSession))
            {
                LeaveSessionInternal(Context.ConnectionId, existingSession);
            }

            // Get or create session
            var session = _sessions.GetOrAdd(sessionCode, _ => new SessionInfo());
            
            // Add connection to session
            session.ConnectionIds.Add(Context.ConnectionId);
            session.LastActivity = DateTime.UtcNow;
            
            // Add pilot info
            session.Pilots[pilotId] = new PilotStatusDto
            {
                PilotId = pilotId,
                Callsign = callsign,
                IsReady = false,
                IsOnMission = false,
                LastUpdate = DateTime.UtcNow
            };
            
            _connectionToSession[Context.ConnectionId] = sessionCode;
            _connectionToPilotId[Context.ConnectionId] = pilotId;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        _logger.LogInformation("Pilot {PilotId} ({Callsign}) joined session {SessionCode}", pilotId, callsign, sessionCode);
        
        // Notify others about the new pilot
        await Clients.OthersInGroup(sessionCode).SendAsync("PilotJoined", pilotId, callsign);
        
        // Send current session state to the new pilot
        var currentSession = _sessions[sessionCode];
        await Clients.Caller.SendAsync("SessionState", currentSession.Pilots.Values, currentSession.Missions, currentSession.ActiveCalls);
    }

    // Pilot Status Updates
    public async Task UpdatePilotStatus(bool isReady, LocationDto? currentLocation = null)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out string? sessionCode) ||
            !_connectionToPilotId.TryGetValue(Context.ConnectionId, out string? pilotId))
            return;

        lock (_lockObject)
        {
            if (_sessions.TryGetValue(sessionCode, out SessionInfo? session) &&
                session.Pilots.TryGetValue(pilotId, out PilotStatusDto? pilot))
            {
                pilot.IsReady = isReady;
                if (currentLocation != null)
                    pilot.CurrentLocation = currentLocation;
                pilot.LastUpdate = DateTime.UtcNow;
                session.LastActivity = DateTime.UtcNow;
            }
        }

        await Clients.OthersInGroup(sessionCode).SendAsync("PilotStatusUpdated", pilotId, isReady, currentLocation);
        
        _logger.LogInformation("Pilot {PilotId} status updated: Ready={IsReady}", pilotId, isReady);
    }

    // Aircraft Data Sharing
    public async Task SendAircraftData(AircraftDataDto data)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out string? sessionCode))
            return;
            
        lock (_lockObject)
        {
            if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
            {
                session.LastActivity = DateTime.UtcNow;
            }
        }
        
        // Relay aircraft data to all other pilots in the session
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
    }

    // Mission Management
    public async Task CreateMission(MissionDto mission)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out string? sessionCode))
            return;

        mission.MissionId = Guid.NewGuid().ToString();
        mission.Status = "pending";

        lock (_lockObject)
        {
            if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
            {
                session.Missions.Add(mission);
                session.LastActivity = DateTime.UtcNow;
            }
        }

        await Clients.Group(sessionCode).SendAsync("NewMission", mission);
        
        _logger.LogInformation("New mission created: {MissionId} - {Type} from {Departure} to {Destination}", 
            mission.MissionId, mission.Type, mission.DepartureLocation.Name, mission.DestinationLocation.Name);
    }

    public async Task AcceptMission(string missionId)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out string? sessionCode) ||
            !_connectionToPilotId.TryGetValue(Context.ConnectionId, out string? pilotId))
            return;

        lock (_lockObject)
        {
            if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
            {
                var mission = session.Missions.FirstOrDefault(m => m.MissionId == missionId);
                if (mission != null && mission.Status == "pending")
                {
                    mission.Status = "accepted";
                    mission.AcceptedBy = pilotId;
                    session.CurrentMissionId = missionId;
                    
                    // Update pilot status
                    if (session.Pilots.TryGetValue(pilotId, out PilotStatusDto? pilot))
                    {
                        pilot.IsOnMission = true;
                    }
                }
                session.LastActivity = DateTime.UtcNow;
            }
        }

        await Clients.Group(sessionCode).SendAsync("MissionAccepted", missionId, pilotId);
        
        _logger.LogInformation("Mission {MissionId} accepted by pilot {PilotId}", missionId, pilotId);
    }

    public async Task UpdateMissionStatus(string missionId, string status)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out string? sessionCode))
            return;

        lock (_lockObject)
        {
            if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
            {
                var mission = session.Missions.FirstOrDefault(m => m.MissionId == missionId);
                if (mission != null)
                {
                    mission.Status = status;
                    
                    // If mission is completed, update pilot status
                    if (status == "completed" && session.Pilots.TryGetValue(mission.AcceptedBy, out PilotStatusDto? pilot))
                    {
                        pilot.IsOnMission = false;
                        pilot.IsReady = true;
                    }
                }
                session.LastActivity = DateTime.UtcNow;
            }
        }

        await Clients.Group(sessionCode).SendAsync("MissionStatusUpdated", missionId, status);
        
        _logger.LogInformation("Mission {MissionId} status updated to {Status}", missionId, status);
    }

    // Phone Call Management
    public async Task StartPhoneCall(PhoneCallDto phoneCall)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out string? sessionCode))
            return;

        phoneCall.CallId = Guid.NewGuid().ToString();
        phoneCall.SessionCode = sessionCode;
        phoneCall.StartTime = DateTime.UtcNow;
        phoneCall.IsActive = true;

        lock (_lockObject)
        {
            if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
            {
                session.ActiveCalls.Add(phoneCall);
                session.LastActivity = DateTime.UtcNow;
            }
        }

        // Notify all pilots about incoming call
        await Clients.Group(sessionCode).SendAsync("IncomingCall", phoneCall);
        
        _logger.LogInformation("Phone call started: {CallId} - {CallerName} ({CallerType})", 
            phoneCall.CallId, phoneCall.CallerName, phoneCall.CallerType);
    }

    public async Task AnswerCall(string callId)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out string? sessionCode) ||
            !_connectionToPilotId.TryGetValue(Context.ConnectionId, out string? pilotId))
            return;

        lock (_lockObject)
        {
            if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
            {
                var call = session.ActiveCalls.FirstOrDefault(c => c.CallId == callId);
                if (call != null)
                {
                    call.AssignedTo = pilotId;
                }
                session.LastActivity = DateTime.UtcNow;
            }
        }

        await Clients.Group(sessionCode).SendAsync("CallAnswered", callId, pilotId);
        
        _logger.LogInformation("Call {CallId} answered by pilot {PilotId}", callId, pilotId);
    }

    public async Task HangUpCall(string callId)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out string? sessionCode))
            return;

        lock (_lockObject)
        {
            if (_sessions.TryGetValue(sessionCode, out SessionInfo? session))
            {
                var call = session.ActiveCalls.FirstOrDefault(c => c.CallId == callId);
                if (call != null)
                {
                    call.IsActive = false;
                    session.ActiveCalls.Remove(call);
                }
                session.LastActivity = DateTime.UtcNow;
            }
        }

        await Clients.Group(sessionCode).SendAsync("CallEnded", callId);
        
        _logger.LogInformation("Call {CallId} ended", callId);
    }

    // Conversation Management
    public async Task SendConversationMessage(ConversationMessageDto message)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out string? sessionCode))
            return;

        message.Timestamp = DateTime.UtcNow;

        // Relay conversation message to all participants
        await Clients.Group(sessionCode).SendAsync("ConversationMessage", message);
        
        _logger.LogDebug("Conversation message sent for call {CallId}: {Speaker} - {Message}", 
            message.CallId, message.Speaker, message.Message);
    }

    // Audio Data Relay
    public async Task SendAudioData(string callId, byte[] audioData, string audioFormat)
    {
        if (!_connectionToSession.TryGetValue(Context.ConnectionId, out string? sessionCode))
            return;

        // Relay audio data to other participants
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAudioData", callId, audioData, audioFormat);
    }

    // Cleanup on disconnect
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        string? sessionCode = null;
        string? pilotId = null;
        
        lock (_lockObject)
        {
            _connectionToSession.TryRemove(Context.ConnectionId, out sessionCode);
            _connectionToPilotId.TryRemove(Context.ConnectionId, out pilotId);
            
            if (sessionCode != null)
            {
                LeaveSessionInternal(Context.ConnectionId, sessionCode);
            }
        }
        
        if (sessionCode != null && pilotId != null)
        {
            await Clients.Group(sessionCode).SendAsync("PilotLeft", pilotId);
        }
        
        if (exception != null)
        {
            _logger.LogWarning("Connection {ConnectionId} disconnected with exception: {Exception}", Context.ConnectionId, exception.Message);
        }
        else
        {
            _logger.LogInformation("Pilot {PilotId} disconnected from session {SessionCode}", pilotId, sessionCode);
        }
        
        await base.OnDisconnectedAsync(exception);
    }
    
    private void LeaveSessionInternal(string connectionId, string sessionCode)
    {
        if (!_sessions.TryGetValue(sessionCode, out SessionInfo? session)) 
            return;
        
        session.ConnectionIds.Remove(connectionId);
        
        // Clean up empty sessions
        if (!session.ConnectionIds.Any())
        {
            _sessions.TryRemove(sessionCode, out _);
            _logger.LogInformation("Session {SessionCode} removed as all pilots disconnected", sessionCode);
        }
    }
} 