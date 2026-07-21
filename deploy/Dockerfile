# syntax=docker/dockerfile:1

# The API is published on the host before Buildx runs. This avoids executing
# Grpc.Tools' Linux ARM protoc binary under Colima, while the framework-dependent
# publish output remains valid for both Linux runtime architectures.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.8 AS runtime
WORKDIR /app

# Public TLS terminates at the load balancer. WebSockets use HTTP/1.1 on 8080;
# native gRPC uses cleartext HTTP/2 on 8081 inside the VPC.
ENV ASPNETCORE_ENVIRONMENT=Production \
    Kestrel__Endpoints__Web__Url=http://0.0.0.0:8080 \
    Kestrel__Endpoints__Web__Protocols=Http1 \
    Kestrel__Endpoints__Grpc__Url=http://0.0.0.0:8081 \
    Kestrel__Endpoints__Grpc__Protocols=Http2

COPY artifacts/mutualgpu-api/ ./

EXPOSE 8080 8081
USER app
ENTRYPOINT ["dotnet", "MutualGPU.Api.dll"]
