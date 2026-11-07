# EnterprisePulse .NET

![.NET](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-Minimal%20API-512BD4?logo=dotnet&logoColor=white)
![Blazor](https://img.shields.io/badge/Blazor-Server-512BD4?logo=blazor&logoColor=white)
![MudBlazor](https://img.shields.io/badge/MudBlazor-UI-594AE2)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-16-4169E1?logo=postgresql&logoColor=white)
![Hangfire](https://img.shields.io/badge/Hangfire-Jobs-F85A40)

**EnterprisePulse**, çok şirketli (multi-tenant) kurumsal bir ERP sistemidir. Bu repo, NestJS / React orijinalinin **ASP.NET Core 10 + Blazor (MudBlazor)** ile yeniden yazımıdır: tek çözümde API, arka plan işleri ve web arayüzü.

| Repo | İçerik |
| --- | --- |
| **EnterprisePulse.NET** (bu repo) | ASP.NET Core API + Blazor arayüz |
| [EnterprisePulse.Java](https://github.com/hamzamaral/EnterprisePulse.Java) | Spring Boot + Vaadin yeniden yazımı |
| [EnterprisePulse](https://github.com/hamzamaral/EnterprisePulse) | NestJS / React orijinali, yol haritası, ADR |

![Gösterge paneli](docs/screenshots/dashboard.png)

## Ekranlar

| | |
| --- | --- |
| ![Giriş](docs/screenshots/login.png) | ![Gösterge paneli](docs/screenshots/dashboard.png) |
| **Giriş:** demo hesaplar, şirket / şube, açık / koyu tema | **Gösterge paneli:** yetkiye göre KPI kartları (onay, bildirim, İK, demirbaş) |
| ![Muhasebe](docs/screenshots/accounting.png) | ![Geçmiş](docs/screenshots/audit-history.png) |
| **Muhasebe:** bekleyen belgeler, tek tıkla muhasebeleştirme, mizan / bilanço | **Geçmiş:** alan etiketli, okunabilir denetim kaydı (create / update / delete) |

## Hızlı başlangıç

Gereksinimler: .NET 10 SDK, Docker.

```bash
docker compose up -d
# Postgres 5442 · RustFS 9010/9011 · Mailpit 1035/8035

dotnet run --project src/EnterprisePulse.Api --launch-profile http
# http://localhost:5100  ·  Swagger: /swagger

dotnet run --project src/EnterprisePulse.Web --launch-profile http
# http://localhost:5101
```

API açılışta migration uygular ve demo verisini yükler.

### Demo kullanıcılar

| Rol | E-posta | Şifre |
| --- | --- | --- |
| Sistem yöneticisi | `admin@enterprisepulse.dev` | `Admin123!` |
| Müdür | `manager@enterprisepulse.dev` | `Manager123!` |
| Personel | `employee@enterprisepulse.dev` | `Employee123!` |

### Yerel servisler

| Servis | Adres |
| --- | --- |
| API | http://localhost:5100 |
| Swagger | http://localhost:5100/swagger |
| Web (Blazor) | http://localhost:5101 |
| Hangfire paneli | http://localhost:5100/hangfire |
| Mailpit | http://localhost:8035 |
| RustFS (S3) | http://localhost:9011 |

### Testler

```bash
dotnet test
# Birim + entegrasyon; entegrasyon testleri Postgres 5442 ister
```

## Özellikler

- **Platform:** JWT giriş, şirket / şube değiştirme, kullanıcı · rol · izin, denetim kaydı, SignalR bildirimleri, onay kutusu, ayarlar (para birimi, kur, KDV, birim, numaralandırma, onay akışları). Arka plan işleri Hangfire + PostgreSQL ile (Redis / BullMQ yok).
- **Gösterge paneli:** yetkiye göre İK, zimmet, stok ve finans KPI’ları; personel için kalan izin, onay bekleyen zimmetler, masraflar ve görevler.
- **İnsan Kaynakları:** personel, departman, pozisyon, organizasyon şeması, izin talebi ve çok adımlı onay, **İzinlerim** / **Mesaim**, puantaj, vardiya planı.
- **Zimmet ve Demirbaş:** demirbaş kartı, QR etiket, zimmet / iade / devir, amortisman, sayım, **Zimmetlerim**.
- **Stok ve Depo:** stok durumu, ürün / varyant, lot · seri, giriş / çıkış / transfer / düzeltme, sayım.
- **Satın Alma / Satış / Finans:** teklif, sipariş, irsaliye, fatura; cari hesaplar; kasa / banka, çek · senet, ödeme / tahsilat, yaşlandırma.
- **CRM:** potansiyel müşteri, fırsat hattı, aktiviteler.
- **Masraf ve Harcırah:** self-servis formlar, onay, finans ödemesi.
- **Proje Yönetimi:** proje, ekip, görev panosu, zaman çizelgesi.
- **Bordro / İşe Alım / Performans / Eğitim:** dönem hesaplama, ücret pusulası; iş ilanı ve başvuru hattı; değerlendirme dönemi; eğitim oturumları ve uyum.
- **Destek / Sözleşme / Bütçe / Filo:** SLA’lı ticket kuyruğu, sözleşme takvimi, departman bütçesi, araç · yakıt · belge uyarıları.
- **Muhasebe / e-Fatura / Üretim:** tekdüzen hesap planı, yevmiye, mizan · bilanço · gelir tablosu; UBL-TR kuyruk simülasyonu; ürün ağacı, iş emri, MRP.

## Yapı

```
EnterprisePulse.sln
├── src/
│   ├── EnterprisePulse.Core   # varlıklar, EF Core, kimlik, yetki, iş akışı, modül servisleri
│   ├── EnterprisePulse.Api    # Minimal API, JWT, Hangfire, SignalR, Swagger
│   └── EnterprisePulse.Web    # Blazor Server + MudBlazor (metadata’dan liste / form / detay)
├── tests/
│   ├── EnterprisePulse.UnitTests
│   └── EnterprisePulse.IntegrationTests
├── tools/codegen              # Prisma şemasından C# varlık + metadata üretimi
└── docker-compose.yml
```

Kod üretimi:

```bash
node tools/codegen/emit-csharp.mjs
```

## Kardeş projeler

Aynı domain modeli üç stack’te:

| Proje | API | UI | İş kuyruğu | Portlar |
| --- | --- | --- | --- | --- |
| [EnterprisePulse](https://github.com/hamzamaral/EnterprisePulse) | NestJS | React | Redis + BullMQ | 3000 / 5173 |
| **EnterprisePulse.NET** | ASP.NET 10 | Blazor | Hangfire + Postgres | 5100 / 5101 |
| [EnterprisePulse.Java](https://github.com/hamzamaral/EnterprisePulse.Java) | Spring Boot 3.5 | Vaadin 24 | `@Scheduled` + ShedLock | 8180 / 8181 |
