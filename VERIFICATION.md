# İlk Atomik Çalışma — Doğrulama Kaydı

Tarih: 12 Ekim 2026 (Europe/Istanbul).

## Doğrulananlar

- .NET SDK 10.0.401 çalışma alanındaki araç klasörüne kuruldu; işletim sisteminin SDK kurulumu değiştirilmedi.
- Bütün NuGet bağımlılıkları indirildi. EF Core/Relational paket sürümü çakışmaları giderildi.
- Solution Release derlemesi: 0 hata, 0 uyarı.
- Unit testler: 26 geçti, 0 başarısız, 0 atlandı.
- Bunların 8'i PDF'deki B3 kabul testleridir; tutarlar ve banknot dağılımları kontrol edildi.
- Bağımsız exhaustive-search testinde 30 küçük stok için 12'şer istek karşılaştırıldı. Tam/alt/üst tutarlar, minimum banknot sayısı, stok sınırı ve toplam tutar denetlendi.
- Docker Compose konfigürasyonu `docker compose config --quiet` ile doğrulandı.
- İlk EF migration ve model snapshot üretildi. Migration SQL'i üretildi; bunun gerçek PostgreSQL'e uygulanması henüz doğrulanmadı.

## Kodlanmış fakat gerçek ortamda doğrulanmamış

- ATM CRUD/konfigürasyon/nakit yükle-boşalt/durum geçişi HTTP endpoint'leri.
- PostgreSQL advisory lock ve işlemin tamamını kapsayan rollback.
- LOAD/UNLOAD ledger kayıtları; SQL immutable trigger'ları.
- Rapor, ledger consistency ve kur seed'i.
- Uygulama başlangıcında otomatik migration.
- Swagger/OpenAPI, HTTP hata modeli ve database health endpoint'i.
- Docker imajı oluşturma, API+DB başlatma ve HTTP uçtan uca akışı.

## Ortam engeli

Docker Desktop başlatıldı ancak araç oturumundan `dockerDesktopLinuxEngine` named pipe bağlantısı `permission denied` döndü. Yerel Docker bağlantısına izin isteme aracı bu özel Windows yolunu temsil edemedi. Bu yüzden PostgreSQL konteyneri başlatılmadı; gerçek DB entegrasyon testleri yazılıp çalıştırılmadı. Compose dosyasının geçerli olması, konteyner akışının test edildiği anlamına gelmez.

## Sıradaki kabul kapısı

G05–G06: gerçek PostgreSQL erişimi, temiz migration, HTTP ATM kurulum akışı, kapasite/yanlış durum/rollback ve ledger tutarlılığı entegrasyon testleri. Bu kapıdan sonra çekim rezervasyonu ayrı atomik iş olarak geliştirilecek.
