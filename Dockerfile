# syntax=docker/dockerfile:1

# -------- Stage 1: Build frontend --------
FROM --platform=$BUILDPLATFORM node:24-alpine3.23 AS frontend-build

WORKDIR /frontend
COPY ./frontend ./

RUN apk upgrade --no-cache
RUN npm ci
RUN npm run build
RUN npm run build:server
RUN npm prune --omit=dev

# -------- Stage 2: Build backend --------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine3.23 AS backend-build

WORKDIR /backend
COPY ./backend ./

# Accept build-time architecture as ARG (e.g., x64 or arm64)
ARG TARGETARCH
RUN dotnet restore
RUN dotnet publish -c Release -r linux-musl-${TARGETARCH} -o ./publish

# Use the target architecture for the runtime binary in multi-platform builds.
FROM node:24-alpine3.23 AS node-runtime

# -------- Stage 3: Combined runtime image --------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine3.23

# Label the image
ARG REPO_URL
LABEL org.opencontainers.image.source=${REPO_URL}

# Prepare environment
WORKDIR /app
RUN mkdir /config \
    && apk upgrade --no-cache \
    && apk add --no-cache libstdc++ gcompat shadow su-exec bash curl tzdata
COPY --from=node-runtime /usr/local/bin/node /usr/local/bin/node
RUN node --version

# Copy frontend
COPY --from=frontend-build /frontend/node_modules ./frontend/node_modules
COPY --from=frontend-build /frontend/package.json ./frontend/package.json
COPY --from=frontend-build /frontend/dist-node/server.js ./frontend/dist-node/server.js
COPY --from=frontend-build /frontend/build ./frontend/build

# Copy backend
COPY --from=backend-build /backend/publish ./backend

# Entry and runtime setup
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh

# Set env variables
EXPOSE 3000
ARG NZBDAV_VERSION
ENV NZBDAV_VERSION=${NZBDAV_VERSION}
ENV NODE_ENV=production
ENV LOG_LEVEL=warning

CMD ["/entrypoint.sh"]
