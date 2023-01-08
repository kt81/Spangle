FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["src/Spangle.MediaServer/Spangle.MediaServer.csproj", "src/Spangle.MediaServer/"]
RUN dotnet restore "src/Spangle.MediaServer/Spangle.MediaServer.csproj"
COPY . .
WORKDIR "/src/src/Spangle.MediaServer"
RUN dotnet build "Spangle.MediaServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Spangle.MediaServer.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Spangle.MediaServer.dll"]
