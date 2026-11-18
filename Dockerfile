FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Href.Core.Api/Href.Core.Api.csproj src/Href.Core.Api/
RUN dotnet restore src/Href.Core.Api/Href.Core.Api.csproj

COPY src/ src/
RUN dotnet publish src/Href.Core.Api/Href.Core.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["sh", "-c", "export ASPNETCORE_HTTP_PORTS=${PORT:-8080} && exec dotnet Href.Core.Api.dll"]
