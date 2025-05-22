using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();

builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 102400;
    options.StreamBufferCapacity = 20;
});

var app = builder.Build();

app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

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
}

public class LightStatesDto
{
    public bool LightBeacon { get; set; }
    public bool LightLanding { get; set; }
    public bool LightTaxi { get; set; }
    public bool LightNav { get; set; }
    public bool LightStrobe { get; set; }
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
        {
            _sessionConnections[sessionCode] = new List<string>();
        }
        _sessionConnections[sessionCode].Add(Context.ConnectionId);
        
        if (!_sessionControlMap.ContainsKey(sessionCode))
        {
            _sessionControlMap[sessionCode] = Context.ConnectionId;
            await Clients.Caller.SendAsync("ControlStatusChanged", true);
            _logger.LogInformation("Initial control assigned to {ControlId} in session {SessionCode}", 
                Context.ConnectionId, sessionCode);
        }
        else
        {
            bool hasControl = _sessionControlMap[sessionCode] == Context.ConnectionId;
            await Clients.Caller.SendAsync("ControlStatusChanged", hasControl);
            _logger.LogInformation("Notified joining client {ClientId} of control status ({HasControl}) in session {SessionCode}", 
                Context.ConnectionId, hasControl, sessionCode);
        }
    }

    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        if (_sessionControlMap.TryGetValue(sessionCode, out var controlId) && controlId == Context.ConnectionId)
        {
            _logger.LogInformation("Received data from controller in session {SessionCode}: Alt={Alt:F1}, GS={GS:F1}", 
                sessionCode, data.Altitude, data.GroundSpeed);
                
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
        }
    }
    
    public async Task SendLightStates(string sessionCode, LightStatesDto lights)
    {
        _logger.LogInformation("Received light states from client in session {SessionCode}: B={Beacon}, L={Landing}, T={Taxi}, N={Nav}, S={Strobe}", 
            sessionCode, lights.LightBeacon, lights.LightLanding, lights.LightTaxi, lights.LightNav, lights.LightStrobe);
        
      
        await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveLightStates", lights);
    }
    
    public async Task TransferControl(string sessionCode, bool giving)
    {
        string currentController = _sessionControlMap.GetValueOrDefault(sessionCode, "");
        
        if (giving) 
        {
            if (currentController == Context.ConnectionId)
            {
           
                var otherConnections = _sessionConnections[sessionCode]
                    .Where(id => id != Context.ConnectionId)
                    .ToList();
                    
                if (otherConnections.Any())
                {
                    var newController = otherConnections.First();
                    _sessionControlMap[sessionCode] = newController;
                    
               
                    await Clients.Caller.SendAsync("ControlStatusChanged", false);
                    await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                    
                    _logger.LogInformation("Control transferred from {OldId} to {NewId} in session {SessionCode}", 
                        Context.ConnectionId, newController, sessionCode);
                }
            }
        }
        else
        {
            if (currentController != Context.ConnectionId)
            {
                var oldController = currentController;
                _sessionControlMap[sessionCode] = Context.ConnectionId;
                
   
                await Clients.Caller.SendAsync("ControlStatusChanged", true);
                if (!string.IsNullOrEmpty(oldController))
                    await Clients.Client(oldController).SendAsync("ControlStatusChanged", false);
                
                _logger.LogInformation("Control taken by {NewId} from {OldId} in session {SessionCode}", 
                    Context.ConnectionId, oldController, sessionCode);
            }
        }
    }
    
    public override async Task OnDisconnectedAsync(Exception exception)
    {

        var sessions = _sessionConnections
            .Where(kvp => kvp.Value.Contains(Context.ConnectionId))
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
                    _logger.LogInformation("Control automatically transferred to {NewId} after disconnect in session {SessionCode}", 
                        newController, session);
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