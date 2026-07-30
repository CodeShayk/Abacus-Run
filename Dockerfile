FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/Abacus.Run.Api/Abacus.Run.Api.csproj
RUN dotnet publish src/Abacus.Run.Api/Abacus.Run.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Abacus.Run.Api.dll"]