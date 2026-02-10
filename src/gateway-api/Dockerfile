FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY ["gateway-api.csproj", "."]
RUN dotnet restore -r linux-musl-x64
COPY . .
RUN dotnet publish -c Release -o /app/publish \
    --no-restore \
    -r linux-musl-x64 \
    --self-contained true \
    -p:PublishTrimmed=true \
    -p:TrimMode=partial

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["./gateway-api"]
