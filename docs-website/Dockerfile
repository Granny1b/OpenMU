# Build the static site...
FROM node:20-alpine AS build
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
# Where the site will be reachable. Override at build time, e.g.
#   --build-arg DOCS_URL=http://203.0.113.10:8080
ARG DOCS_URL=http://localhost:8080
ARG DOCS_BASE_URL=/
RUN DOCS_URL=$DOCS_URL DOCS_BASE_URL=$DOCS_BASE_URL npm run build

# ...and serve it as plain static files.
FROM nginx:alpine
COPY --from=build /src/build /usr/share/nginx/html
