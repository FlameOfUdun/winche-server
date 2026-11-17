# Winche Server: the whole Winche stack and its console in one container.
# The .NET build runs once, on the build machine's own platform, for the platform being built (amd64 or arm64):
# only the small final stage runs per platform.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY nuget.config Directory.Build.props ./
COPY packages/ packages/
COPY src/Winche.Server/Winche.Server.csproj src/Winche.Server/
COPY src/Winche.Console/Winche.Console.csproj src/Winche.Console/
RUN dotnet restore src/Winche.Server/Winche.Server.csproj -a $TARGETARCH
COPY src/ src/
RUN dotnet publish src/Winche.Server/Winche.Server.csproj -c Release -a $TARGETARCH --self-contained false -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# The notices the licenses of what the image includes ask for (Deno, …).
COPY THIRD-PARTY-NOTICES.md /app/
# Npgsql looks for Kerberos (GSSAPI) when it connects; the slim image doesn't have it. Functions run on Deno.
ARG DENO_VERSION=2.9.8
RUN apt-get update \
 && apt-get install -y --no-install-recommends libgssapi-krb5-2 ca-certificates curl unzip \
 && curl -fsSL -o /tmp/deno.zip "https://github.com/denoland/deno/releases/download/v${DENO_VERSION}/deno-$(uname -m)-unknown-linux-gnu.zip" \
 && unzip -q /tmp/deno.zip -d /usr/local/bin && rm /tmp/deno.zip && deno --version \
 && apt-get purge -y unzip && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*   # curl stays: health checks
# Projects' files live under /data/projects/<id> unless moved to a bucket in the console: mount a volume on /data.
RUN mkdir -p /data/projects /data/deno && chown -R $APP_UID /data
VOLUME /data
# The base image listens on 8080 (ASPNETCORE_HTTP_PORTS).
ENV Winche__ProjectsPath=/data/projects \
    DENO_DIR=/data/deno
EXPOSE 8080
# The process runs (the container's check); load balancers use /healthz (ready once warm, not while stopping).
HEALTHCHECK --interval=15s --timeout=3s --start-period=60s CMD curl -fs http://localhost:8080/livez || exit 1
USER $APP_UID
ENTRYPOINT ["dotnet", "Winche.Server.dll"]
