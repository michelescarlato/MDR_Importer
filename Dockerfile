# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy source from the repo checkout (no git clone needed)
COPY . .

# If your repo has multiple projects, set the csproj explicitly:
# docker build --build-arg PROJECT_PATH=src/MDR_Importer/MDR_Importer.csproj .
ARG PROJECT_PATH=MDR_Importer.csproj

RUN dotnet restore "${PROJECT_PATH}"
RUN dotnet publish "${PROJECT_PATH}" -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ARG PUID=1000
ARG PGID=1000

# Create user/group matching host IDs
RUN groupadd -g "${PGID}" mdr \
 && useradd  -u "${PUID}" -g "${PGID}" -m -s /usr/sbin/nologin mdr

# App binaries
COPY --from=build /app/out ./

# Create dirs used by your docker-compose mounts
RUN mkdir -p /app/MDR_Data /app/MDR_Sources /app/MDR_Logs /app/test \
 && chown -R "${PUID}:${PGID}" /app

# OPTIONAL: only keep this if you really want to bake it into the image (often contains secrets)
# COPY appsettings.json /app/appsettings.json

USER mdr
ENTRYPOINT ["dotnet", "MDR_Importer.dll"]
