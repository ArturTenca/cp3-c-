# Imagem do worker C#. Build a partir da raiz do repositório:
#   docker build -f docker/worker.Dockerfile -t expensehub-worker .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig ./
COPY sources/ sources/
RUN dotnet publish sources/ExpenseHub.Worker/ExpenseHub.Worker.csproj -c Release -o /app --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG GIT_COMMIT=unknown
LABEL org.opencontainers.image.revision=$GIT_COMMIT \
      org.opencontainers.image.title="expensehub-worker"
ENV APP_VERSION=$GIT_COMMIT
WORKDIR /app
COPY --from=build /app .
USER root
RUN mkdir -p /data && chown -R app:app /data
USER app
ENTRYPOINT ["dotnet", "ExpenseHub.Worker.dll"]
