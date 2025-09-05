# STAGE 1: build
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# copia il file csproj e ripristina i pacchetti
COPY *.csproj ./
RUN dotnet restore

# copia tutto e pubblica
COPY . .
RUN dotnet publish -c Release -o /app

# STAGE 2: runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "LupusInTabula.dll"]
