FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet publish src/AegisOps.Worker/AegisOps.Worker.csproj -c Release -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
ENV ASPNETCORE_HTTP_PORTS=8081
EXPOSE 8081
ENTRYPOINT ["dotnet", "AegisOps.Worker.dll"]
