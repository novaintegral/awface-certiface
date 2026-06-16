FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

COPY backend/AWFace.Api/AWFace.Api.csproj backend/AWFace.Api/
RUN dotnet restore backend/AWFace.Api/AWFace.Api.csproj

COPY backend/AWFace.Api backend/AWFace.Api
RUN dotnet publish backend/AWFace.Api/AWFace.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0

WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "AWFace.Api.dll"]

