FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore as distinct layers
COPY ["barakoCMS/barakoCMS.csproj", "barakoCMS/"]
RUN dotnet restore "barakoCMS/barakoCMS.csproj"

# Copy everything else and build
COPY . .
WORKDIR "/src/barakoCMS"
RUN dotnet build "barakoCMS.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "barakoCMS.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "barakoCMS.dll"]
