# Fimple ATM Nakit Yönetimi — Ayrıntılı Yol Haritası

Bu plan `Project_Assignment_Software_Developer.pdf` içindeki A1–A12, B1–B10, teknik gereksinimler ve teslim maddelerini kapsar. Dokümandaki şartlar gereksinimdir; aşağıdaki belirsizlik kararları bizim varsayımlarımızdır.

## 1. Hedef ve teslim ölçütü

Birden fazla ATM'yi yöneten ASP.NET Core REST API. Arayüz yok. PostgreSQL, EF Core code-first ve migration, Docker Compose, Swagger, ProblemDetails, health endpoint, otomatik testler ve sıralı Postman senaryosu teslim edilir.

Başarı: temiz veritabanıyla `docker compose up --build` çalışır; migration otomatik uygulanır; 8 banknot kabul testi ve gerçek PostgreSQL entegrasyon testleri geçer; Postman akışı tamamlanır; A ve B maddeleri izlenebilir testlere ve endpoint'lere bağlanır.

## 2. Mimari

- .NET 10 LTS; ASP.NET Core; PostgreSQL 17; EF Core/Npgsql 10.
- Domain: para birimleri, ATM durumu, kaset kuralları, stokla sınırlı banknot seçim algoritması. Veritabanına bağımlı olmaz.
- Infrastructure: DbContext, migration, hareket defteri ve veritabanı kilitleri.
- API: DTO'lar, REST endpoint'leri, iş akışları, ProblemDetails, hosted service, OpenAPI.
- Tests: saf algoritma/boundary testleri ve gerçek PostgreSQL kullanan HTTP entegrasyon testleri.
- Tek deploy edilen uygulama. İş kurallarını gereksiz mikroservislere bölmeyiz.

## 3. Belirsizlikler için kararlar

1. Günlük limit UTC takvim gününe göre, kart ve para birimi bazında bütün ATM ağında paylaşılır. Süresi dolan teklif serbest bırakılır; onay ancak teklif oluşturulduğu gün içinde yapılabilir.
2. Kart kimliği HMAC-SHA256 ile türetilir; ham kart numarası veritabanına/loglara yazılmaz. Anahtar environment üzerinden alınır. Bu proje gerçek kart verisiyle kullanım için sertifikalı bir ödeme sistemi değildir.
3. Alt/üst alternatifler birlikte sunulursa kaset bazında iki seçeneğin banknot ihtiyaçlarının maksimumu rezerve edilir. Böylece ikisi de gerçekleştirilebilir; ortak banknotlar iki kez sayılmaz. Günlük limit rezervasyonu en büyük sunulan tutardır. Onayda müşteri alternatif kimliğini seçer; kullanılmayan rezervasyon bırakılır.
4. Uygun alt tutar yoksa null döner; sıfır tutarlı para çekimi sunulmaz. En az banknot kuralı alternatiflere de uygulanır. Eşit sonuçlarda yüksek kupür ağırlıklı, deterministik dağılım kullanılır.
5. İki aynı recycling kasetinden çekimde yüksek doluluk oranlı olan önce kullanılır; eşitlik kaset sıra numarasıyla çözülür. Yatırmada en az dolu uygun kaset önce doldurulur.
6. Bakım durumuna geçerken aktif teklifler iptal edilerek nakit ve limit rezervasyonları bırakılır. Onay bekleyen mutabakat varken hizmete dönüş reddedilir.
7. Mutabakat eşiği kaset/kupür farklarının mutlak TRY değerlerinin toplamıdır. Artı/eksi farklar birbirini gizlemez. 1.000 TRY'nin üzeri ikinci kullanıcı onayı ister; tam 1.000 istemez.
8. Fiziksel sayım tamamlanınca düzeltme hareketleri ve bakiyeler aynı transaction'da kaydedilir. İkinci onay gerektiren durumda bu kayıtlar korunur, ATM MAINTENANCE'ta kalır; onay yalnızca hizmete dönüş engelini kaldırır. Fark hesabında kur anlık görüntüsü kaydedilir.
9. Deposit-only kasette karma para/kupür olduğundan mutabakat her kasette para/kupür bazında adet içerir. Recycling için yalnızca yapılandırılmış para/kupür kabul edilir.
10. Düşük nakit uyarısı eşik geçişinde üretilir. İlk kez düşük hâle gelmesi de geçiş sayılır; stok düzelince yeniden uyarı üretilebilir. Yalnızca recycling kasetleri izlenir.
11. Kaset hareketleri fiziksel adet değişimi ve rezerve adet değişimini ayrı taşır. Güncel toplam ve rezervasyon hareketlerden yeniden kurulabilir. Hareket kayıtları güncellenmez/silinmez; uygulama ve veritabanı koruması bulunur.
12. Idempotency-Key ağ genelinde benzersizdir. Aynı anahtar/farklı payload 409 döner; aynı anahtar/aynı başarılı confirm önceki cevabı döndürür. Başarısız istekler başarılı confirm kaydı oluşturmaz.
13. ATM para birimi bazında stok tutar; karma para değerleri yalnızca tanımlı kurlarla TRY toplamına çevrilir. Kur seed'i vaka içindir, canlı kur değildir.
14. Negatif/sıfır çekim, kesirli banknot tutarı, desteklenmeyen para/kupür, negatif adet ve kapasite aşımı reddedilir. Sıfır adet içeren stok verisi geçerlidir.

