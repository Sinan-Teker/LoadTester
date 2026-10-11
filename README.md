# LoadTester

HTTP tabanlı sistemler için yük testi aracı. Senaryolar web arayüzünden oluşturulur ve düzenlenir, testler canlı izlenir, sonuçlar geçmişte saklanır.

- **Motor:** .NET 10, `SocketsHttpHandler` (her sanal kullanıcının kendi bağlantıları ve çerezleri vardır)
- **Arayüz:** Blazor Server, tamamen C#; grafikler sunucuda SVG olarak çizilir, ek JavaScript kütüphanesi yoktur
- **Kayıt:** SQLite (`src/LoadTester.Web/App_Data/loadtester.db`)

## Çalıştırma

Gereksinim: .NET 10 SDK.

```powershell
dotnet run --project src/LoadTester.Web
```

Tarayıcıda <http://localhost:5080> adresini açın. Veritabanı ilk açılışta kendiliğinden oluşur.

Testleri çalıştırmak için:

```powershell
dotnet test
```

Yayına almak için: `dotnet publish src/LoadTester.Web -c Release -o publish` (çıktıdaki `LoadTester.Web.exe` doğrudan çalıştırılabilir; adres `--urls http://localhost:5080` ile verilebilir).

## Kullanım

1. **Senaryolar → Yeni senaryo** ile senaryoyu oluşturun.
2. **Kaydet ve başlat** ya da listede **Başlat** ile testi başlatın; canlı izleme sayfası açılır.
3. Test sürerken istek/sn, yanıt süreleri, sanal kullanıcı sayısı ve hatalar saniye saniye güncellenir. **Testi durdur** ile istediğiniz an bitirebilirsiniz.
4. Biten testler **Test geçmişi** sayfasında listelenir; **Yeniden çalıştır** senaryonun güncel hâlini tekrar çalıştırır.

### Senaryo

| Bölüm | Açıklama |
|---|---|
| Temel URL | Tam adres yazılmayan adımlarda URL bunun sonuna eklenir (`https://api.ornek.com` + `/urunler`). |
| Yük profili | Yük modeli ve aşamalar (aşağıda). |
| Değişkenler | Satır başına `ad=değer`. Adımlarda `{{ad}}` olarak kullanılır. |
| Ortak başlıklar | Satır başına `Ad: değer`; tüm adımlara eklenir, adımda aynı adlı başlık varsa o kullanılır. |
| Adımlar | Her iterasyonda yukarıdan aşağıya sırayla çalışan HTTP istekleri. |
| İstek ayarları | Zaman aşımı, adımlar arası bekleme (rastgele en az–en çok), HTTP sürümü, yönlendirme, çerez, sertifika ve önbellek atlama ayarları. |

### Yük profili

- **Sanal kullanıcı (kapalı model):** Hedef, aynı anda çalışan kullanıcı sayısıdır. Her kullanıcı adımları baştan sona çalıştırır ve test bitene kadar tekrarlar. Sistem yavaşladıkça istek hızı da düşer.
- **Sabit hız (açık model):** Hedef, saniyede başlatılan iterasyon sayısıdır; yanıt süresinden bağımsızdır. Tek adımlı senaryoda istek/sn'ye eşittir. *Eş zamanlı kullanıcı sınırı* dolarsa yeni iterasyonlar başlatılmaz ve sonuçta "atlandı" olarak gösterilir.

Profil bir **başlangıç değeri** ve sırayla uygulanan **aşamalardan** oluşur. Her aşama hedefine önceki değerden doğrusal olarak ulaşır; süresi 0 olan aşama anında geçiş yapar. Örnekler:

| Amaç | Başlangıç | Aşamalar (süre → hedef) |
|---|---|---|
| Isınma + sabit yük | 0 | 30 sn → 50, 5 dk → 50 |
| Ani yük (spike) | 10 | 1 dk → 10, 0 sn → 200, 30 sn → 200, 0 sn → 10, 1 dk → 10 |
| Kademeli artış (stres) | 0 | 2 dk → 100, 2 dk → 200, 2 dk → 400 |

### Adımlar

