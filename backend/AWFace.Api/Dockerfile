FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

ARG AWFACE_VERSION=1.0.0
ARG AWFACE_BUILD_COMMIT=unknown

WORKDIR /src

COPY VERSION ./
COPY backend/AWFace.Api/AWFace.Api.csproj backend/AWFace.Api/
RUN dotnet restore backend/AWFace.Api/AWFace.Api.csproj

COPY backend/AWFace.Api backend/AWFace.Api
RUN test "$(cat VERSION)" = "$AWFACE_VERSION" && \
    dotnet publish backend/AWFace.Api/AWFace.Api.csproj \
      -c Release \
      -o /app/publish \
      /p:UseAppHost=false \
      /p:Version="$AWFACE_VERSION" \
      /p:InformationalVersion="$AWFACE_VERSION+$AWFACE_BUILD_COMMIT"

FROM mcr.microsoft.com/dotnet/aspnet:9.0

ARG AWFACE_VERSION=1.0.0
ARG AWFACE_BUILD_COMMIT=unknown
ARG AWFACE_BUILD_DATE=unknown

LABEL org.opencontainers.image.title="AWFace API" \
      org.opencontainers.image.version="${AWFACE_VERSION}" \
      org.opencontainers.image.revision="${AWFACE_BUILD_COMMIT}" \
      org.opencontainers.image.created="${AWFACE_BUILD_DATE}" \
      org.opencontainers.image.source="https://github.com/novaintegral/awface-certiface"

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:5500
ENV AWFACE_VERSION=${AWFACE_VERSION}
ENV AWFACE_BUILD_COMMIT=${AWFACE_BUILD_COMMIT}
ENV AWFACE_BUILD_DATE=${AWFACE_BUILD_DATE}
EXPOSE 5500

ENTRYPOINT ["dotnet", "AWFace.Api.dll"]