## 4. Aşama 0 — Ortam ve proje iskeleti

- [ ] Yerel .NET 10 SDK, Docker motoru ve paket erişimini doğrula.
- [ ] Solution ve dört proje oluştur; project reference'larını düzenle.
- [ ] Nullable, deterministik derleme, kod stili, gitignore ve SDK pinning ekle.
- [ ] PostgreSQL + API compose, Dockerfile, health endpoint, Swagger ve hata modeli kur.
- [ ] İlk EF migration ve başlangıç kurlarını hazırla.
- Çıkış ölçütü: API derlenir; temiz veritabanında açılır; health ve Swagger cevap verir.

## 5. Aşama 1 — ATM ve kaset kuralları (A1–A8, B1–B2)

- [ ] ATM oluştur/listele/get ve durum geçişleri.
- [ ] Tam 5 kaset, bir deposit-only, dört recycling; kapasite 100.
- [ ] TRY/USD/EUR ve 20/50/100/500 doğrulamaları.
- [ ] Bir veya iki döviz recycling kaseti; aynı kupürlü iki kaset desteği.
- [ ] İlk konfigürasyon ve nakit yüklemesi; bakımda konfigürasyon/yükle/boşalt.
- [ ] Nakit veya rezervasyon varken kaset türü değişimini engelle.
- [ ] Her işlem için LOAD/UNLOAD ledger kayıtları.
- Çıkış ölçütü: geçerli kurulum hizmete alınır; yanlış kurulum/durum/kapasite istekleri anlamlı hata verir.

## 6. Aşama 2 — Banknot algoritması (A9, B3)

- [ ] Stokla sınırlı dinamik programlama: erişilebilir tutarlar ve minimum banknot sayısı.
- [ ] Tam çözüm, en yakın alt/üst erişilebilir tutarlar.
- [ ] Günlük limit nedeniyle üst çözümün elenmesi.
- [ ] 8 zorunlu kabul testi birebir ekle.
- [ ] Stok yok, 0/negatif tutar, 500'ün olmadığı durumlar, eşit çözüm ve küçük stok brute-force karşılaştırması.
- Çıkış ölçütü: 8 test geçer; stok aşılmaz; küçük örneklerde exhaustive oracle ile minimum doğrulanır.

## 7. Aşama 3 — Teklif, rezervasyon, limit ve onay (A10, B4–B6)

- [ ] Quote/alternative/reservation ve kart günlük kullanım modeli.
- [ ] 60 saniye geçerlilik; expiry service + her işlemde gecikmiş rezervasyon temizliği.
- [ ] PostgreSQL transaction-scoped advisory lock: kart → ATM sıralaması. Aynı kartın farklı ATM istekleri limiti aşamaz; aynı ATM stokları çakışmaz.
- [ ] Alternatiflerin rezervasyon birleşimi; fiziksel stok/rezerve/kullanılabilir ayrımı.
- [ ] Confirm ve cancel; seçilmeyen banknotları bırakma.
- [ ] Idempotent başarılı confirm; farklı payload çatışması.
- [ ] UTC gün sınırı ve expired/cancelled teklif reddi.
- [ ] Gerçek DB ile paralel aynı ATM, paralel aynı kart/farklı ATM ve çift confirm testleri.
- Çıkış ölçütü: hiçbir stok/limit negatif olmaz; duplicate confirm tek debit yaratır; expire/cancel kaynakları geri bırakır.

## 8. Aşama 4 — Para yatırma (A11, B7)

- [ ] Para/kupür/adet ve unrecognisedCount DTO'su.
- [ ] Önce uygun recycling, sonra deposit-only, sonra iade.
- [ ] Aynı kupürlü kasetler için deterministik dağıtım.
- [ ] Kabul edilen banknotlar için DEPOSIT hareketi; iadeler nakit hesabına girmez.
- [ ] Kaset bazında allocations ve returned listesini döndür.
- [ ] Karma para, tam dolu kaset, kısmi kabul, tanınmayan banknot ve yanlış durum testleri.
- Çıkış ölçütü: giren adet = kabul + iade; hiçbir kaset 100'ü aşmaz.

## 9. Aşama 5 — Ledger ve mutabakat (A8, B8–B9)