- **Metot ve URL:** Göreli (`/api/urunler`) ya da tam adres (`https://...`).
- **Başlıklar ve gövde:** Content-Type verilmezse `{` veya `[` ile başlayan gövde `application/json`, diğerleri düz metin olarak gönderilir.
- **Beklenen durum kodu:** `200`, `200-299`, `2xx`, `200,302` biçimleri kullanılabilir; boş bırakılırsa her kod kabul edilir. Varsayılan `200-399`.
- **Yanıtta aranacak metin:** Yanıt bu metni içermezse istek başarısız sayılır.
- **Değişken çıkarma:** Yanıttan bir değeri sonraki adımlarda kullanmak için:
  - *JSON yolu:* `$.data.token`, `items[0].id`, `$['ad soyad']`
  - *Düzenli ifade:* ilk yakalama grubu alınır, grup yoksa eşleşmenin tamamı (`"id":\s*(\d+)`)
  - *Yanıt başlığı:* başlık adı (`X-Request-Id`)

### Hazır ifadeler

| İfade | Değer |
|---|---|
| `{{$guid}}` | Yeni GUID |
| `{{$timestamp}}` | Unix zamanı (milisaniye) |
| `{{$randomInt(1,100)}}` | Aralıkta rastgele tam sayı (parametresiz: 0 – 2.147.483.647) |
| `{{$vu}}` | Sanal kullanıcı numarası |
| `{{$iteration}}` | O kullanıcının iterasyon numarası |

Her iterasyonun başında değişkenler yeniden hesaplanır ve çerezler sıfırlanır; böylece her iterasyon yeni bir oturum gibi davranır. Değişken çıkarma sonuçları aynı iterasyondaki sonraki adımlarda geçerlidir. Kaydederken tanımsız değişken kullanımı, hatalı URL, JSON yolu ve düzenli ifadeler denetlenir.

### Önbelleği atlama (cache-busting)

CDN, ters vekil sunucu (nginx, Varnish vb.) ya da uygulama önbelleği arkasındaki bir sistemde aynı adresin yanıtı önbellekten dönebilir; bu durumda test, sunucunun değil önbelleğin kapasitesini ölçer. **İstek ayarları → Önbelleği atla** açıldığında GET ve HEAD isteklerine:

- her istekte farklı bir sorgu parametresi eklenir: `/api/urunler?sayfa=2` → `/api/urunler?sayfa=2&_cb=9f86d081…`
- `Cache-Control: no-cache` ve `Pragma: no-cache` başlıkları gönderilir (adımda ya da ortak başlıklarda aynı adlı başlık varsa o kullanılır).

Parametrenin adı (`_cb`) değiştirilebilir: API'niz bilinmeyen parametreleri reddediyorsa kabul ettiği bir adı seçin; CDN'iniz bazı parametreleri önbellek anahtarına katmıyorsa onlardan farklı bir ad kullanın. POST, PUT gibi istekler önbelleğe alınmadığı için değiştirilmez. Yalnızca belirli adımlarda önbelleği atlamak isterseniz ayarı kapalı bırakıp o adımların URL'sine `?v={{$guid}}` ekleyebilirsiniz.

### HTTP sürümü

**İstek ayarları → HTTP sürümü** ile isteklerin hangi protokolle gönderileceği seçilir:

| Seçim | Davranış |
|---|---|
| Varsayılan | İstekler HTTP/1.1 ile gönderilir; sunucu daha yeni bir sürümü desteklese de kullanılmaz. |
| HTTP/2 | `https` adreslerde sürüm TLS el sıkışmasında (ALPN) kararlaştırılır. `http` adreslerde istek doğrudan HTTP/2 ile gönderilir; sunucunun şifresiz HTTP/2'yi (h2c) desteklemesi gerekir. |
| HTTP/3 | QUIC (UDP) üzerinden, adresteki sunucu ve bağlantı noktasına gönderilir; yalnızca `https` adreslerle kullanılabilir. İşletim sisteminin QUIC desteklemesi gerekir: Windows 11, Windows Server 2022 ya da `libmsquic` kurulu Linux. |

