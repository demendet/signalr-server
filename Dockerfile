FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["G1000SignalingServer.csproj", "./"]
RUN dotnet restore "G1000SignalingServer.csproj"
COPY . .
RUN dotnet build "G1000SignalingServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "G1000SignalingServer.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "G1000SignalingServer.dll"] 