- [ ] Hareket kaydını append-only yap; SQL trigger ile UPDATE/DELETE engelle.
- [ ] Consistency endpoint: banknot stokları ve rezervasyonları ledger toplamıyla karşılaştır.
- [ ] Tüm kasetlerin fiziksel sayımını gir; farkların detaylarını oluştur.
- [ ] Kur tablosu, kur snapshot'ı ve mutlak TRY toplamı.
- [ ] 1.000 altı/eşit/üstü akışları; farklı X-User onayı.
- [ ] Pending approval sırasında hizmete dönüş ve yeni mutabakat engeli.
- [ ] Ledger bozulması tespiti ve SQL seviyesinde immutable koruması testleri.
- Çıkış ölçütü: mutabakat farkları izlenebilir; aynı kişi onaylayamaz; bütün operasyonlardan sonra consistency temizdir.

## 10. Aşama 6 — Rapor ve uyarılar (A12, B10)

- [ ] ATM ve kaset raporları; denomination, physical/reserved/available counts.
- [ ] Para birimi toplamları ve TRY genel toplamı.
- [ ] 10% altına geçişi izle; aynı transaction'da outbox event.
- [ ] Hosted service log + sent timestamp; hata hâlinde yeniden işleme.
- [ ] Aynı olay loglandıktan sonra servis durursa tekrar loglanabileceğini (at-least-once) belgeleyerek event ID ekle.
- [ ] Eşik 10/9, tekrar uyarı, rezervasyon/serbest bırakma ve outbox transaction testleri.
- Çıkış ölçütü: uyarı kaybolmaz; rapor toplamları kasetlerle tutarlıdır.

## 11. Aşama 7 — Teslim ve doğrulama

- [ ] Swagger'da örnek DTO'lar, header'lar ve hata kodları.
- [ ] Postman collection ve environment; otomatik ID aktarımı ve response assertion'ları.
- [ ] Temiz compose kurulumunda bütün uçtan uca senaryo.
- [ ] Release build + tüm unit/integration testleri.
- [ ] README: çalıştırma, test, mimari gerekçeler, varsayımlar, sınırlamalar, AI beyanı.
- [ ] Gereksinim izlenebilirlik tablosunda her A/B maddesine endpoint/test bağla.
- [ ] Git repository ve kullanıcıya sunulabilir ZIP.
- Çıkış ölçütü: başka bir geliştirici README ile projeyi ayağa kaldırıp senaryoyu çalıştırabilir.

## 12. Planlanan API

| İşlem | Endpoint |
|---|---|
| ATM oluştur/listele/get | POST/GET /api/atms, GET /api/atms/{id} |
| Kaset konfigürasyonu | PUT /api/atms/{id}/configuration |
| Durum geçişi | POST /api/atms/{id}/state |
| Nakit yükle/boşalt | POST /api/atms/{id}/load, /unload |
| Teklif | POST /api/atms/{id}/quotes |
| Onay/iptal | POST /api/quotes/{id}/confirm, /cancel |
| Yatırma | POST /api/atms/{id}/deposits |
| Sayım | POST /api/atms/{id}/reconciliations |
| İkinci onay | POST /api/reconciliations/{id}/approve |
| Rapor | GET /api/atms/{id}/report, GET /api/reports |
| Defter kontrolü | GET /api/atms/{id}/consistency |
| Hareketler | GET /api/atms/{id}/movements |
| Kurlar | GET /api/exchange-rates |
| Sağlık | GET /health |

## 13. Doğrulama disiplini

Her aşama sonunda ilgili testleri çalıştırıp sonucu kaydederiz. Yazılmış fakat çalıştırılmamış testleri geçmiş kabul etmeyiz. Altyapı engeli varsa hangi testlerin koşamadığını belirtiriz. Nihai teslimde tamamlanmamış hiçbir gereksinimi tamamlanmış diye işaretlemeyiz.

## 14. Çalışma kaydı

- 2026-10-12: PDF gereksinimleri analiz edildi; tüm kapsamın yol haritası ve belirsizlik kararları yazıldı. Çalışma klasörü boş. Yerel SDK 9.0.305; .NET 10 kurulumu başlatıldı. Docker Desktop başlatılması denendi; veritabanı doğrulaması bekliyor.
- 2026-10-12: Kullanıcı atomik ilerleme istedi. Ayrı kabul ölçütlü 32 iş `WORK_ITEMS.md` içinde tanımlandı. İlk çalışma kapsamı proje temeli + domain kuralları + banknot algoritması ve ATM kurulum koduyla sınırlandırıldı. SDK 10.0.401 kuruldu. 26 unit test geçti (8 zorunlu B3 dahil). Release build sıfır hata/uyarı. Gerçek PostgreSQL ve compose akışı named-pipe erişimi nedeniyle doğrulanmadı; G05–G06 sıradaki atomik adım.
