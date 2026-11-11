FROM node:22-alpine AS build
WORKDIR /web
COPY src/AegisOps.Web/package*.json ./
RUN npm ci
COPY src/AegisOps.Web/ ./
RUN npm run build

# Stable nginx line; Dependabot (docker ecosystem) proposes patch updates. 1.27 no longer receives fixes.
FROM nginx:1.31-alpine
COPY src/AegisOps.Web/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /web/dist /usr/share/nginx/html
EXPOSE 80
