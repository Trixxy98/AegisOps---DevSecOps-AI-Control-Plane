# The build stage runs on the build machine's platform (static output); only the nginx stage is per-architecture.
FROM --platform=$BUILDPLATFORM node:22-alpine AS build
WORKDIR /web
COPY src/AegisOps.Web/package*.json ./
RUN npm ci
COPY src/AegisOps.Web/ ./
RUN npm run build

# Stable nginx line; Dependabot (docker ecosystem) proposes patch updates. 1.27 no longer receives fixes.
FROM nginx:1.30-alpine
COPY src/AegisOps.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /web/dist /usr/share/nginx/html
EXPOSE 80
