FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 3000

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src

# Show directory content before copy
RUN echo "Contents of source directory before copy:" && ls -la

COPY ["G1000SignalingServer.csproj", "./"]
RUN dotnet restore "G1000SignalingServer.csproj" --verbosity detailed

# Show directory content after restore
RUN echo "Contents after restore:" && ls -la

COPY . .

# Show directory content after full copy
RUN echo "Contents after full copy:" && ls -la

# Use detailed verbosity for better error messages
RUN dotnet build "G1000SignalingServer.csproj" -c Release -o /app/build --verbosity detailed

FROM build AS publish
RUN dotnet publish "G1000SignalingServer.csproj" -c Release -o /app/publish --verbosity detailed

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "G1000SignalingServer.dll"] 