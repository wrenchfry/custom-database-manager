FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY ["SqliteUnderTheHood.csproj", "./"]
RUN dotnet restore "SqliteUnderTheHood.csproj"

COPY . .
RUN dotnet publish "SqliteUnderTheHood.csproj" --configuration Release --no-restore --output /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SqliteUnderTheHood.dll"]
