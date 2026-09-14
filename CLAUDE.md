# IAMS-backend

.NET / ASP.NET Core API for the Inventory and Asset Management System. C#, EF Core, SQL Server.

**Architecture: Vertical Slice Architecture.** Organize by feature, not by technical layer — no
`Controllers/`, `Services/`, `Repositories/`, `Models/` split. Each feature slice owns its own
endpoint, request/response contract, handler, and validator:

```text
Features/
└── Orders/
    ├── CreateOrder/
    │   ├── Endpoint.cs
    │   ├── Command.cs
    │   ├── Handler.cs
    │   ├── Validator.cs
    │   └── Response.cs
    └── GetOrder/
        ├── Endpoint.cs
        ├── Query.cs
        ├── Handler.cs
        └── Response.cs
```

This repo is currently an empty scaffold (only `LICENSE`/`README.md`). Whatever structure the first
real feature establishes becomes the convention for everything after it — keep it feature-cohesive
and avoid introducing generic/shared services or repository wrappers unless a slice genuinely needs
to share logic with another.

See the workspace-level `../CLAUDE.md` for the Agent Teams setup this repo is part of.
