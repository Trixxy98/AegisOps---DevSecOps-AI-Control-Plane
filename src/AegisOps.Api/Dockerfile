FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/AegisOps.Api/AegisOps.Api.csproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0
WORKDIR /app
COPY --from=build /publish .
# Scan reports live in /app/reports (a named volume): the unprivileged user must own it so new volumes
# are created writable. The base image provides user `app` (APP_UID 1654).
RUN mkdir -p /app/reports && chown "$APP_UID" /app/reports
ENV ASPNETCORE_HTTP_PORTS="8080;9464"
EXPOSE 8080 9464
USER $APP_UID
ENTRYPOINT ["dotnet", "AegisOps.Api.dll"]
