FROM mcr.microsoft.com/dotnet/runtime:7.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["examples/Spangle.Examples.Console/Spangle.Examples.Console.csproj", "examples/Spangle.Examples.Console/"]
RUN dotnet restore "examples/Spangle.Examples.Console/Spangle.Examples.Console.csproj"
COPY . .
WORKDIR "/src/examples/Spangle.Examples.Console"
RUN dotnet build "Spangle.Examples.Console.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Spangle.Examples.Console.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Spangle.Examples.Console.dll"]
