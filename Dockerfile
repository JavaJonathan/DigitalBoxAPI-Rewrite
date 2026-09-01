FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY DigitalBoxApi.csproj .
RUN dotnet restore

COPY . .
# No debug symbols in the image: nothing consumes a .pdb in production and it drops a few MB.
RUN dotnet publish -c Release -o /app --no-restore /p:DebugType=none /p:DebugSymbols=false

# Alpine runtime: ~110 MB vs ~220 MB for the Debian-based aspnet:9.0 tag, so every image
# layer in ECR and on the EC2 disk is roughly half the size. The app is fully managed
# (PdfPig, CsvHelper, Npgsql) with no native dependencies, and ICU ships in this image so
# the invariant-culture CSV/date parsing is unaffected.
FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS runtime
WORKDIR /app

COPY --from=build /app .

ENV ASPNETCORE_URLS=http://+:8080
# Explicit rather than relying on the implicit default, so appsettings.Production.json loads.
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "DigitalBoxApi.dll"]
