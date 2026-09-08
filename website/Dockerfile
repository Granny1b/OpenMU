# Build and run the public website. Same base images as the game server (src/Startup/Dockerfile)
# so the VPS pulls no additional layers.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
WORKDIR /app

# icu-libs and the globalization switch must be set together; without both, culture-aware
# formatting throws at runtime on alpine.
RUN apk add --no-cache icu-libs
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /source
COPY Directory.Build.props Directory.Packages.props ./
COPY src/MuSite/ src/MuSite/
RUN dotnet publish src/MuSite/MuSite.csproj -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .

# The data protection key ring signs the auth cookie: anything able to read it can forge a session
# for any account, the Owner included. Deliberately 700 and owned by the app user - NOT the
# chmod 777 the game server uses for its own key directory.
ARG APP_UID=1000
RUN mkdir -p /app/keys && chown ${APP_UID} /app/keys && chmod 700 /app/keys

USER ${APP_UID}
ENTRYPOINT ["dotnet", "MuSite.dll"]
