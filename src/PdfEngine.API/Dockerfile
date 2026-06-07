# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/PdfEngine.API/PdfEngine.API.csproj", "src/PdfEngine.API/"]
COPY ["src/PdfEngine.Application/PdfEngine.Application.csproj", "src/PdfEngine.Application/"]
COPY ["src/PdfEngine.Domain/PdfEngine.Domain.csproj", "src/PdfEngine.Domain/"]
COPY ["src/PdfEngine.Infrastructure/PdfEngine.Infrastructure.csproj", "src/PdfEngine.Infrastructure/"]
RUN dotnet restore "src/PdfEngine.API/PdfEngine.API.csproj"
COPY . .
WORKDIR "/src/src/PdfEngine.API"
RUN dotnet build "PdfEngine.API.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PdfEngine.API.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Ensure Playwright dependencies are installed
# Note: Playwright requires certain Linux packages to run headless browsers
RUN apt-get update && apt-get install -y \
    wget \
    gnupg \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

COPY --from=publish /app/publish .

# Install Playwright CLI tool and Browsers directly inside the container without relying on a project build step
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN dotnet tool install --global Microsoft.Playwright.CLI && \
    /root/.dotnet/tools/playwright install chromium --with-deps

EXPOSE 8080
ENV ASPNETCORE_URLS=http+:8080

ENTRYPOINT ["dotnet", "PdfEngine.API.dll"]
