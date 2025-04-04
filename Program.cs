using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();

builder.Services.AddSignalR(options => {
    options.MaximumReceiveMessageSize = 1024000;
    options.StreamBufferCapacity = 50; 
})
.AddJsonProtocol(options => {
    options.PayloadSerializerOptions.NumberHandling = 
        JsonNumberHandling.AllowNamedFloatingPointLiterals;
    options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
    options.PayloadSerializerOptions.Converters.Add(new CustomFloatConverter());
});

var app = builder.Build();

app.MapHub<CockpitHub>("/sharedcockpithub");

app.Run();

public class CustomFloatConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetDouble();
        }
        return 0.0;
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            writer.WriteNumberValue(0.0);
        }
        else
        {
            writer.WriteNumberValue(value);
        }
    }
}

public class AircraftData
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Latitude { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Longitude { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Altitude { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Pitch { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Bank { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Heading { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Throttle { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Aileron { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Elevator { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Rudder { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double BrakeLeft { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double BrakeRight { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double ParkingBrake { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Mixture { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int Flaps { get; set; } = 0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int Gear { get; set; } = 0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double GroundSpeed { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double VerticalSpeed { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double AirspeedTrue { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double AirspeedIndicated { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double OnGround { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double VelocityBodyX { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double VelocityBodyY { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double VelocityBodyZ { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double AileronTrim { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double ElevatorTrim { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double RudderTrim { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double BeaconLight { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double LandingLight { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double TaxiLight { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double NavLight { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double StrobeLight { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double PanelLight { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double CabinLight { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double BatteryMaster { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double AlternatorMaster { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double AvionicsMaster { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double BatteryVoltage { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double BusVoltage { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double EngineRPM { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double EngineCombustion { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double OilTemperature { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double OilPressure { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double EGT { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double FuelFlow { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double FuelQuantityLeft { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double FuelQuantityRight { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Com1Active { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Com1Standby { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Nav1Active { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Nav1Standby { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Com2Active { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Com2Standby { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Nav2Active { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double Nav2Standby { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double TransponderCode { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APMaster { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APHeadingLock { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APHeadingValue { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APAltitudeLock { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APAltitudeValue { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APAirspeedLock { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APAirspeedValue { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APVerticalSpeedLock { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APVerticalSpeedValue { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APNavLock { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double APApproachLock { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double G1000ActivePage { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double KohlsmanSetting { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double HeadingBug { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double AltitudeBug { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double NAV1OBS { get; set; } = 0.0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double NAV2OBS { get; set; } = 0.0;
}

public class G1000EventData
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int EventID { get; set; } = 0;
    
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int EventParam { get; set; } = 0;
}

public class CockpitHub : Hub
{
    private readonly ILogger<CockpitHub> _logger;

    public CockpitHub(ILogger<CockpitHub> logger)
    {
        _logger = logger;
    }

    public async Task JoinSession(string sessionCode)
    {
        _logger.LogInformation("Connection {ConnectionId} joined session {SessionCode}", 
            Context.ConnectionId, sessionCode);
        await Groups.AddToGroupAsync(Context.ConnectionId, sessionCode);
    }

    public async Task SendAircraftData(string sessionCode, AircraftData data)
    {
        try 
        {
            _logger.LogInformation("Data from host in session {SessionCode}: Alt={Alt:F1}", 
                sessionCode, data.Altitude);
                
            await Clients.Group(sessionCode).SendAsync("ReceiveAircraftData", data);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in SendAircraftData: {ex.Message}");
        }
    }
    
    public async Task SendG1000Event(string sessionCode, G1000EventData eventData)

    {
        try 
        {
            _logger.LogInformation("G1000 Event from host in session {SessionCode}: ID={ID}, Param={Param}", 
                sessionCode, eventData.EventID, eventData.EventParam);
                
            await Clients.Group(sessionCode).SendAsync("ReceiveG1000Event", eventData);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in SendG1000Event: {ex.Message}");
        }
    }
}