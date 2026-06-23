FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dev
WORKDIR /src
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
CMD ["dotnet", "watch", "--project", "API", "run"]

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish API -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Pointer.API.dll"]
