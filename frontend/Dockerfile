# Build the project
FROM node:24-alpine3.23 AS build-env
WORKDIR /nzb-webdav
COPY ./ ./
RUN apk upgrade --no-cache
RUN npm ci
RUN npm run build
RUN npm run build:server
RUN npm prune --omit=dev

# Build the production image
FROM node:24-alpine3.23
RUN apk upgrade --no-cache
EXPOSE 3000
WORKDIR /nzb-webdav
ENV NODE_ENV=production
COPY --from=build-env /nzb-webdav/node_modules ./node_modules
COPY --from=build-env /nzb-webdav/package.json ./package.json
COPY --from=build-env /nzb-webdav/dist-node/server.js ./dist-node/server.js
COPY --from=build-env /nzb-webdav/build ./build
CMD ["node", "dist-node/server.js"]
