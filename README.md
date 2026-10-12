# ATM Cash Management Backend

Fimple software developer vaka çalışması. ASP.NET Core REST API + PostgreSQL + EF Core code-first. **Geliştirme devam ediyor; henüz bütün vaka tamamlanmadı.**

## Mevcut durum

İlk atomik çalışma: proje temeli, ATM kurulum akışı ve banknot algoritması.

- Domain / Infrastructure / API / Tests solution oluşturuldu.
- ATM oluşturma, 5 kaset konfigürasyonu, döviz kaseti kuralı, elle nakit yükleme/boşaltma ve durum geçişleri kodlandı.
- LOAD/UNLOAD hareket kayıtları, stok raporu ve ledger tutarlılık kontrolü kodlandı.
- Stokla sınırlı minimum banknot algoritması ve PDF'deki 8 kabul senaryosu kodlandı.
- OpenAPI/Swagger UI, ProblemDetails, PostgreSQL healthcheck, Docker Compose ve otomatik migration başlangıcı hazırlandı.
- Veritabanı ve Docker akışının çalıştığı henüz doğrulanmadı. Ayrıntılı doğrulama kaydı `VERIFICATION.md` içindedir.

Tam plan `ROADMAP.md`, tek tek uygulanacak atomik işler `WORK_ITEMS.md` içindedir.

## Projeyi çalıştırma

Docker Desktop'ın Linux container motorunun çalışması gerekir. Repository kökünde:

```sh
docker compose up --build
```

- API: http://localhost:5080
- Swagger: http://localhost:5080/swagger
- OpenAPI: http://localhost:5080/openapi/v1.json
- Health: http://localhost:5080/health
- PostgreSQL: localhost:55432

İlk açılışta EF migration'ları uygulanır. Compose, PostgreSQL hazır olmadan API'yi başlatmaz.

Docker ayarlarındaki kullanıcı/parola yalnızca yerel vaka çalışması içindir. Çalışma verileri `cash_data` volume'ünde kalır.

Yerel geliştirme için .NET 10 SDK gereklidir:

```sh
docker compose up -d db
dotnet run --project src/AtmCash.Api --urls http://localhost:5080
```

Bağlantı `ConnectionStrings__CashDb` environment değişkeniyle değiştirilebilir.

## Testleri çalıştırma

```sh
dotnet test -c Release
```

Mevcut 26 test yalnızca unit testtir. 8 zorunlu B3 testi, günlük limit üst alternatif sınırı, boş stok, geçersiz tutar, küçük stoklarda exhaustive search ile karşılaştırma ve kaset/durum kuralları bulunur. Gerçek PostgreSQL entegrasyon testleri bir sonraki atomik işte eklenecektir; unit testler DB gereksinimini karşılamaz.

## Mimari ve temel kararlar

- **Domain:** İş kuralları, modeller ve veritabanından bağımsız banknot algoritması.
- **Infrastructure:** EF Core model, PostgreSQL erişimi, ATM operasyonları ve transaction yönetimi.
- **API:** HTTP sözleşmeleri, DI, hatalar, OpenAPI ve healthcheck.
- **Tests:** Bağımsız oracle ile doğruluk denetimi dahil domain testleri. Sonraki adımda HTTP + gerçek PostgreSQL testleri.

Banknot seçimi bounded dynamic programming kullanır. Kupürler 10'a bölünerek durum alanı küçültülür. Her kupür için mevcut stok sınırı içinde seçenekler taranır; her erişilebilir tutarda en az banknot sayısı saklanır. Sonuç geriye doğru oluşturulur. Tam çözüm yoksa en yakın alt/üst erişilebilir tutar döner. Karmaşıklık `O(D × A × C)`; D kupür sayısı, A onluk birimlerde toplam/limit, C kupür başına stok. ATM kapasitesiyle sınırlı stok için uygulanabilir; unit test bağımsız exhaustive search ile kontrol eder.

ATM yazma işlemleri transaction içinde PostgreSQL advisory lock alır. Kilit işletim sistemi içindeki tek uygulamaya değil veritabanına bağlı olduğundan farklı API instance'ları aynı ATM için seri çalışır. Henüz çekim rezervasyonu uygulanmadığı için B6 tamamlanmış değildir.

Kaset stokları hızlı okuma için saklanır; LOAD/UNLOAD defter kayıtları aynı transaction'da eklenir. Tutarlılık kontrolü RepeatableRead snapshot'ında fiziksel ve rezerve adet toplamlarını defterle karşılaştırır. Defterin uygulama üzerinden değiştirilmesi engellenir; SQL seviyesinde koruma migration'da tanımlanır.

## Varsayımlar

Ayrıntılı ve gerekçeli varsayımlar `ROADMAP.md` bölüm 3'te kayıtlıdır. Henüz uygulanmamış akışlara ait kararlar plan niteliğindedir.

- İlk nakit yüklemesi bir istekte bütün gerekli kasetleri içerir. Recycling kasetine nakit gelince durum LOADED olur; ardından IN_SERVICE'e geçilir. Sonraki yüklemeler MAINTENANCE'ta yapılır.
- Hizmete alınmak için en az bir recycling kasetinde nakit bulunmalıdır; bütün kasetlerin dolu olması gerekmez.
- Kaset yeniden yapılandırma bütün kasetler boşken yapılır; hareket geçmişi korunur.
- Kur seed'i: TRY=1, USD=40, EUR=45. Bu değerler örnek veridir; güncel piyasa kuru değildir.
- Operasyonel nakit yükleme/boşaltmada X-User başlığı gerekir; kimlik doğrulaması uygulanmaz.

## Tamamlanmamış işler ve bilinen sınırlamalar

- Quote/confirm/cancel, 60 saniye rezervasyon, idempotency ve kart başına günlük limit.
- Kart kimliğinin HMAC ile korunması.
- Paralel çekim ve ağ genelinde kart limiti entegrasyon testleri.
- Müşteri yatırması, overflow ve iade dağılımı.
- Fiziksel mutabakat, kur snapshot'ı ve farklı kullanıcı onayı.
- Ağ raporu, düşük nakit uyarısı ve outbox hosted service.
- Gerçek DB/HTTP testleri ve temiz Docker uçtan uca doğrulaması.
- Postman collection ve environment dosyası.
- Son güvenlik/validasyon, izlenebilirlik tablosu ve teslim ZIP'i.

## AI kullanımı

OpenAI Codex; gereksinim analizi, Türkçe yol haritası, başlangıç mimarisi, kod ve test oluşturma için kullanıldı. Çıktıdaki paket sürümü uyuşmazlığı düzeltildi; banknot algoritması PDF kabul testleri ve bağımsız exhaustive search oracle'ı ile sınandı. Veritabanı doğrulaması henüz yapılmadığı için AI tarafından üretilmiş API/EF akışları tamamlanmış kabul edilmedi.
