# syntax=docker/dockerfile:1
# Build context: ./Web/edu-eco-app. See docker-compose.yml at repo root.
FROM node:22-alpine AS build
WORKDIR /src

COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

FROM nginx:1.27-alpine AS final
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist/edu-eco-app/browser /usr/share/nginx/html
EXPOSE 80
