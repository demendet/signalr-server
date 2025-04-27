# G1000 Signaling Server

This is a SignalR-based signaling server for WebRTC connections in the G1000 Shared Cockpit application.

## Important

**The signaling server MUST be running for the WebRTC connections to work properly!**

## How to Run

1. Open a command prompt and navigate to the G1000SignalingServer folder
2. Run the server with:
   ```
   dotnet run
   ```
3. Alternatively, use the provided batch file in the parent directory:
   ```
   run_signaling_server.bat
   ```

## Connection Details

- The server runs on port 3000 by default
- The SignalR hub endpoint is: `http://localhost:3000/sharedcockpithub`
- Make sure this URL matches what's configured in the main application

## Troubleshooting

- Check that the server is running before trying to connect the main application
- Look for log messages in the console
- If you get connection errors, ensure no other application is using port 3000
- If needed, you can modify the port in Program.cs and update the connection URL in the main application

## Overview

The signaling server is built using ASP.NET Core and SignalR. It provides a simple hub that allows clients to:
- Join/create rooms using a room ID
- Exchange WebRTC offers and answers
- Share ICE candidates
- Send G1000 control messages as a fallback if WebRTC data channels are not available

## Development

### Prerequisites
- .NET 8.0 SDK or higher

### Running Locally
1. Navigate to the project directory: `cd G1000SignalingServer`
2. Run the project: `dotnet run`
3. The server will start at https://localhost:7290

## Deployment to Railway

This signaling server is designed to be easily deployed to Railway. Follow these steps to deploy it:

1. Create a new Railway project
2. Connect your GitHub repository to Railway
3. Add a new service from the repository and choose the G1000SignalingServer directory
4. Railway will automatically detect the .NET project and build it
5. The server will be deployed and accessible via the Railway-provided URL

### Environment Variables

You can configure the following environment variables for the deployed server:

- `ASPNETCORE_ENVIRONMENT`: Set to "Production" for production deployments
- `ASPNETCORE_URLS`: To specify which URLs and ports the server listens on

## Security

This signaling server includes:
- CORS protection configured for specific allowed origins
- Transport-level encryption via HTTPS
- No persistent storage of sensitive information

## Architecture

The G1000 signaling server is part of a cloud-assisted peer-to-peer architecture:

1. Clients connect to the signaling server to join a session
2. The signaling server facilitates WebRTC connection negotiation
3. After WebRTC connections are established, clients communicate directly with each other
4. The signaling server remains available as a fallback for control messages

The server has minimal impact on session performance as most data transfers directly between peers after initial connection setup.

## License

This project is licensed under the MIT License - see the LICENSE file for details. 