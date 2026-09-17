# Build the project
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine3.23 AS build-env
WORKDIR /nzb-webdav
COPY ./ ./
RUN dotnet restore
RUN dotnet publish -c Release -r linux-musl-x64 -o ./publish

# Build the production image
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.23
EXPOSE 8080
WORKDIR /nzb-webdav
RUN mkdir /config
RUN apk upgrade --no-cache \
    && apk add --no-cache gcompat shadow su-exec
COPY --from=build-env /nzb-webdav/publish ./
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh
CMD ["/entrypoint.sh"]
