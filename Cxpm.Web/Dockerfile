FROM node:24-alpine AS frontend-build
WORKDIR /src/Cxpm.Web/clientapp
RUN corepack enable
COPY Cxpm.Web/clientapp/package.json Cxpm.Web/clientapp/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile
COPY Cxpm.Web/clientapp/ ./
RUN pnpm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src
COPY NuGet.config ./
COPY Cxpm.slnx ./
COPY Cxpm.Core/Cxpm.Core.csproj Cxpm.Core/
COPY Cxpm.Web/Cxpm.Web.csproj Cxpm.Web/
RUN dotnet restore Cxpm.Web/Cxpm.Web.csproj --configfile NuGet.config
COPY Cxpm.Core/ Cxpm.Core/
COPY Cxpm.Web/ Cxpm.Web/
COPY --from=frontend-build /src/Cxpm.Web/wwwroot/ Cxpm.Web/wwwroot/
RUN dotnet publish Cxpm.Web/Cxpm.Web.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    RepositoryStorage__Path=/var/lib/cxpm/packages
EXPOSE 8080
RUN mkdir -p /var/lib/cxpm/packages && chown -R app:app /var/lib/cxpm
COPY --from=backend-build --chown=app:app /app/publish/ ./
USER app
VOLUME ["/var/lib/cxpm"]
ENTRYPOINT ["dotnet", "Cxpm.Web.dll"]
