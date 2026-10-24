FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/Directory.Build.props ./Directory.Build.props
COPY backend/src/TPR10.PdfValidator/ ./worker/
RUN dotnet restore worker/TPR10.PdfValidator.csproj --locked-mode
RUN dotnet publish worker/TPR10.PdfValidator.csproj -c Release --no-restore -o /out
FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /out/ ./
USER 1654:1654
ENV DOTNET_EnableDiagnostics=0 DOTNET_EnableWriteXorExecute=0
ENTRYPOINT ["dotnet", "TPR10.PdfValidator.dll"]
