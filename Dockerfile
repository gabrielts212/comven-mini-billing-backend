FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Comven.slnx ./
COPY src ./src
COPY tests ./tests

RUN dotnet restore Comven.slnx
RUN dotnet publish src/Comven.Api/Comven.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update \
	&& apt-get install -y --no-install-recommends libgssapi-krb5-2 \
	&& rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish ./

EXPOSE 8080

ENTRYPOINT ["dotnet", "Comven.Api.dll"]