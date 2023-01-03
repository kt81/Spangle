FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["Spangle/Spangle.csproj", "Spangle/"]
RUN dotnet restore "Spangle/Spangle.csproj"
COPY . .
WORKDIR "/src/Spangle"
RUN dotnet build "Spangle.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Spangle.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Spangle.dll"]
