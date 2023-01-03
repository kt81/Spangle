FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["SpangleWebHost/SpangleWebHost.csproj", "SpangleWebHost/"]
RUN dotnet restore "SpangleWebHost/SpangleWebHost.csproj"
COPY . .
WORKDIR "/src/SpangleWebHost"
RUN dotnet build "SpangleWebHost.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "SpangleWebHost.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "SpangleWebHost.dll"]
