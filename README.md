# Rashmatz Server (F# + Oxpecker + SQLite WAL)

## Quickstart

```powershell
# 1. Run all unit, integration, OpenAPI, and 50-parallel-write SQLite concurrency tests
dotnet test

# 2. (Optional) Initialize & seed rashmatz.db explicitly
dotnet run --project src/Rashmatz.Server.fsproj -- --seed-db

# 3. Start the server (auto-creates & seeds rashmatz.db in Development mode)
dotnet run --project src/Rashmatz.Server.fsproj
```

- **OpenAPI JSON:** `http://localhost:5000/openapi/v1.json`
- **Interactive HTTP requests:** Open `rashmatz.http` in Visual Studio, VS Code (REST Client), or Rider.
- **Schemathesis fuzz/contract tests:** Run `.\tests\run-schemathesis.ps1` while the server is running.
- **SqlHydra codegen (optional):** After running `--init-db`, run `dotnet tool restore` and `dotnet sqlhydra sqlite` to regenerate strongly-typed table records from `rashmatz.db` using `sqlhydra.toml`.
