FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Fixture.csproj ./
COPY Program.cs.txt ./Program.cs
RUN dotnet publish -c Release -o /out
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out/ ./
USER 1654:1654
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "Fixture.dll"]
