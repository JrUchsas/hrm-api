FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
ENV ASPNETCORE_URLS=http://*:${PORT:-8080}
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["hrm-api.csproj", "."]
RUN dotnet restore "./hrm-api.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./hrm-api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "./hrm-api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "hrm-api.dll"]