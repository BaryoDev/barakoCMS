FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the manifests and restore as a distinct layer, so the cache survives a source-only change.
#
# The two props files are not optional here. Central package management keeps every package version in
# Directory.Packages.props, and the shared MSBuild settings — TargetFramework among them — live in
# Directory.Build.props. Restoring with only the .csproj present gives NETSDK1013 ("The TargetFramework
# value '' was not recognized"), which is what broke the Decaf image on the 3.18.0 release.
COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
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
