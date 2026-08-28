# Build stage runs on the build host's own arch and cross-compiles to the
# target arch, so multi-arch CI builds don't pay for QEMU emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY TcpProxy.csproj .
RUN dotnet restore -a $TARGETARCH
COPY . .
RUN dotnet publish -c Release -a $TARGETARCH -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENV CONFIG_PATH=/data/config.json
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "TcpProxy.dll"]
