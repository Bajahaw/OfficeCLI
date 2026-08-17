FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY src/officecli/officecli.csproj src/officecli/
RUN dotnet restore src/officecli/officecli.csproj -r linux-x64
COPY src/ src/
COPY skills/ skills/
COPY schemas/ schemas/
RUN test -f skills/officecli/SKILL.md
RUN dotnet publish src/officecli/officecli.csproj -c Release -r linux-x64 -o /out --no-restore --nologo

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=build /out/officecli /app/officecli
COPY docker-entrypoint.sh /app/docker-entrypoint.sh
RUN chmod +x /app/officecli /app/docker-entrypoint.sh

ENV OFFICECLI_SKIP_UPDATE=1

EXPOSE 8765
VOLUME /data

ENTRYPOINT ["/app/docker-entrypoint.sh"]
