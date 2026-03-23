FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["src/RiftSentry.SyncServer/RiftSentry.SyncServer.csproj", "src/RiftSentry.SyncServer/"]
COPY ["src/RiftSentry.SyncContracts/RiftSentry.SyncContracts.csproj", "src/RiftSentry.SyncContracts/"]
RUN dotnet restore "src/RiftSentry.SyncServer/RiftSentry.SyncServer.csproj"

COPY . .
RUN dotnet publish "src/RiftSentry.SyncServer/RiftSentry.SyncServer.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "RiftSentry.SyncServer.dll"]
