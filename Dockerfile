# syntax=docker/dockerfile:1
# 分阶段构建：SDK 镜像编译，chiseled ASP.NET 运行镜像只留应用文件。
# 不复制 Viewer / Tests / testdata，镜像里没有 Avalonia、没有测试样本、没有临时文件写入。
FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

# 先只拷 csproj 做 restore，源码变动不失效依赖层缓存。
COPY SimdCodecs.Core/SimdCodecs.Core.csproj SimdCodecs.Core/
COPY SimdCodecs.Processing/SimdCodecs.Processing.csproj SimdCodecs.Processing/
COPY SimdCodecs.Api/SimdCodecs.Api.csproj SimdCodecs.Api/
RUN dotnet restore SimdCodecs.Api/SimdCodecs.Api.csproj

COPY SimdCodecs.Core/ SimdCodecs.Core/
COPY SimdCodecs.Processing/ SimdCodecs.Processing/
COPY SimdCodecs.Api/ SimdCodecs.Api/
RUN dotnet publish SimdCodecs.Api/SimdCodecs.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
WORKDIR /app
COPY --from=build /app ./

# chiseled 镜像无 shell、无 curl，故不写 HEALTHCHECK；探活由宿主机 curl /api/ready 完成。
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    ApiRuntime__StructuredLog=true
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "SimdCodecs.Api.dll"]
