# -- build stage
# stage name: build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY basicapp/basicapp.csproj basicapp/
RUN dotnet restore "basicapp/basicapp.csproj"
COPY . .
RUN dotnet publish "basicapp/basicapp.csproj" -c Release -o /app --no-restore

# -- runtime stage
# stage name: base
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base 
WORKDIR /app
# copy from the build stage above to the runtime stage
COPY --from=build /app .
# asp image ships as non-root user, so we need to change ownership of the app folder
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "basicapp.dll"]