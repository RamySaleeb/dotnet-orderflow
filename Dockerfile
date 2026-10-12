FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/OrderFlow/OrderFlow.csproj src/OrderFlow/
RUN dotnet restore src/OrderFlow
COPY src/OrderFlow/ src/OrderFlow/
RUN dotnet publish src/OrderFlow -c Release -o /app --no-restore
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "OrderFlow.dll"]
