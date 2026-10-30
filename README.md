# OBERON Proto Explorer

Blazor WebAssembly client for exploring and testing **OBERON ServiceGateway** gRPC/Protobuf APIs. Provides a visual interface for browsing procedures, inspecting RPC schemas, and testing live calls.

## Features

- **Procedure Catalog**: Browse all available gRPC services and methods
- **RPC Schema Explorer**: Inspect request/response message structures
- **Live Testing**: Execute gRPC calls against a running ServiceGateway
- **Documentation Viewer**: Inline procedure and RPC insights from JSON catalogs
- **Multi-language Guides**: Code generation examples for C#, Go, Java, JavaScript, Python
- **Protobuf Downloads**: Export .proto files and descriptor sets

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  OBERON Proto Explorer (Blazor WASM on :10150)             │
│  ├── Components/           → UI pages & reusable components │
│  ├── Services/             → gRPC client, catalogs, editors │
│  ├── Proto/                → .proto definitions             │
│  ├── Documentation/        → Procedure/RPC insight JSON     │
│  ├── Guides/               → Language-specific examples     │
│  └── wwwroot/              → Static assets, downloads       │
└─────────────────────────────────────────────────────────────┘
         │
         ▼
   ┌───────────────┐
   │ OBERON        │
   │ ServiceGateway│
   │ (gRPC)        │
   └───────────────┘
```

## Prerequisites

- .NET 10 SDK
- Running OBERON ServiceGateway (gRPC endpoint)

## Installation

```bash
git clone https://github.com/Rugents-masterpiece/oberon-proto-explorer.git
cd oberon-proto-explorer

# Configure ServiceGateway endpoint
# Edit appsettings.json or appsettings.Development.json
# "GrpcEndpoint": "http://localhost:50051"

# Run
dotnet run

# Access at http://localhost:10150
```

## Configuration (`appsettings.json`)

```json
{
  "GrpcEndpoint": "http://localhost:50051",
  "CatalogPaths": {
    "Procedures": "Documentation/procedure-docs.json",
    "RpcInsights": "Documentation/rpc-insights.json",
    "ClientGuides": "Guides/"
  }
}
```

## Project Structure

| Directory | Purpose |
|-----------|---------|
| `Components/` | Blazor pages (Home, Explorer, Guides, Layout) |
| `Services/` | gRPC client, catalogs, request editor |
| `Proto/` | Protobuf definitions (`oberon_service_grpc.proto`) |
| `Documentation/` | Procedure & RPC insight JSON catalogs |
| `Guides/` | Language-specific code generation guides |
| `wwwroot/` | Static assets, downloads, Bootstrap |

## Development

```bash
# Watch mode
dotnet watch run

# Build for production
dotnet publish -c Release -o ./publish

# Run tests (if any)
dotnet test
```

## ServiceGateway Integration

The explorer expects a running OBERON ServiceGateway with:
- gRPC reflection enabled (for schema discovery)
- Procedure catalog endpoint
- RPC insights endpoint

## Security

- No credentials in repository
- ServiceGateway endpoint configured via `appsettings.json` (not committed with real values)
- Runs on localhost by default

## License

MIT
