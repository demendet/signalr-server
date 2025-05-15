using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Add console logging with increased detail for SignalR
builder.Logging.AddConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore.SignalR", LogLevel.Debug);
builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Connections", LogLevel.Debug);

// Add SignalR services with increased buffer size and timeouts for smoother data flow
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
    options.EnableDetailedErrors = true; // Enable detailed error messages
    options.HandshakeTimeout = TimeSpan.FromSeconds(30); // Increase handshake timeout
    options.KeepAliveInterval = TimeSpan.FromSeconds(10); // Keep-alive interval
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(30); // Client timeout
})
.AddJsonProtocol(options => {
    options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

// Map the hub
app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

// --- Data Transfer Objects and Hub Implementation ---

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
    public double ParkingBrake { get; set; }
    public double Mixture { get; set; }
    public int Flaps { get; set; }
    public int Gear { get; set; }
    public double GroundSpeed { get; set; }
    public double VerticalSpeed { get; set; }
    public double AirspeedTrue { get; set; }
    public double AirspeedIndicated { get; set; }
    public double OnGround { get; set; }
    public double VelocityBodyX { get; set; }
    public double VelocityBodyY { get; set; }
    public double VelocityBodyZ { get; set; }
    public double ElevatorTrimPosition { get; set; }
    public double LightBeacon { get; set; }
    public double LightLanding { get; set; }
    public double LightTaxi { get; set; }
    public double LightNav { get; set; }
    public double LightStrobe { get; set; }
    public double PitotHeat { get; set; }
}

public class LightStatesDto
{
    public bool LightBeacon { get; set; }
    public bool LightLanding { get; set; }
    public bool LightTaxi { get; set; }
    public bool LightNav { get; set; }
    public bool LightStrobe { get; set; }
}

public class PitotHeatStateDto
{
    public bool PitotHeatOn { get; set; }
}

public class G1000SoftkeyPressDto
{
    public int SoftkeyNumber { get; set; }
}

// New DTOs for G1000 avionics synchronization
public class RadioFrequencyChangeDto
{
    public string RadioType { get; set; } // "NAV1", "NAV2", "COM1", "COM2"
    public int SubIndex { get; set; } // 0=active, 1=standby, 2=swap
    public double Value { get; set; } // Frequency value or 1.0 for swap
}

public class TransponderChangeDto
{
    public int SubIndex { get; set; } // 0=code, 1=mode, 2=ident
    public double Value { get; set; } // Code value, mode value, or 1.0 for ident
}

public class AdfChangeDto
{
    public int SubIndex { get; set; } // 0=frequency, 1=card
    public double Value { get; set; } // Frequency or card value
}

public class ObsChangeDto
{
    public int SubIndex { get; set; } // 0=NAV1, 1=NAV2, 2=GPS
    public double Value { get; set; } // OBS value in degrees
}

public class AvionicsChangeDto
{
    public int SubIndex { get; set; } // 0=master, 1=bus1, 2=bus2
    public double Value { get; set; } // 0.0 or 1.0 for off/on
}

public class ElectricalMasterChangeDto
{
    public int SubIndex { get; set; } // 0=battery, 1=alternator
    public double Value { get; set; } // 0.0 or 1.0 for off/on
}

public class LightChangeDto
{
    public int SubIndex { get; set; } // 0=nav, 1=beacon, 2=landing, 3=taxi, 4=strobe, 5=panel, 6=pitot heat
    public double Value { get; set; } // 0.0 or 1.0 for off/on
}

public class AutopilotChangeDto
{
    public int SubIndex { get; set; } // 0=master, 1=FD, 2=HDG, 3=NAV, 4=APR, 5=ALT, 6=VS, 7=FLC, 8=HDG setting, 9=ALT s
    public double Value { get; set; } // 0.0 or 1.0 for mode toggles, or actual values for settings
}

public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;
    private static readonly Dictionary<string, string> _sessionControlMap = new();
    private static readonly Dictionary<string, List<string>> _sessionConnections = new();

    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinSession(string sessionCode)
    {
        try
        {
            if (string.IsNullOrEmpty(sessionCode))
            {
                _logger.LogError("Session code cannot be null or empty for connection {ConnectionId}", Context.ConnectionId);
                throw new ArgumentException("Session code cannot be null or empty");
            }

            _logger.LogInformation("Connection {ConnectionId} joining session {SessionCode}", Context.ConnectionId, sessionCode);
            
            // Add to SignalR group with error handling
            try
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
                _logger.LogInformation("Successfully added connection {ConnectionId} to group {SessionCode}", Context.ConnectionId, sessionCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding connection {ConnectionId} to group {SessionCode}", Context.ConnectionId, sessionCode);
                throw;
            }
            
            lock (_sessionConnections)
            {
                if (!_sessionConnections.ContainsKey(sessionCode))
                    _sessionConnections[sessionCode] = new List<string>();
                _sessionConnections[sessionCode].Add(Context.ConnectionId);
            }
            
            // Assign control if needed
            bool hasControl = false;
            lock (_sessionControlMap)
            {
                if (!_sessionControlMap.ContainsKey(sessionCode))
                {
                    _sessionControlMap[sessionCode] = Context.ConnectionId;
                    hasControl = true;
                    _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", Context.ConnectionId, sessionCode);
                }
                else
                {
                    hasControl = _sessionControlMap[sessionCode] == Context.ConnectionId;
                }
            }
            
            await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
            _logger.LogInformation("Notified client {ClientId} of control status ({HasControl}) in session {SessionCode}", Context.ConnectionId, hasControl, sessionCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in JoinSession for connection {ConnectionId}, session {SessionCode}", Context.ConnectionId, sessionCode ?? "null");
            throw; // Rethrow to send error to client
        }
    }
    
    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        try
        {
            if (string.IsNullOrEmpty(sessionCode))
            {
                _logger.LogWarning("SendAircraftData called with empty session code from {ConnectionId}", Context.ConnectionId);
                return;
            }
            
            if (_sessionControlMap.TryGetValue(sessionCode, out string controlId) && controlId == Context.ConnectionId)
            {
                _logger.LogInformation("Received data in session {SessionCode}: Alt={Alt:F1}", sessionCode, data.Altitude);
                await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendAircraftData for session {SessionCode}", sessionCode);
        }
    }
    
    public async Task SendLightStates(string sessionCode, LightStatesDto lights)
    {
        try
        {
            _logger.LogInformation("Received light states in session {SessionCode}", sessionCode);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightStates", lights);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendLightStates for session {SessionCode}", sessionCode);
        }
    }
    
    public async Task SendPitotHeatState(string sessionCode, PitotHeatStateDto state)
    {
        try
        {
            _logger.LogInformation("Received pitot heat state in session {SessionCode}: {State}", sessionCode, state.PitotHeatOn);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceivePitotHeatState", state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendPitotHeatState for session {SessionCode}", sessionCode);
        }
    }
    
    public async Task SendG1000SoftkeyPress(string sessionCode, G1000SoftkeyPressDto press)
    {
        try
        {
            _logger.LogInformation("Received G1000 softkey press in session {SessionCode}: {Number}", sessionCode, press.SoftkeyNumber);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveG1000SoftkeyPress", press);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendG1000SoftkeyPress for session {SessionCode}", sessionCode);
        }
    }
    
    // New methods for G1000 avionics synchronization
    public async Task SendRadioFrequencyChange(string sessionCode, RadioFrequencyChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received radio frequency change in session {SessionCode}: {Radio} subIndex={SubIndex}", 
                sessionCode, change.RadioType, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveRadioFrequencyChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendRadioFrequencyChange for session {SessionCode}", sessionCode);
        }
    }

    public async Task SendTransponderChange(string sessionCode, TransponderChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received transponder change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveTransponderChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendTransponderChange for session {SessionCode}", sessionCode);
        }
    }

    public async Task SendAdfChange(string sessionCode, AdfChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received ADF change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAdfChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendAdfChange for session {SessionCode}", sessionCode);
        }
    }

    public async Task SendObsChange(string sessionCode, ObsChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received OBS change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveObsChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendObsChange for session {SessionCode}", sessionCode);
        }
    }

    public async Task SendAvionicsChange(string sessionCode, AvionicsChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received avionics change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAvionicsChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendAvionicsChange for session {SessionCode}", sessionCode);
        }
    }

    public async Task SendElectricalMasterChange(string sessionCode, ElectricalMasterChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received electrical master change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveElectricalMasterChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendElectricalMasterChange for session {SessionCode}", sessionCode);
        }
    }

    public async Task SendLightChange(string sessionCode, LightChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received light change in session {SessionCode}: subIndex={SubIndex}, value={Value}", 
                sessionCode, change.SubIndex, change.Value);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendLightChange for session {SessionCode}", sessionCode);
        }
    }

    public async Task SendAutopilotChange(string sessionCode, AutopilotChangeDto change)
    {
        try
        {
            _logger.LogInformation("Received autopilot change in session {SessionCode}: subIndex={SubIndex}", 
                sessionCode, change.SubIndex);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAutopilotChange", change);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in SendAutopilotChange for session {SessionCode}", sessionCode);
        }
    }
    
    public async Task TransferControl(string sessionCode, bool giving)
    {
        try
        {
            if (string.IsNullOrEmpty(sessionCode))
            {
                _logger.LogWarning("TransferControl called with empty session code from {ConnectionId}", Context.ConnectionId);
                return;
            }
            
            string currentController;
            lock (_sessionControlMap)
            {
                _sessionControlMap.TryGetValue(sessionCode, out currentController);
            }
            
            if (giving)
            {
                if (currentController == Context.ConnectionId)
                {
                    List<string> otherConnections;
                    lock (_sessionConnections)
                    {
                        otherConnections = _sessionConnections.TryGetValue(sessionCode, out var connections) 
                            ? connections.Where(id => id != Context.ConnectionId).ToList()
                            : new List<string>();
                    }
                    
                    if (otherConnections.Any())
                    {
                        var newController = otherConnections.First();
                        lock (_sessionControlMap)
                        {
                            _sessionControlMap[sessionCode] = newController;
                        }
                        
                        await Clients.Caller.SendAsync("ControlStatusChanged", false);
                        await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                        _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", Context.ConnectionId, newController, sessionCode);
                    }
                }
            }
            else
            {
                if (currentController != Context.ConnectionId)
                {
                    string oldController = currentController;
                    lock (_sessionControlMap)
                    {
                        _sessionControlMap[sessionCode] = Context.ConnectionId;
                    }
                    
                    await Clients.Caller.SendAsync("ControlStatusChanged", true);
                    if (!string.IsNullOrEmpty(oldController))
                        await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                    _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", Context.ConnectionId, oldController, sessionCode);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TransferControl for session {SessionCode}", sessionCode);
        }
    }
    
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        try
        {
            List<string> sessions;
            lock (_sessionConnections)
            {
                sessions = _sessionConnections.Where(kvp => kvp.Value.Contains(Context.ConnectionId))
                                               .Select(kvp => kvp.Key)
                                               .ToList();
            }
            
            foreach (var session in sessions)
            {
                lock (_sessionConnections)
                {
                    if (_sessionConnections.TryGetValue(session, out var connections))
                    {
                        connections.Remove(Context.ConnectionId);
                    }
                }
                
                string controlId;
                lock (_sessionControlMap)
                {
                    _sessionControlMap.TryGetValue(session, out controlId);
                }
                
                if (controlId == Context.ConnectionId)
                {
                    List<string> remainingConnections;
                    lock (_sessionConnections)
                    {
                        remainingConnections = _sessionConnections.TryGetValue(session, out var connections) 
                            ? connections 
                            : new List<string>();
                    }
                    
                    if (remainingConnections.Any())
                    {
                        var newController = remainingConnections.First();
                        lock (_sessionControlMap)
                        {
                            _sessionControlMap[session] = newController;
                        }
                        
                        await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                        _logger.LogInformation("Control automatically transferred to {NewId} in session {SessionCode}", newController, session);
                    }
                    else
                    {
                        lock (_sessionControlMap)
                        {
                            _sessionControlMap.Remove(session);
                        }
                        
                        lock (_sessionConnections)
                        {
                            _sessionConnections.Remove(session);
                        }
                        
                        _logger.LogInformation("Session {SessionCode} removed as all clients disconnected", session);
                    }
                }
                
                bool anyConnections;
                lock (_sessionConnections)
                {
                    anyConnections = _sessionConnections.TryGetValue(session, out var connections) && connections.Any();
                    if (!anyConnections)
                    {
                        _sessionConnections.Remove(session);
                    }
                }
                
                if (!anyConnections)
                {
                    lock (_sessionControlMap)
                    {
                        _sessionControlMap.Remove(session);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnDisconnectedAsync for connection {ConnectionId}", Context.ConnectionId);
        }
        
        await base.OnDisconnectedAsync(exception);
    }
} 