Seçilen sürüm zorunludur: sunucu (ya da aradaki CDN, yük dengeleyici) bu sürümü desteklemiyorsa daha düşük bir sürüme geçilmez, istek başarısız sayılır ve nedeni hata tablosunda görünür (ör. `TLS/SSL hatası: İstemci ile sunucu arasında ortak bir uygulama protokolü yok`). Böylece sonuçların gerçekten seçilen sürümü ölçtüğünden emin olursunuz.

Her sanal kullanıcının kendi bağlantısı olduğundan HTTP/2 ve HTTP/3'te de kullanıcı başına bir bağlantı açılır ve adımlar sırayla çalıştığı için bir bağlantıda aynı anda tek istek bulunur.

HTTP/3'te:

- Alınan ve gönderilen veri miktarı ölçülmez (sayaç TCP bağlantıları üzerinde çalışır); raporda "—" olarak gösterilir.
- Yanıt alınamayan bir istekten sonra o kullanıcı yeni bir bağlantıyla devam eder. .NET, QUIC bağlantısı kurulamayan bir sunucuya HTTP/3'ü bir süre kapattığından aksi hâlde sonraki istekler hiç gönderilmeden başarısız sayılırdı.

### Bir isteğin başarısız sayıldığı durumlar

Bağlantı kurulamaması, zaman aşımı, beklenmeyen durum kodu, aranan metnin bulunamaması, değişkenin çıkarılamaması ve seçilen HTTP sürümünün kullanılamaması. Hatalar adım ve neden bazında gruplanarak gösterilir.

## Ölçümler

| Ölçüm | Not |
|---|---|
| İstek, istek/sn, hata oranı | Toplamda ve adım bazında |
| Yanıt süresi: ortalama, en az, p50, p90, p95, p99, en çok | İstek gönderiminden gövdenin tamamı okunana kadar; hatalı istekler dahildir. Yüzdelikler yaklaşık %0,5 hassasiyetli bir histogramla hesaplanır. |
| Durum kodları | Adım bazında dağılım |
| Sanal kullanıcı, iterasyon, atlanan iterasyon | Sabit hız modunda eş zamanlılık |
| Alınan / gönderilen veri | Bağlantı üzerindeki bayt (başlıklar ve TLS dahil); HTTP/3'te ölçülmez |

Grafikler saniyelik örneklerden çizilir; uzun testlerde okunaklı kalması için en çok 180 noktaya ortalanır.

## Proje yapısı

```
src/LoadTester.Core          Yük motoru (arayüzden bağımsız): senaryo modeli, doğrulama, şablonlar, metrikler
src/LoadTester.Web           Blazor Server arayüzü, SQLite kayıtları, arka planda test yöneticisi
tests/LoadTester.Core.Tests  Birim testleri ve gerçek bir Kestrel sunucusuna karşı motor testleri
```

`LoadTester.Core` başka bir uygulamadan da kullanılabilir:

```csharp
var result = await new LoadTestEngine(scenario).RunAsync(
    (report, point) => Console.WriteLine($"{point.RequestsPerSecond} istek/sn"),
    cancellationToken);
```

## Notlar ve sınırlar

- Arayüzde **kimlik doğrulama yoktur**; uygulamayı yerel makinede ya da güvenilir bir ağda çalıştırın, internete açık bir sunucuya koymayın.
- Yalnızca test etme izniniz olan sistemlere yük uygulayın.
- Yükü üreten makinenin işlemci ve ağ kapasitesi sonuçları sınırlayabilir; arayüz de aynı süreçte çalışır. Çok yüksek eş zamanlılıkta (binlerce kullanıcı) işletim sisteminin bağlantı noktası sınırlarına dikkat edin.
- Aynı senaryo aynı anda yalnızca bir kez çalışır; farklı senaryolar paralel çalışabilir.
- Uygulama kapatılırken çalışan testler durdurulur ve kısmi sonuçları kaydedilir. Beklenmedik kapanışta test, sonraki açılışta "Yarıda kaldı" olarak işaretlenir.
- Senaryolar ve sonuçlar veritabanında JSON olarak saklanır; modele alan eklemek veritabanı değişikliği gerektirmez.
