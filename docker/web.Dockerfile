# Imagem da aplicação web (API). Build a partir da raiz do repositório:
#   docker build -f docker/web.Dockerfile -t expensehub-web .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY sources/ sources/
RUN dotnet publish sources/ExpenseHub.Api/ExpenseHub.Api.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG GIT_COMMIT=unknown
LABEL org.opencontainers.image.revision=$GIT_COMMIT \
      org.opencontainers.image.title="expensehub-web"
ENV APP_VERSION=$GIT_COMMIT \
    ASPNETCORE_HTTP_PORTS=8080
WORKDIR /app
COPY --from=build /app .
USER root
RUN mkdir -p /data/keys && chown -R app:app /data
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "ExpenseHub.Api.dll"]
