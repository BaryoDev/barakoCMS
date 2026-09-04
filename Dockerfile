FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the manifests and restore as a distinct layer, so the cache survives a source-only change.
#
# The two props files are not optional here. Central package management keeps every package version in
# Directory.Packages.props, and the shared MSBuild settings — TargetFramework among them — live in
# Directory.Build.props. Restoring with only the .csproj present gives NETSDK1013 ("The TargetFramework
# value '' was not recognized"), which is what broke the Decaf image on the 3.18.0 release.
#
# The lock file rides in the same layer as the .csproj: locked mode needs it beside the project it
# restores, and a restore that finds no lock file fails rather than generating one (NU1004).
COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["barakoCMS/barakoCMS.csproj", "barakoCMS/packages.lock.json", "barakoCMS/"]
RUN dotnet restore "barakoCMS/barakoCMS.csproj" --locked-mode

# Copy everything else and build
COPY . .
WORKDIR "/src/barakoCMS"
RUN dotnet build "barakoCMS.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "barakoCMS.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# The commit this image was built from, served back on /health/build. Same reasoning as
# Dockerfile.suite: .git is in .dockerignore, so it has to be passed in.
ARG BARAKO_BUILD_SHA=""
ENV BARAKO_BUILD_SHA=$BARAKO_BUILD_SHA

# Non-root. The base image ships app as uid 1654 for this, and nothing here needs privilege: 8080 is
# above 1024 so an unprivileged user can bind it, and the app writes nothing to the container
# filesystem at runtime. Uploads go to the Files module's configured store.
#
# No compose file in this repository mounts a host path into the API, so nothing shipped breaks. The
# caveat is for anyone who adds one: a directory owned by root is not writable by uid 1654, and that
# fails at the mount rather than here.
USER app

ENTRYPOINT ["dotnet", "barakoCMS.dll"]
