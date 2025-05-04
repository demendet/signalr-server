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
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 102400; // 100KB
    options.StreamBufferCapacity = 20; // Increase buffer capacity
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
    public int SubIndex { get; set; } // 0=master, 1=FD, 2=HDG, 3=NAV, 4=APR, 5=ALT, 6=VS, 7=FLC, 8=HDG setting, 9=ALT setting
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
        _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", Context.ConnectionId, sessionCode);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
        
        if (!_sessionConnections.ContainsKey(sessionCode))
            _sessionConnections[sessionCode] = new List<string>();
        _sessionConnections[sessionCode].Add(Context.ConnectionId);
        
        if (!_sessionControlMap.ContainsKey(sessionCode))
        {
            _sessionControlMap[sessionCode] = Context.ConnectionId;
            await Clients.Caller.SendAsync("ControlStatusChanged", true);
            _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", Context.ConnectionId, sessionCode);
        }
        else
        {
            bool hasControl = _sessionControlMap[sessionCode] == Context.ConnectionId;
            await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
            _logger.LogInformation("Notified joining client {ClientId} of control status ({HasControl}) in session {SessionCode}", Context.ConnectionId, hasControl, sessionCode);
        }
    }
    
    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        if (_sessionControlMap.TryGetValue(sessionCode, out string controlId) && controlId == Context.ConnectionId)
        {
            _logger.LogInformation("Received data in session {SessionCode}: Alt={Alt:F1}", sessionCode, data.Altitude);
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
        }
    }
    
    public async Task SendLightStates(string sessionCode, LightStatesDto lights)
    {
        _logger.LogInformation("Received light states in session {SessionCode}", sessionCode);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightStates", lights);
    }
    
    public async Task SendPitotHeatState(string sessionCode, PitotHeatStateDto state)
    {
        _logger.LogInformation("Received pitot heat state in session {SessionCode}: {State}", sessionCode, state.PitotHeatOn);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceivePitotHeatState", state);
    }
    
    public async Task SendG1000SoftkeyPress(string sessionCode, G1000SoftkeyPressDto press)
    {
        _logger.LogInformation("Received G1000 softkey press in session {SessionCode}: {Number}", sessionCode, press.SoftkeyNumber);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveG1000SoftkeyPress", press);
    }
    
    // New methods for G1000 avionics synchronization
    public async Task SendRadioFrequencyChange(string sessionCode, RadioFrequencyChangeDto change)
    {
        _logger.LogInformation("Received radio frequency change in session {SessionCode}: {Radio} subIndex={SubIndex}", 
            sessionCode, change.RadioType, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveRadioFrequencyChange", change);
    }

    public async Task SendTransponderChange(string sessionCode, TransponderChangeDto change)
    {
        _logger.LogInformation("Received transponder change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveTransponderChange", change);
    }

    public async Task SendAdfChange(string sessionCode, AdfChangeDto change)
    {
        _logger.LogInformation("Received ADF change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAdfChange", change);
    }

    public async Task SendObsChange(string sessionCode, ObsChangeDto change)
    {
        _logger.LogInformation("Received OBS change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveObsChange", change);
    }

    public async Task SendAvionicsChange(string sessionCode, AvionicsChangeDto change)
    {
        _logger.LogInformation("Received avionics change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAvionicsChange", change);
    }

    public async Task SendElectricalMasterChange(string sessionCode, ElectricalMasterChangeDto change)
    {
        _logger.LogInformation("Received electrical master change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveElectricalMasterChange", change);
    }

    public async Task SendLightChange(string sessionCode, LightChangeDto change)
    {
        _logger.LogInformation("Received light change in session {SessionCode}: subIndex={SubIndex}, value={Value}", 
            sessionCode, change.SubIndex, change.Value);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightChange", change);
    }

    public async Task SendAutopilotChange(string sessionCode, AutopilotChangeDto change)
    {
        _logger.LogInformation("Received autopilot change in session {SessionCode}: subIndex={SubIndex}", 
            sessionCode, change.SubIndex);
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAutopilotChange", change);
    }
    
    public async Task TransferControl(string sessionCode, bool giving)
    {
        string currentController = _sessionControlMap.GetValueOrDefault(sessionCode, "");
        if (giving)
        {
            if (currentController == Context.ConnectionId)
            {
                var otherConnections = _sessionConnections[sessionCode].Where(id => id != Context.ConnectionId).ToList();
                if (otherConnections.Any())
                {
                    var newController = otherConnections.First();
                    _sessionControlMap[sessionCode] = newController;
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
                _sessionControlMap[sessionCode] = Context.ConnectionId;
                await Clients.Caller.SendAsync("ControlStatusChanged", true);
                if (!string.IsNullOrEmpty(oldController))
                    await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", Context.ConnectionId, oldController, sessionCode);
            }
        }
    }
    
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        var sessions = _sessionConnections.Where(kvp => kvp.Value.Contains(Context.ConnectionId))
                                           .Select(kvp => kvp.Key)
                                           .ToList();
        foreach (var session in sessions)
        {
            _sessionConnections[session].Remove(Context.ConnectionId);
            if (_sessionControlMap.GetValueOrDefault(session) == Context.ConnectionId)
            {
                if (_sessionConnections[session].Any())
                {
                    var newController = _sessionConnections[session].First();
                    _sessionControlMap[session] = newController;
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    _logger.LogInformation("Control automatically transferred to {NewId} in session {SessionCode}", newController, session);
                }
                else
                {
                    _sessionControlMap.Remove(session);
                    _sessionConnections.Remove(session);
                    _logger.LogInformation("Session {SessionCode} removed as all clients disconnected", session);
                }
            }
            if (!_sessionConnections[session].Any())
            {
                _sessionConnections.Remove(session);
                _sessionControlMap.Remove(session);
            }
        }
        await base.OnDisconnectedAsync(exception);
    }
}