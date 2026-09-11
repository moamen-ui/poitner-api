FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dev
WORKDIR /src
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
CMD ["dotnet", "watch", "--project", "API", "run", "--no-launch-profile"]

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG APP_VERSION=0.0.0-dev
WORKDIR /src
COPY . .
RUN dotnet publish API/Pointer.API.csproj -c Release -o /app -p:InformationalVersion=$APP_VERSION

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Pointer.API.dll"]
