# Build na plataforma do builder: o resultado (HTML/JS) é independente de arquitetura.
FROM --platform=$BUILDPLATFORM node:20-alpine AS build

ARG AWFACE_VERSION=1.1.2
ARG AWFACE_BUILD_COMMIT=unknown
ARG AWFACE_BUILD_DATE=unknown

WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .
RUN test "$(cat VERSION)" = "$AWFACE_VERSION"
RUN npm run build

FROM nginx:1.27-alpine

ARG AWFACE_VERSION=1.1.2
ARG AWFACE_BUILD_COMMIT=unknown
ARG AWFACE_BUILD_DATE=unknown

LABEL org.opencontainers.image.title="AWFace Frontend" \
      org.opencontainers.image.version="${AWFACE_VERSION}" \
      org.opencontainers.image.revision="${AWFACE_BUILD_COMMIT}" \
      org.opencontainers.image.created="${AWFACE_BUILD_DATE}" \
      org.opencontainers.image.source="https://github.com/novaintegral/awface-certiface"

COPY docker/nginx/default.conf /etc/nginx/conf.d/default.conf
COPY docker/nginx/awface-security-headers.conf /etc/nginx/snippets/awface-security-headers.conf
COPY --from=build /app/dist/facetec-demo-app /usr/share/nginx/html
# Remove service workers antigos instalados por aplicações que já usaram este domínio.
COPY docker/nginx/ngsw-worker.js /usr/share/nginx/html/ngsw-worker.js
COPY docker/nginx/ngsw-worker.js /usr/share/nginx/html/safety-worker.js
COPY docker/nginx/ngsw-worker.js /usr/share/nginx/html/worker-basic.min.js

EXPOSE 5580

