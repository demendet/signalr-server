using Microsoft.AspNetCore.SignalR;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using DualLInkNet.Models;

namespace DualLInkNet
{
    // This represents the server code running on Railway
    public class FlightHub : Hub
    {
        private static Dictionary<string, string> sessionToConnectionId = new Dictionary<string, string>();
        private static Dictionary<string, string> connectionIdToSession = new Dictionary<string, string>();
        private static Dictionary<string, HashSet<string>> sessionClients = new Dictionary<string, HashSet<string>>();
        private static Dictionary<string, string> controllerConnections = new Dictionary<string, string>();

        public async Task JoinSession(string sessionCode, bool isHost)
        {
            string normalizedCode = sessionCode.ToLower().Trim();
            string connectionId = Context.ConnectionId;

            // Create a new session if one doesn't exist
            if (!sessionClients.ContainsKey(normalizedCode))
            {
                sessionClients[normalizedCode] = new HashSet<string>();
            }

            // Add this connection to the session
            sessionClients[normalizedCode].Add(connectionId);
            connectionIdToSession[connectionId] = normalizedCode;

            // If this is the host, store their connection ID
            if (isHost)
            {
                sessionToConnectionId[normalizedCode] = connectionId;
                
                // Host automatically gets control first
                controllerConnections[normalizedCode] = connectionId;
                
                // Inform the host they have control
                await Clients.Caller.SendAsync("ControlStatusChanged", true);
            }
            else
            {
                // Inform client they don't have control
                await Clients.Caller.SendAsync("ControlStatusChanged", false);
            }

            // Notify everyone in the session about the join
            await Clients.Group(normalizedCode).SendAsync("ClientJoined", connectionId);

            // Add connection to the session group
            await Groups.AddToGroupAsync(connectionId, normalizedCode);

            await Clients.Caller.SendAsync("JoinedSession", normalizedCode);
        }

        public async Task SendAircraftData(object data)
        {
            if (!connectionIdToSession.TryGetValue(Context.ConnectionId, out string sessionCode))
                return;

            // Check if this client has control privileges
            if (!controllerConnections.TryGetValue(sessionCode, out string controllerConnectionId) || 
                controllerConnectionId != Context.ConnectionId)
            {
                // This client doesn't have control - ignore their data
                return;
            }

            // Forward the data exactly as received, including all properties (lights, etc.)
            // to everyone in the session except the sender
            await Clients.OthersInGroup(sessionCode).SendAsync("ReceiveAircraftData", data);
        }

        public async Task TransferControl(bool givingControl)
        {
            if (!connectionIdToSession.TryGetValue(Context.ConnectionId, out string sessionCode))
                return;
                
            // Verify this session exists
            if (!sessionClients.TryGetValue(sessionCode, out HashSet<string> clients) || clients.Count < 2)
            {
                // Session doesn't exist or only one client
                return;
            }

            // Get the current controller
            if (controllerConnections.TryGetValue(sessionCode, out string controllerConnectionId))
            {
                if (givingControl)
                {
                    // If current controller is giving up control
                    if (controllerConnectionId == Context.ConnectionId)
                    {
                        // Find the first available client other than the current controller
                        string newController = clients.FirstOrDefault(c => c != Context.ConnectionId);
                        if (!string.IsNullOrEmpty(newController))
                        {
                            // Transfer control to the other client
                            controllerConnections[sessionCode] = newController;
                            
                            // Notify participants of the change
                            await Clients.Client(controllerConnectionId).SendAsync("ControlStatusChanged", false);
                            await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                        }
                    }
                }
                else // Taking control
                {
                    // If someone is requesting control and they don't have it
                    if (controllerConnectionId != Context.ConnectionId)
                    {
                        // Remove control from current controller
                        await Clients.Client(controllerConnectionId).SendAsync("ControlStatusChanged", false);
                        
                        // Give control to the requester
                        controllerConnections[sessionCode] = Context.ConnectionId;
                        await Clients.Caller.SendAsync("ControlStatusChanged", true);
                    }
                }
            }
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            string connectionId = Context.ConnectionId;
            
            if (connectionIdToSession.TryGetValue(connectionId, out string sessionCode))
            {
                // Remove from session clients
                if (sessionClients.TryGetValue(sessionCode, out var clients))
                {
                    clients.Remove(connectionId);
                    
                    // If this was the controller, transfer control
                    if (controllerConnections.TryGetValue(sessionCode, out string controllerConnection) &&
                        controllerConnection == connectionId)
                    {
                        string newController = clients.FirstOrDefault();
                        if (!string.IsNullOrEmpty(newController))
                        {
                            controllerConnections[sessionCode] = newController;
                            await Clients.Client(newController).SendAsync("ControlStatusChanged", true);
                        }
                    }

                    // If session is empty, clean up
                    if (clients.Count == 0)
                    {
                        sessionClients.Remove(sessionCode);
                        sessionToConnectionId.Remove(sessionCode);
                        controllerConnections.Remove(sessionCode);
                    }
                }
                
                connectionIdToSession.Remove(connectionId);
                
                // Notify others in the session about the disconnect
                await Clients.Group(sessionCode).SendAsync("ClientDisconnected", connectionId);
            }
            
            await base.OnDisconnectedAsync(exception);
        }
    }
} 