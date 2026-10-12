# Atomik İş Listesi

Kullanıcının isteği: bütün vaka tek seferde tamamlanmayacak. Bir iş, kabul ölçütü ve doğrulamasıyla kapatılır; sonra ayrı adımda sıradakine geçilir. Büyük aşamalar `ROADMAP.md` içindedir.

Durumlar: **Doğrulandı**, **Kodlandı / doğrulama bekliyor**, **Bekliyor**.

| ID | Atomik iş | Kabul ölçütü | Durum |
|---|---|---|---|
| G01 | Yol haritası ve kapsam/varsayımlar | Bütün A/B, teknik ve teslim maddeleri planlandı | Doğrulandı |
| G02 | .NET solution, proje referansları ve paketler | Release build, sıfır hata/uyarı | Doğrulandı |
| G03 | Banknot algoritması | PDF 8 test + bağımsız stok oracle'ı geçer | Doğrulandı |
| G04 | Kaset ve durum domain kuralları | Para/kupür/döviz ve geçiş unit testleri geçer | Doğrulandı |
| G05 | EF model ve ilk migration | Gerçek PostgreSQL'de temiz migration ve seed | Verified on PostgreSQL 17 |
| G06 | ATM oluştur/konfigüre/yükle/hizmete al | Gerçek DB üzerinden HTTP akışı + rollback/kapasite testleri | Verified with PostgreSQL 17 HTTP integration test |
| G07 | Bakım/boşalt/yeniden yapılandır | Yanlış durumda ret; fiziksel stok/movement tutarlılığı | Verified with PostgreSQL 17 maintenance integration test |
| G08 | Ledger immutable ve consistency | SQL update/delete reddi, doğru/bozuk stok tespiti | Verified with PostgreSQL 17 ledger and consistency integration test |
| G09 | Docker, Swagger, ProblemDetails, health | Temiz compose ve gerçek HTTP doğrulaması | Verified with clean Docker Compose and HTTP integration test |
| G10 | Quote/alternative/reservation modeli | Migration ve reservation toplam kuralları | Verified with PostgreSQL 17 migration and reservation rule tests |
| G11 | Kart kimliği ve günlük limit modeli | Ham kart saklanmaz; UTC/ağ genelinde limit | Verified with HMAC, UTC, and PostgreSQL network limit tests |
| G12 | Quote endpoint | Tam/alt/üst ve kaset bazında birleşik rezervasyon | Verified with PostgreSQL 17 quote and reservation HTTP integration test |
| G13 | Aynı kupürlü kasetlerden çekim | Daha dolu kaset önce; kapasite/stok aşılmaz | Verified with PostgreSQL 17 duplicate-cassette quote integration test |
| G14 | Confirm | Seçilen alternatif debit; kalan rezervasyon bırakılır | Verified with PostgreSQL 17 confirmation and release integration tests |
| G15 | Confirm idempotency | Duplicate aynı response/tek debit; farklı payload 409 | Verified with concurrent PostgreSQL 17 confirmation replay and conflict tests |
| G16 | Cancel | Nakit ve kart limiti rezervasyonu bırakılır | Bekliyor |
| G17 | Expiry hosted service | 60 saniye sonra otomatik bırakma; geç onay reddi | Bekliyor |
| G18 | Bakım ve teklifler | Bakıma geçiş aktif teklifleri iptal eder | Bekliyor |
| G19 | Eş zamanlı ATM teklif testi | Paralel HTTP istekleri aynı banknotu iki kez ayıramaz | Bekliyor |
| G20 | Eş zamanlı kart/ağ limiti testi | Farklı ATM'lerde aynı kartın toplamı limiti aşmaz | Bekliyor |
| G21 | Yatırma ve iade | Recycling → deposit-only → iade; tanınmayan hariç | Bekliyor |
| G22 | Yatırma sınır testleri | Karma para/duplicate kaset/tam dolu/kısmi kabul | Bekliyor |
| G23 | Mutabakat kayıt ve fark hesabı | Her kaset/para/kupür sayımı; kur snapshot'ı | Bekliyor |
| G24 | İkinci kişi onayı | >1000 TRY bekler; aynı kullanıcı ret; hizmete dönüş kilidi | Bekliyor |
| G25 | Mutabakat sınır testleri | 999/1000/1001, artı/eksi fark, onay akışı | Bekliyor |
| G26 | Ağ ve ATM raporu | Fiziksel/rezerve/kullanılabilir; kurla TRY toplamı | Bekliyor |
| G27 | Düşük nakit outbox üretimi | 10/9 sınırı; işlemle aynı transaction | Bekliyor |
| G28 | Outbox hosted service | Olay loglanır/sent; hata sonrası retry | Bekliyor |
| G29 | Postman collection/environment | ID aktarımı ve bütün sıralı senaryo | Bekliyor |
| G30 | Hata ve API sözleşmesi incelemesi | Null/invalid/header/durum için ProblemDetails | Bekliyor |
| G31 | Temiz Docker ve tam test koşusu | Release + tüm unit/integration + E2E geçer | Bekliyor |
| G32 | README, izlenebilirlik ve teslim | Gerçek durum, varsayımlar, AI beyanı, Git/ZIP | Bekliyor |

## Sonraki atomik adım

G05–G06: gerçek PostgreSQL'i erişilebilir hâle getir; migration/seed'i doğrula; ATM oluştur → yapılandır → yükle → hizmete al akışını HTTP entegrasyon testiyle çalıştır. Quote geliştirmesine bu temel doğrulanmadan başlanmayacak.
