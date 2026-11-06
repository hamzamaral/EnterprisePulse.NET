# EnterprisePulse .NET

EnterprisePulse ERP'nin ASP.NET Core 10 API + Blazor (MudBlazor) web ile yeniden yazımı.

## Çalıştırma

```bash
docker compose up -d                       # Postgres 5442, RustFS 9010/9011, Mailpit 1035/8035
dotnet run --project src/EnterprisePulse.Api --launch-profile http   # http://localhost:5100 (Swagger: /swagger)
dotnet run --project src/EnterprisePulse.Web --launch-profile http   # http://localhost:5101
```

API açılışta migration uygular ve demo verisini yükler.

| Kullanıcı | Şifre |
| --- | --- |
| admin@enterprisepulse.dev | Admin123! |
| manager@enterprisepulse.dev | Manager123! |
| employee@enterprisepulse.dev | Employee123! |

## Yapı

- `src/EnterprisePulse.Core`: varlıklar (üretilmiş), EF Core, kimlik, yetki, iş akışı, dosya, bildirim, genel CRUD, modül servisleri
- `src/EnterprisePulse.Api`: Minimal API uç noktaları, JWT, Hangfire, SignalR
- `src/EnterprisePulse.Web`: Blazor Server arayüzü; metadata'dan üretilen liste/form/detay ekranları
- `tools/codegen`: Prisma şemasından varlık, enum ve `metadata.json` üretimi (`node tools/codegen/emit-csharp.mjs`)
- `tests`: birim ve entegrasyon testleri (`dotnet test`, Postgres 5442 gerekir)
