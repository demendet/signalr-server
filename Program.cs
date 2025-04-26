using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel to use the PORT environment variable for Railway
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://+:{port}");

// Add CORS support
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
        
        // Note: When using AllowAnyOrigin, you cannot use AllowCredentials
        // If you need to allow credentials, use specific origins instead:
        // policy.WithOrigins("https://your-client-domain.com", "http://localhost:3000")
        //      .AllowAnyMethod()
        //      .AllowAnyHeader()
        //      .AllowCredentials();
    });
});

// Add SignalR
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 102400; // 100KB for large messages
    options.EnableDetailedErrors = true;
});

// Add logging
builder.Services.AddLogging();

var app = builder.Build();

// Configure middleware
app.UseCors();
app.UseHttpsRedirection();

// Map routes
app.MapGet("/", () => "G1000 Signaling Server Running");
app.MapHub<G1000SignalingHub>("/sharedcockpithub");

app.Run();

// SignalR Hub for WebRTC signaling
public class G1000SignalingHub : Hub
{
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _rooms = new();
    private static readonly ConcurrentDictionary<string, string> _userRooms = new();
    private readonly ILogger<G1000SignalingHub> _logger;

    public G1000SignalingHub(ILogger<G1000SignalingHub> logger)
    {
        _logger = logger;
    }

    // Join or create a room
    public async Task JoinRoom(string roomId)
    {
        // Get or create the room
        var room = _rooms.GetOrAdd(roomId, _ => new ConcurrentDictionary<string, string>());

        // Add the user to the room
        string connectionId = Context.ConnectionId;
        room[connectionId] = connectionId;
        _userRooms[connectionId] = roomId;

        // Join the SignalR group for this room
        await Groups.AddToGroupAsync(connectionId, roomId);

        _logger.LogInformation("User {ConnectionId} joined room {RoomId}", connectionId, roomId);

        // Notify other participants
        await Clients.OthersInGroup(roomId).SendAsync("UserJoined", connectionId);

        // Return information about other users in the room
        await Clients.Caller.SendAsync("RoomJoined", roomId, room.Keys.Where(id => id != connectionId).ToList());
    }

    // Send a WebRTC offer to a specific peer
    public async Task SendOffer(string targetConnectionId, string offer)
    {
        string senderConnectionId = Context.ConnectionId;
        _logger.LogInformation("User {ConnectionId} sent offer to {TargetConnectionId}", senderConnectionId, targetConnectionId);
        
        await Clients.Client(targetConnectionId).SendAsync("ReceiveOffer", senderConnectionId, offer);
    }

    // Send a WebRTC answer to a specific peer
    public async Task SendAnswer(string targetConnectionId, string answer)
    {
        string senderConnectionId = Context.ConnectionId;
        _logger.LogInformation("User {ConnectionId} sent answer to {TargetConnectionId}", senderConnectionId, targetConnectionId);
        
        await Clients.Client(targetConnectionId).SendAsync("ReceiveAnswer", senderConnectionId, answer);
    }

    // Send ICE candidate information to a specific peer
    public async Task SendIceCandidate(string targetConnectionId, string iceCandidate)
    {
        string senderConnectionId = Context.ConnectionId;
        _logger.LogInformation("User {ConnectionId} sent ICE candidate to {TargetConnectionId}", senderConnectionId, targetConnectionId);
        
        await Clients.Client(targetConnectionId).SendAsync("ReceiveIceCandidate", senderConnectionId, iceCandidate);
    }

    // Send G1000 control messages - softkey press
    public async Task SendG1000SoftkeyPress(string roomId, int keyNumber, bool isPfd)
    {
        _logger.LogInformation("User {ConnectionId} pressed {Type} softkey {Key} in room {RoomId}", 
            Context.ConnectionId, isPfd ? "PFD" : "MFD", keyNumber, roomId);
        
        await Clients.OthersInGroup(roomId).SendAsync("ReceiveG1000SoftkeyPress", keyNumber, isPfd);
    }

    // Send generic G1000 control messages
    public async Task SendG1000Control(string roomId, string controlName)
    {
        _logger.LogInformation("User {ConnectionId} activated control {ControlName} in room {RoomId}", 
            Context.ConnectionId, controlName, roomId);
        
        await Clients.OthersInGroup(roomId).SendAsync("ReceiveG1000Control", controlName);
    }

    // Handle G1000 knob rotation
    public async Task SendG1000KnobRotation(string roomId, string knobName, int steps)
    {
        _logger.LogInformation("User {ConnectionId} rotated knob {KnobName} by {Steps} steps in room {RoomId}", 
            Context.ConnectionId, knobName, steps, roomId);
        
        await Clients.OthersInGroup(roomId).SendAsync("ReceiveG1000KnobRotation", knobName, steps);
    }

    // Handle disconnect
    public override async Task OnDisconnectedAsync(Exception exception)
    {
        string connectionId = Context.ConnectionId;
        
        // Check if user was in a room
        if (_userRooms.TryRemove(connectionId, out string roomId))
        {
            // Remove user from room
            if (_rooms.TryGetValue(roomId, out var room))
            {
                room.TryRemove(connectionId, out _);
                
                // If room is empty, remove it
                if (room.IsEmpty)
                {
                    _rooms.TryRemove(roomId, out _);
                    _logger.LogInformation("Room {RoomId} removed as it's now empty", roomId);
                }
                else
                {
                    // Notify other users that this user has left
                    await Clients.OthersInGroup(roomId).SendAsync("UserLeft", connectionId);
                    _logger.LogInformation("User {ConnectionId} left room {RoomId}", connectionId, roomId);
                }
            }
            
            // Remove user from SignalR group
            await Groups.RemoveFromGroupAsync(connectionId, roomId);
        }
        
        await base.OnDisconnectedAsync(exception);
    }
}
